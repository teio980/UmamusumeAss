#include "AdbCommandRunner.hpp"

#include <algorithm>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <condition_variable>
#include <deque>
#include <limits>
#include <memory>
#include <mutex>
#include <optional>
#include <stop_token>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <tlhelp32.h>

namespace UmaAssistant {





namespace {


struct HandleCloser
{
    void operator()(HANDLE h) noexcept
    {
        if (h && h != INVALID_HANDLE_VALUE) ::CloseHandle(h);
    }
};

using UniqueHandle = std::unique_ptr<std::remove_pointer_t<HANDLE>, HandleCloser>;




void create_inheritable_pipe(
    UniqueHandle& read_handle,
    UniqueHandle& write_handle)
{
    HANDLE r = nullptr, w = nullptr;
    SECURITY_ATTRIBUTES sa{};
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = TRUE;
    sa.lpSecurityDescriptor = nullptr;

    if (!::CreatePipe(&r, &w, &sa, 0)) {
        return;
    }

    ::SetHandleInformation(r, HANDLE_FLAG_INHERIT, 0);

    read_handle.reset(r);
    write_handle.reset(w);
}


constexpr std::size_t kMaxDiagnostics = 64 * 1024;

void terminate_process_descendants(DWORD root_pid) noexcept
{
    HANDLE snapshot = ::CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return;

    std::unordered_map<DWORD, std::vector<DWORD>> children;
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    bool found = ::Process32FirstW(snapshot, &entry) != FALSE;
    while (found)
    {
        children[entry.th32ParentProcessID].push_back(entry.th32ProcessID);
        found = ::Process32NextW(snapshot, &entry) != FALSE;
    }
    ::CloseHandle(snapshot);

    std::vector<DWORD> descendants;
    std::unordered_set<DWORD> visited{root_pid};
    std::vector<DWORD> pending{root_pid};
    while (!pending.empty())
    {
        auto const parent = pending.back();
        pending.pop_back();
        auto const it = children.find(parent);
        if (it == children.end()) continue;
        for (auto const child : it->second)
        {
            if (!visited.insert(child).second) continue;
            descendants.push_back(child);
            pending.push_back(child);
        }
    }

    for (auto it = descendants.rbegin(); it != descendants.rend(); ++it)
    {
        HANDLE process = ::OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, FALSE, *it);
        if (process == nullptr) continue;
        ::TerminateProcess(process, 1);
        ::WaitForSingleObject(process, 5000);
        ::CloseHandle(process);
    }
}

}





class RealWin32Process final : public IWin32Process
{
public:
    RealWin32Process() = default;

    RealWin32Process(RealWin32Process const&) = delete;
    RealWin32Process& operator=(RealWin32Process const&) = delete;

    IWin32Process::Result execute(
        std::filesystem::path const& executable,
        std::wstring const&          command_line,
        std::stop_token              cancellation,
        std::chrono::milliseconds    timeout,
        std::size_t                  max_standard_output_bytes,
        bool                         leave_children_running_on_success) override;

private:


    static void drain_pipe(
        HANDLE              pipe_handle,
        HANDLE              process_handle,
        std::string&        output,
        std::size_t         limit,
        std::atomic<bool>&  truncated,
        bool                stop_when_parent_exits);
};

namespace {

constexpr std::size_t kMaxInteractiveReadBuffer = 64 * 1024;
constexpr std::size_t kMaxInteractiveWriteQueue = 2 * 1024 * 1024;
constexpr auto kInteractiveWriteTimeout = std::chrono::seconds{2};

void create_stdin_pipe(UniqueHandle& child_read, UniqueHandle& parent_write)
{
    HANDLE read_handle = nullptr;
    HANDLE write_handle = nullptr;
    SECURITY_ATTRIBUTES attributes{};
    attributes.nLength = sizeof(attributes);
    attributes.bInheritHandle = TRUE;
    if (!::CreatePipe(&read_handle, &write_handle, &attributes, 0)) return;
    if (!::SetHandleInformation(write_handle, HANDLE_FLAG_INHERIT, 0))
    {
        ::CloseHandle(read_handle);
        ::CloseHandle(write_handle);
        return;
    }
    child_read.reset(read_handle);
    parent_write.reset(write_handle);
}

class Win32InteractiveSession final : public IWin32InteractiveSession
{
public:
    Win32InteractiveSession(
        HANDLE process,
        HANDLE job,
        DWORD process_id,
        HANDLE stdin_write,
        HANDLE stdout_read,
        HANDLE stderr_read)
        : process_{process},
          job_{job},
          process_id_{process_id},
          stdin_write_{stdin_write},
          stdout_read_{stdout_read},
          stderr_read_{stderr_read},
          stdout_reader_{[this](std::stop_token token) {
              drain_pipe(stdout_read_.get(), stdout_buffer_, stdout_truncated_, token);
          }},
          stderr_reader_{[this](std::stop_token token) {
              drain_pipe(stderr_read_.get(), stderr_buffer_, stderr_truncated_, token);
          }},
          writer_{[this](std::stop_token token) { write_pipe(token); }}
    {
    }

    ~Win32InteractiveSession() override { close(); }

    [[nodiscard]] bool has_exited() const noexcept override
    {
        return !process_ || ::WaitForSingleObject(process_.get(), 0) == WAIT_OBJECT_0;
    }

    [[nodiscard]] bool write(
        std::string_view bytes, std::stop_token cancellation) noexcept override
    {
        if (bytes.empty()) return true;
        if (cancellation.stop_requested() || has_exited()) return false;
        std::shared_ptr<WriteRequest> request;
        try
        {
            request = std::make_shared<WriteRequest>();
            request->bytes.assign(bytes);
            std::lock_guard lock{mutex_};
            if (closing_ || writer_failed_
                || request->bytes.size() > kMaxInteractiveWriteQueue - queued_write_bytes_)
                return false;
            write_queue_.push_back(request);
            queued_write_bytes_ += request->bytes.size();
            condition_.notify_all();
        }
        catch (...)
        {
            return false;
        }

        auto const deadline = std::chrono::steady_clock::now() + kInteractiveWriteTimeout;
        for (;;)
        {
            bool abort_session = false;
            bool request_completed = false;
            bool request_succeeded = false;
            {
                std::unique_lock lock{mutex_};
                if (request->completed)
                {
                    request_completed = true;
                    request_succeeded = request->succeeded;
                }
                else if (cancellation.stop_requested()
                    || std::chrono::steady_clock::now() >= deadline)
                {
                    closing_ = true;
                    for (auto const& pending : write_queue_)
                    {
                        pending->completed = true;
                        pending->succeeded = false;
                    }
                    write_queue_.clear();
                    queued_write_bytes_ = 0;
                    request->completed = true;
                    request->succeeded = false;
                    condition_.notify_all();
                    abort_session = true;
                }
                else if (!request_completed)
                {
                    condition_.wait_for(lock, std::chrono::milliseconds{10});
                }
            }
            if (request_completed)
            {
                if (!request_succeeded) close();
                return request_succeeded;
            }
            if (abort_session)
            {
                // A partial protocol write cannot be rolled back safely. Kill
                // the helper process tree so it cannot apply stale queued input.
                close();
                return false;
            }
        }
    }

    [[nodiscard]] Output read(
        std::chrono::milliseconds timeout, std::stop_token cancellation) noexcept override
    {
        try
        {
            auto const deadline = std::chrono::steady_clock::now()
                + (timeout < std::chrono::milliseconds::zero()
                    ? std::chrono::milliseconds::zero() : timeout);
            std::unique_lock lock{mutex_};
            while (stdout_buffer_.empty() && stderr_buffer_.empty()
                && !has_exited() && !cancellation.stop_requested())
            {
                if (condition_.wait_until(lock, deadline) == std::cv_status::timeout) break;
            }
            Output result;
            result.standard_output.swap(stdout_buffer_);
            result.standard_error.swap(stderr_buffer_);
            result.standard_output_truncated = stdout_truncated_;
            result.standard_error_truncated = stderr_truncated_;
            return result;
        }
        catch (...)
        {
            return {};
        }
    }

    void close() noexcept override
    {
        std::lock_guard close_lock{close_mutex_};
        if (cleanup_completed_) return;

        {
            std::lock_guard lock{mutex_};
            closing_ = true;
            for (auto const& pending : write_queue_)
            {
                pending->completed = true;
                pending->succeeded = false;
            }
            write_queue_.clear();
            queued_write_bytes_ = 0;
        }
        condition_.notify_all();

        if (process_ && ::WaitForSingleObject(process_.get(), 250) != WAIT_OBJECT_0)
        {
            if (job_) ::TerminateJobObject(job_.get(), 1);
            else
            {
                terminate_process_descendants(process_id_);
                ::TerminateProcess(process_.get(), 1);
            }
            ::WaitForSingleObject(process_.get(), 2000);
        }

        if (writer_.joinable())
        {
            writer_.request_stop();
            (void)::CancelSynchronousIo(writer_.native_handle());
        }
        if (stdout_reader_.joinable()) stdout_reader_.request_stop();
        if (stderr_reader_.joinable()) stderr_reader_.request_stop();
        condition_.notify_all();
        if (writer_.joinable()) writer_.join();
        if (stdout_reader_.joinable()) stdout_reader_.join();
        if (stderr_reader_.joinable()) stderr_reader_.join();

        stdin_write_.reset();
        stdout_read_.reset();
        stderr_read_.reset();
        if (job_) ::TerminateJobObject(job_.get(), 0);
        job_.reset();
        process_.reset();
        cleanup_completed_ = true;
    }

private:
    struct WriteRequest
    {
        std::string bytes;
        bool started{};
        bool completed{};
        bool succeeded{};
    };

    void drain_pipe(
        HANDLE pipe,
        std::string& target,
        bool& truncated,
        std::stop_token cancellation) noexcept
    {
        if (!pipe) return;
        char bytes[4096];
        std::optional<DWORD> remaining_after_exit;
        while (!cancellation.stop_requested())
        {
            if (!remaining_after_exit && has_exited())
            {
                DWORD available = 0;
                if (!::PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr)) break;
                remaining_after_exit = available;
                if (available == 0) break;
            }

            DWORD available = 0;
            if (remaining_after_exit)
                available = *remaining_after_exit;
            else if (!::PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr))
                break;
            if (available == 0)
            {
                if (has_exited()) break;
                ::Sleep(5);
                continue;
            }

            DWORD read_bytes = 0;
            auto const requested = (std::min)(available, static_cast<DWORD>(sizeof(bytes)));
            if (!::ReadFile(pipe, bytes, requested, &read_bytes, nullptr) || read_bytes == 0) break;
            {
                std::lock_guard lock{mutex_};
                auto const room = target.size() < kMaxInteractiveReadBuffer
                    ? kMaxInteractiveReadBuffer - target.size() : 0;
                auto const copied = (std::min)(room, static_cast<std::size_t>(read_bytes));
                target.append(bytes, copied);
                if (copied != read_bytes) truncated = true;
            }
            condition_.notify_all();
            if (remaining_after_exit)
            {
                *remaining_after_exit -= (std::min)(*remaining_after_exit, read_bytes);
                if (*remaining_after_exit == 0) break;
            }
        }
        condition_.notify_all();
    }

    void write_pipe(std::stop_token cancellation) noexcept
    {
        for (;;)
        {
            std::shared_ptr<WriteRequest> request;
            {
                std::unique_lock lock{mutex_};
                condition_.wait(lock, [&] {
                    return closing_ || !write_queue_.empty() || cancellation.stop_requested();
                });
                if (cancellation.stop_requested()) return;
                if (write_queue_.empty() && closing_)
                {
                    stdin_write_.reset(); // EOF lets `minitouch -i` finish cleanly.
                    return;
                }
                if (write_queue_.empty()) continue;
                request = std::move(write_queue_.front());
                write_queue_.pop_front();
                queued_write_bytes_ -= request->bytes.size();
                request->started = true;
            }

            std::size_t offset = 0;
            while (offset < request->bytes.size() && !cancellation.stop_requested())
            {
                DWORD written = 0;
                auto const requested = static_cast<DWORD>((std::min)(
                    request->bytes.size() - offset,
                    static_cast<std::size_t>((std::numeric_limits<DWORD>::max)())));
                if (!stdin_write_ || !::WriteFile(stdin_write_.get(),
                        request->bytes.data() + offset,
                        requested, &written, nullptr) || written == 0)
                {
                    std::lock_guard lock{mutex_};
                    writer_failed_ = true;
                    closing_ = true;
                    request->completed = true;
                    request->succeeded = false;
                    for (auto const& pending : write_queue_)
                    {
                        pending->completed = true;
                        pending->succeeded = false;
                    }
                    write_queue_.clear();
                    queued_write_bytes_ = 0;
                    condition_.notify_all();
                    return;
                }
                offset += written;
            }
            {
                std::lock_guard lock{mutex_};
                request->completed = true;
                request->succeeded = offset == request->bytes.size()
                    && !cancellation.stop_requested();
            }
            condition_.notify_all();
        }
    }

    UniqueHandle process_;
    UniqueHandle job_;
    DWORD process_id_{};
    UniqueHandle stdin_write_;
    UniqueHandle stdout_read_;
    UniqueHandle stderr_read_;
    std::mutex mutex_;
    std::mutex close_mutex_;
    std::condition_variable condition_;
    std::deque<std::shared_ptr<WriteRequest>> write_queue_;
    std::size_t queued_write_bytes_{};
    std::string stdout_buffer_;
    std::string stderr_buffer_;
    bool stdout_truncated_{};
    bool stderr_truncated_{};
    bool writer_failed_{};
    bool closing_{};
    bool cleanup_completed_{};
    std::jthread stdout_reader_;
    std::jthread stderr_reader_;
    std::jthread writer_;
};

}





IWin32Process::Result RealWin32Process::execute(
    std::filesystem::path const& executable,
    std::wstring const&          command_line,
    std::stop_token              cancellation,
    std::chrono::milliseconds    timeout,
    std::size_t                  max_standard_output_bytes,
    bool                         leave_children_running_on_success)
{
    Result result;


    if (!std::filesystem::exists(executable)) {
        return result;
    }
    auto const ext = executable.extension().wstring();
    if (ext != L".exe" && ext != L".EXE") {
        return result;
    }


    UniqueHandle stdout_read, stdout_write;
    UniqueHandle stderr_read, stderr_write;
    create_inheritable_pipe(stdout_read, stdout_write);
    create_inheritable_pipe(stderr_read, stderr_write);

    if (!stdout_read || !stderr_read) {
        return result;
    }


    std::wstring mutable_cmdline = command_line;

    STARTUPINFOW si{};
    si.cb        = sizeof(si);
    si.dwFlags   = STARTF_USESTDHANDLES;
    si.hStdInput  = ::GetStdHandle(STD_INPUT_HANDLE);
    si.hStdOutput = stdout_write.get();
    si.hStdError  = stderr_write.get();

    PROCESS_INFORMATION pi{};

    auto const creation_ok = ::CreateProcessW(
        executable.c_str(),
        mutable_cmdline.data(),
        nullptr,
        nullptr,
        TRUE,
        CREATE_SUSPENDED | CREATE_NO_WINDOW,
        nullptr,
        nullptr,
        &si,
        &pi);

    if (!creation_ok) {
        return result;
    }


    UniqueHandle hProcess{pi.hProcess};
    UniqueHandle hThread{pi.hThread};
    result.started = true;


    stdout_write.reset();
    stderr_write.reset();


    UniqueHandle hJob;
    if (!leave_children_running_on_success)
        hJob.reset(::CreateJobObjectW(nullptr, nullptr));
    if (hJob) {
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION jeli{};
        jeli.BasicLimitInformation.LimitFlags =
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        bool job_ok = ::SetInformationJobObject(
            hJob.get(),
            JobObjectExtendedLimitInformation,
            &jeli,
            sizeof(jeli)) != FALSE;
        if (job_ok) {
            job_ok = ::AssignProcessToJobObject(hJob.get(), pi.hProcess) != FALSE;
        }
        if (!job_ok) {
            hJob.reset();
        }
    }


    ::ResumeThread(hThread.get());


    std::string captured_stdout;
    std::string captured_stderr;
    std::atomic<bool> stdout_truncated{false};
    std::atomic<bool> stderr_truncated{false};

    std::jthread t1{[&](std::stop_token) {
        drain_pipe(stdout_read.get(), hProcess.get(), captured_stdout,
                   max_standard_output_bytes, stdout_truncated,
                   leave_children_running_on_success);
    }};
    std::jthread t2{[&](std::stop_token) {
        drain_pipe(stderr_read.get(), hProcess.get(), captured_stderr,
                   kMaxDiagnostics, stderr_truncated,
                   leave_children_running_on_success);
    }};


    UniqueHandle hCancelEvent{::CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    std::stop_callback cancel_callback{cancellation, [&hCancelEvent]() {
        if (hCancelEvent) ::SetEvent(hCancelEvent.get());
    }};


    DWORD const timeout_ms = (timeout.count() < 0)
        ? INFINITE
        : static_cast<DWORD>(timeout.count());

    DWORD wait_ret;
    if (hCancelEvent) {
        HANDLE wait_handles[2] = { hProcess.get(), hCancelEvent.get() };
        wait_ret = ::WaitForMultipleObjects(2, wait_handles, FALSE, timeout_ms);
    } else {
        wait_ret = ::WaitForSingleObject(hProcess.get(), timeout_ms);
    }

    bool const timed_out = (wait_ret == WAIT_TIMEOUT);
    bool const canceled  = (wait_ret == WAIT_OBJECT_0 + 1);

    if (timed_out || canceled) {
        if (hJob) {
            ::TerminateJobObject(hJob.get(), 1);
        } else {
            terminate_process_descendants(pi.dwProcessId);
            ::TerminateProcess(hProcess.get(), 1);
        }
        ::WaitForSingleObject(hProcess.get(), INFINITE);
    } else if (wait_ret == WAIT_OBJECT_0) {
        // Ordinary commands must not leave descendants behind. The one
        // persistent mode is used only for the first `adb devices` call,
        // which may cold-start the reusable local ADB server.
        if (hJob) {
            ::TerminateJobObject(hJob.get(), 0);
        } else if (!leave_children_running_on_success) {
            terminate_process_descendants(pi.dwProcessId);
        }
    }


    t1.join();
    t2.join();


    DWORD exit_code = 0;
    ::GetExitCodeProcess(hProcess.get(), &exit_code);


    result.exit_code       = static_cast<int>(exit_code);
    result.standard_output = std::move(captured_stdout);
    result.standard_error  = std::move(captured_stderr);
    result.timed_out       = timed_out;
    result.canceled        = canceled;
    result.standard_output_truncated = stdout_truncated.load(std::memory_order_relaxed);

    return result;
}





void RealWin32Process::drain_pipe(
    HANDLE              pipe_handle,
    HANDLE              process_handle,
    std::string&        output,
    std::size_t         limit,
    std::atomic<bool>&  truncated,
    bool                stop_when_parent_exits)
{
    char buf[4096];
    DWORD bytes_read = 0;
    std::optional<DWORD> drain_after_exit;

    output.reserve((std::min)(limit, sizeof(buf)));

    for (;;) {
        if (stop_when_parent_exits && !drain_after_exit
            && ::WaitForSingleObject(process_handle, 0) == WAIT_OBJECT_0)
        {
            DWORD available = 0;
            if (!::PeekNamedPipe(pipe_handle, nullptr, 0, nullptr, &available, nullptr)) break;
            drain_after_exit = available;
            if (*drain_after_exit == 0) break;
        }

        DWORD available = 0;
        if (drain_after_exit)
        {
            available = *drain_after_exit;
        }
        else if (!::PeekNamedPipe(pipe_handle, nullptr, 0, nullptr, &available, nullptr))
        {
            break;
        }
        if (available == 0) {
            if (::WaitForSingleObject(process_handle, 0) == WAIT_OBJECT_0) break;
            ::Sleep(5);
            continue;
        }
        DWORD const requested = static_cast<DWORD>((std::min)(
            static_cast<std::size_t>(available), sizeof(buf)));
        if (!::ReadFile(pipe_handle, buf, requested, &bytes_read, nullptr) || bytes_read == 0) break;

        auto const remaining = limit > output.size() ? limit - output.size() : 0;
        auto const to_copy = (std::min)(static_cast<std::size_t>(bytes_read), remaining);
        output.append(buf, to_copy);
        if (to_copy < static_cast<std::size_t>(bytes_read))
            truncated.store(true, std::memory_order_relaxed);
        if (drain_after_exit)
        {
            *drain_after_exit -= (std::min)(*drain_after_exit, bytes_read);
            if (*drain_after_exit == 0) break;
        }
    }
}





std::unique_ptr<IWin32Process> create_win32_process()
{
    return std::make_unique<RealWin32Process>();
}

std::unique_ptr<IWin32InteractiveSession> start_win32_interactive_session(
    std::filesystem::path const& executable,
    std::vector<std::string> const& arguments,
    std::string& error)
{
    error.clear();
    if (!std::filesystem::exists(executable)
        || (executable.extension().wstring() != L".exe"
            && executable.extension().wstring() != L".EXE"))
    {
        error = "Interactive ADB executable was not found.";
        return nullptr;
    }

    UniqueHandle child_stdin, parent_stdin;
    UniqueHandle parent_stdout, child_stdout;
    UniqueHandle parent_stderr, child_stderr;
    create_stdin_pipe(child_stdin, parent_stdin);
    create_inheritable_pipe(parent_stdout, child_stdout);
    create_inheritable_pipe(parent_stderr, child_stderr);
    if (!child_stdin || !parent_stdin || !parent_stdout || !child_stdout
        || !parent_stderr || !child_stderr)
    {
        error = "Could not create interactive ADB pipes.";
        return nullptr;
    }

    std::wstring command_line;
    try
    {
        command_line = build_windows_command_line(executable, arguments);
    }
    catch (...)
    {
        error = "Interactive ADB arguments were invalid.";
        return nullptr;
    }

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = child_stdin.get();
    startup.hStdOutput = child_stdout.get();
    startup.hStdError = child_stderr.get();
    PROCESS_INFORMATION process{};
    if (!::CreateProcessW(executable.c_str(), command_line.data(), nullptr, nullptr,
            TRUE, CREATE_SUSPENDED | CREATE_NO_WINDOW, nullptr, nullptr,
            &startup, &process))
    {
        error = "Could not start the interactive ADB process (Win32 error "
            + std::to_string(::GetLastError()) + ").";
        return nullptr;
    }

    UniqueHandle process_handle{process.hProcess};
    UniqueHandle thread_handle{process.hThread};
    UniqueHandle job{::CreateJobObjectW(nullptr, nullptr)};
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!job || !::SetInformationJobObject(job.get(), JobObjectExtendedLimitInformation,
            &limits, sizeof(limits))
        || !::AssignProcessToJobObject(job.get(), process_handle.get()))
    {
        ::TerminateProcess(process_handle.get(), 1);
        ::WaitForSingleObject(process_handle.get(), 1000);
        error = "Could not isolate the interactive ADB process tree.";
        return nullptr;
    }
    if (::ResumeThread(thread_handle.get()) == static_cast<DWORD>(-1))
    {
        ::TerminateJobObject(job.get(), 1);
        ::WaitForSingleObject(process_handle.get(), 1000);
        error = "Could not resume the interactive ADB process.";
        return nullptr;
    }

    child_stdin.reset();
    child_stdout.reset();
    child_stderr.reset();
    thread_handle.reset();
    return std::make_unique<Win32InteractiveSession>(
        process_handle.release(), job.release(), process.dwProcessId,
        parent_stdin.release(), parent_stdout.release(), parent_stderr.release());
}

}
