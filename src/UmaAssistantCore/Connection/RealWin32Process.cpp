#include "AdbCommandRunner.hpp"

#include <algorithm>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <stop_token>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

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
        std::chrono::milliseconds    timeout) override;

private:


    static void drain_pipe(
        HANDLE              pipe_handle,
        std::string&        output,
        std::atomic<size_t>& retained);
};





IWin32Process::Result RealWin32Process::execute(
    std::filesystem::path const& executable,
    std::wstring const&          command_line,
    std::stop_token              cancellation,
    std::chrono::milliseconds    timeout)
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


    UniqueHandle hJob{::CreateJobObjectW(nullptr, nullptr)};
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
    std::atomic<size_t> total_retained{0};

    std::jthread t1{[&](std::stop_token) {
        drain_pipe(stdout_read.get(), captured_stdout, total_retained);
    }};
    std::jthread t2{[&](std::stop_token) {
        drain_pipe(stderr_read.get(), captured_stderr, total_retained);
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
            ::TerminateProcess(hProcess.get(), 1);
        }
        stdout_read.reset();
        stderr_read.reset();
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

    return result;
}





void RealWin32Process::drain_pipe(
    HANDLE              pipe_handle,
    std::string&        output,
    std::atomic<size_t>& retained)
{
    char buf[4096];
    DWORD bytes_read = 0;

    output.reserve(4096);

    while (::ReadFile(pipe_handle, buf, sizeof(buf), &bytes_read, nullptr)) {
        if (bytes_read == 0) break;

        auto const current = retained.load(std::memory_order_relaxed);
        if (current < kMaxDiagnostics) {
            auto const remaining = kMaxDiagnostics - current;
            auto const to_copy  = (std::min)(
                static_cast<std::size_t>(bytes_read), remaining);
            output.append(buf, to_copy);
            retained.fetch_add(to_copy, std::memory_order_relaxed);
        }
    }
}





std::unique_ptr<IWin32Process> create_win32_process()
{
    return std::make_unique<RealWin32Process>();
}

}
