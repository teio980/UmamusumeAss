#include "AdbCommandRunner.hpp"

#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <memory>
#include <stop_token>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

namespace UmaAssistant {
namespace {

struct IgnoreSigpipe
{
    IgnoreSigpipe() noexcept
    {
        signal(SIGPIPE, SIG_IGN);
    }
} s_ignore_sigpipe;

using Clock = std::chrono::steady_clock;

std::vector<std::string> parse_command_line(std::wstring const& cmdline)
{
    std::vector<std::string> args;
    std::string current;
    bool in_quotes = false;
    for (wchar_t wc : cmdline)
    {
        if (wc == L'"')
        {
            in_quotes = !in_quotes;
        }
        else if (wc == L' ' && !in_quotes)
        {
            if (!current.empty())
            {
                args.push_back(std::move(current));
                current.clear();
            }
        }
        else
        {
            if (wc < 0x80)
            {
                current.push_back(static_cast<char>(wc));
            }
            else
            {
                // UTF-8 encode wchar_t
                if (wc < 0x800)
                {
                    current.push_back(static_cast<char>(0xC0 | (wc >> 6)));
                    current.push_back(static_cast<char>(0x80 | (wc & 0x3F)));
                }
                else
                {
                    current.push_back(static_cast<char>(0xE0 | (wc >> 12)));
                    current.push_back(static_cast<char>(0x80 | ((wc >> 6) & 0x3F)));
                    current.push_back(static_cast<char>(0x80 | (wc & 0x3F)));
                }
            }
        }
    }
    if (!current.empty())
    {
        args.push_back(std::move(current));
    }
    return args;
}

void reap_child_process(pid_t pid, bool force_kill = false)
{
    if (pid <= 0) return;
    int status = 0;
    pid_t r = waitpid(pid, &status, WNOHANG);
    if (r == pid)
    {
        kill(-pid, SIGKILL);
        return;
    }

    if (force_kill)
    {
        kill(-pid, SIGKILL);
        waitpid(pid, &status, 0);
        return;
    }

    kill(-pid, SIGTERM);
    auto const deadline = Clock::now() + std::chrono::milliseconds(50);
    while (Clock::now() < deadline)
    {
        r = waitpid(pid, &status, WNOHANG);
        if (r == pid)
        {
            kill(-pid, SIGKILL);
            return;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
    kill(-pid, SIGKILL);
    waitpid(pid, &status, 0);
}

class PosixProcess final : public IWin32Process
{
public:
    Result execute(
        std::filesystem::path const& executable,
        std::wstring const&          command_line,
        std::stop_token              cancellation,
        std::chrono::milliseconds    timeout,
        std::size_t                  max_standard_output_bytes,
        bool                         leave_children_running_on_success) override
    {
        auto args = parse_command_line(command_line);
        // The first token in args is typically the executable name if parsed from command_line
        if (!args.empty() && args.front() == executable.string())
        {
            args.erase(args.begin());
        }
        return execute_args(executable, args, cancellation, timeout,
            max_standard_output_bytes, leave_children_running_on_success);
    }

    Result execute_args(
        std::filesystem::path const& executable,
        std::vector<std::string> const& arguments,
        std::stop_token              cancellation,
        std::chrono::milliseconds    timeout,
        std::size_t                  max_standard_output_bytes,
        bool                         leave_children_running_on_success) override
    {
        (void)leave_children_running_on_success;
        Result result{};

        std::string const exec_str = executable.string();
        std::vector<char const*> argv;
        argv.push_back(exec_str.c_str());
        for (auto const& arg : arguments)
        {
            argv.push_back(arg.c_str());
        }
        argv.push_back(nullptr);

        int out_pipe[2]{-1, -1};
        int err_pipe[2]{-1, -1};
        if (pipe(out_pipe) != 0) return result;
        if (pipe(err_pipe) != 0)
        {
            close(out_pipe[0]);
            close(out_pipe[1]);
            return result;
        }

        pid_t const pid = fork();
        if (pid < 0)
        {
            close(out_pipe[0]); close(out_pipe[1]);
            close(err_pipe[0]); close(err_pipe[1]);
            return result;
        }

        if (pid == 0)
        {
            setpgid(0, 0);
            close(out_pipe[0]);
            close(err_pipe[0]);

            dup2(out_pipe[1], STDOUT_FILENO);
            dup2(err_pipe[1], STDERR_FILENO);
            close(out_pipe[1]);
            close(err_pipe[1]);

            int const devnull = open("/dev/null", O_RDONLY);
            if (devnull >= 0)
            {
                dup2(devnull, STDIN_FILENO);
                close(devnull);
            }

            for (int fd = 3; fd < 256; ++fd) close(fd);

            execv(exec_str.c_str(), const_cast<char* const*>(argv.data()));
            _exit(127);
        }

        result.started = true;
        close(out_pipe[1]);
        close(err_pipe[1]);

        fcntl(out_pipe[0], F_SETFL, O_NONBLOCK);
        fcntl(err_pipe[0], F_SETFL, O_NONBLOCK);

        auto const deadline = Clock::now() + timeout;
        bool out_open = true;
        bool err_open = true;
        char buffer[4096];

        while (out_open || err_open)
        {
            int child_status = 0;
            pid_t const r = waitpid(pid, &child_status, WNOHANG);
            if (r == pid)
            {
                if (!leave_children_running_on_success)
                {
                    kill(-pid, SIGKILL);
                }
                while (true)
                {
                    ssize_t const bytes = read(out_pipe[0], buffer, sizeof(buffer));
                    if (bytes > 0)
                    {
                        auto const ubytes = static_cast<std::size_t>(bytes);
                        if (result.standard_output.size() + ubytes <= max_standard_output_bytes)
                            result.standard_output.append(buffer, ubytes);
                        else
                        {
                            result.standard_output_truncated = true;
                            auto const keep = max_standard_output_bytes > result.standard_output.size()
                                ? max_standard_output_bytes - result.standard_output.size() : 0;
                            if (keep > 0) result.standard_output.append(buffer, keep);
                        }
                    }
                    else break;
                }
                while (true)
                {
                    ssize_t const bytes = read(err_pipe[0], buffer, sizeof(buffer));
                    if (bytes > 0)
                        result.standard_error.append(buffer, static_cast<std::size_t>(bytes));
                    else break;
                }
                if (WIFEXITED(child_status))
                    result.exit_code = WEXITSTATUS(child_status);
                else if (WIFSIGNALED(child_status))
                    result.exit_code = 128 + WTERMSIG(child_status);
                close(out_pipe[0]);
                close(err_pipe[0]);
                return result;
            }

            if (cancellation.stop_requested())
            {
                result.canceled = true;
                reap_child_process(pid, false);
                close(out_pipe[0]);
                close(err_pipe[0]);
                return result;
            }

            auto const now = Clock::now();
            if (now >= deadline)
            {
                result.timed_out = true;
                reap_child_process(pid, false);
                close(out_pipe[0]);
                close(err_pipe[0]);
                return result;
            }

            auto const remaining_ms = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - now).count();
            int const wait_time = std::clamp(static_cast<int>(remaining_ms), 1, 50);

            pollfd fds[2]{};
            int nfds = 0;
            int out_idx = -1;
            int err_idx = -1;

            if (out_open)
            {
                out_idx = nfds++;
                fds[out_idx].fd = out_pipe[0];
                fds[out_idx].events = POLLIN;
            }
            if (err_open)
            {
                err_idx = nfds++;
                fds[err_idx].fd = err_pipe[0];
                fds[err_idx].events = POLLIN;
            }

            int const poll_res = poll(fds, static_cast<nfds_t>(nfds), wait_time);
            if (poll_res < 0 && errno == EINTR) continue;

            if (out_idx >= 0 && (fds[out_idx].revents & (POLLIN | POLLHUP | POLLERR)))
            {
                while (true)
                {
                    ssize_t const bytes = read(out_pipe[0], buffer, sizeof(buffer));
                    if (bytes > 0)
                    {
                        auto const ubytes = static_cast<std::size_t>(bytes);
                        if (result.standard_output.size() + ubytes <= max_standard_output_bytes)
                        {
                            result.standard_output.append(buffer, ubytes);
                        }
                        else
                        {
                            result.standard_output_truncated = true;
                            if (result.standard_output.size() < max_standard_output_bytes)
                            {
                                auto const keep = max_standard_output_bytes - result.standard_output.size();
                                result.standard_output.append(buffer, keep);
                            }
                        }
                    }
                    else if (bytes == 0)
                    {
                        out_open = false;
                        break;
                    }
                    else
                    {
                        if (errno == EAGAIN || errno == EWOULDBLOCK) break;
                        out_open = false;
                        break;
                    }
                }
            }

            if (err_idx >= 0 && (fds[err_idx].revents & (POLLIN | POLLHUP | POLLERR)))
            {
                while (true)
                {
                    ssize_t const bytes = read(err_pipe[0], buffer, sizeof(buffer));
                    if (bytes > 0)
                    {
                        result.standard_error.append(buffer, static_cast<std::size_t>(bytes));
                    }
                    else if (bytes == 0)
                    {
                        err_open = false;
                        break;
                    }
                    else
                    {
                        if (errno == EAGAIN || errno == EWOULDBLOCK) break;
                        err_open = false;
                        break;
                    }
                }
            }
        }

        close(out_pipe[0]);
        close(err_pipe[0]);

        int status = 0;
        waitpid(pid, &status, 0);
        if (WIFEXITED(status))
        {
            result.exit_code = WEXITSTATUS(status);
        }
        else if (WIFSIGNALED(status))
        {
            result.exit_code = 128 + WTERMSIG(status);
        }

        return result;
    }
};

class PosixInteractiveSession final : public IWin32InteractiveSession
{
public:
    PosixInteractiveSession(pid_t pid, int in_fd, int out_fd, int err_fd)
        : pid_(pid), in_fd_(in_fd), out_fd_(out_fd), err_fd_(err_fd)
    {
        fcntl(in_fd_, F_SETFL, O_NONBLOCK);
        fcntl(out_fd_, F_SETFL, O_NONBLOCK);
        fcntl(err_fd_, F_SETFL, O_NONBLOCK);
    }

    ~PosixInteractiveSession() override
    {
        close();
    }

    bool has_exited() const noexcept override
    {
        if (pid_ <= 0) return true;
        int status = 0;
        pid_t const r = waitpid(pid_, &status, WNOHANG);
        return r == pid_ || (r < 0 && errno == ECHILD);
    }

    bool write(std::string_view bytes, std::stop_token cancellation) noexcept override
    {
        if (in_fd_ < 0 || pid_ <= 0 || has_exited()) return false;
        auto const deadline = Clock::now() + std::chrono::seconds(2);
        std::size_t written = 0;
        while (written < bytes.size())
        {
            if (cancellation.stop_requested() || Clock::now() >= deadline)
            {
                close();
                return false;
            }
            pollfd pfd{};
            pfd.fd = in_fd_;
            pfd.events = POLLOUT;
            int const res = poll(&pfd, 1, 50);
            if (res < 0 && errno == EINTR) continue;
            if (res < 0)
            {
                close();
                return false;
            }
            if (res == 0) continue;

            if (pfd.revents & (POLLERR | POLLHUP | POLLNVAL))
            {
                close();
                return false;
            }

            ssize_t const count = ::write(in_fd_, bytes.data() + written, bytes.size() - written);
            if (count > 0)
            {
                written += static_cast<std::size_t>(count);
            }
            else if (count < 0)
            {
                if (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR) continue;
                close();
                return false;
            }
            else
            {
                close();
                return false;
            }
        }
        return true;
    }

    Output read(std::chrono::milliseconds timeout, std::stop_token cancellation) noexcept override
    {
        Output out{};
        if (out_fd_ < 0 && err_fd_ < 0) return out;

        auto const deadline = Clock::now() + timeout;
        char buffer[4096];

        while (true)
        {
            if (cancellation.stop_requested()) break;
            auto const now = Clock::now();
            if (now >= deadline) break;

            auto const remaining_ms = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - now).count();
            int const wait_time = std::clamp(static_cast<int>(remaining_ms), 1, 20);

            pollfd fds[2]{};
            int nfds = 0;
            int out_idx = -1;
            int err_idx = -1;

            if (out_fd_ >= 0)
            {
                out_idx = nfds++;
                fds[out_idx].fd = out_fd_;
                fds[out_idx].events = POLLIN;
            }
            if (err_fd_ >= 0)
            {
                err_idx = nfds++;
                fds[err_idx].fd = err_fd_;
                fds[err_idx].events = POLLIN;
            }

            int const res = poll(fds, static_cast<nfds_t>(nfds), wait_time);
            if (res < 0 && errno == EINTR) continue;
            if (res <= 0) break;

            bool had_data = false;
            if (out_idx >= 0 && (fds[out_idx].revents & POLLIN))
            {
                ssize_t const count = ::read(out_fd_, buffer, sizeof(buffer));
                if (count > 0)
                {
                    out.standard_output.append(buffer, static_cast<std::size_t>(count));
                    had_data = true;
                }
            }
            if (err_idx >= 0 && (fds[err_idx].revents & POLLIN))
            {
                ssize_t const count = ::read(err_fd_, buffer, sizeof(buffer));
                if (count > 0)
                {
                    out.standard_error.append(buffer, static_cast<std::size_t>(count));
                    had_data = true;
                }
            }
            if (had_data) break;
            if ((out_idx >= 0 && (fds[out_idx].revents & (POLLHUP | POLLERR))) ||
                (err_idx >= 0 && (fds[err_idx].revents & (POLLHUP | POLLERR))))
            {
                break;
            }
        }
        return out;
    }

    void close() noexcept override
    {
        if (in_fd_ >= 0) { ::close(in_fd_); in_fd_ = -1; }
        if (pid_ > 0)
        {
            auto const deadline = Clock::now() + std::chrono::milliseconds(250);
            while (Clock::now() < deadline)
            {
                int status = 0;
                pid_t const r = waitpid(pid_, &status, WNOHANG);
                if (r == pid_) break;
                std::this_thread::sleep_for(std::chrono::milliseconds(10));
            }
            reap_child_process(pid_, false);
            pid_ = -1;
        }
        if (out_fd_ >= 0) { ::close(out_fd_); out_fd_ = -1; }
        if (err_fd_ >= 0) { ::close(err_fd_); err_fd_ = -1; }
    }

private:
    pid_t pid_{-1};
    int in_fd_{-1};
    int out_fd_{-1};
    int err_fd_{-1};
};

} // namespace

std::unique_ptr<IWin32Process> create_win32_process()
{
    return std::make_unique<PosixProcess>();
}

std::unique_ptr<IWin32InteractiveSession> start_win32_interactive_session(
    std::filesystem::path const& executable,
    std::vector<std::string> const& arguments,
    std::string& error)
{
    std::string const exec_str = executable.string();
    std::vector<char const*> argv;
    argv.push_back(exec_str.c_str());
    for (auto const& arg : arguments)
    {
        argv.push_back(arg.c_str());
    }
    argv.push_back(nullptr);

    int in_pipe[2]{-1, -1};
    int out_pipe[2]{-1, -1};
    int err_pipe[2]{-1, -1};

    if (pipe(in_pipe) != 0 || pipe(out_pipe) != 0 || pipe(err_pipe) != 0)
    {
        error = "Failed to create pipes";
        if (in_pipe[0] >= 0) { close(in_pipe[0]); close(in_pipe[1]); }
        if (out_pipe[0] >= 0) { close(out_pipe[0]); close(out_pipe[1]); }
        if (err_pipe[0] >= 0) { close(err_pipe[0]); close(err_pipe[1]); }
        return nullptr;
    }

    pid_t const pid = fork();
    if (pid < 0)
    {
        error = "Failed to fork child process";
        close(in_pipe[0]); close(in_pipe[1]);
        close(out_pipe[0]); close(out_pipe[1]);
        close(err_pipe[0]); close(err_pipe[1]);
        return nullptr;
    }

    if (pid == 0)
    {
        setpgid(0, 0);
        close(in_pipe[1]);
        close(out_pipe[0]);
        close(err_pipe[0]);

        dup2(in_pipe[0], STDIN_FILENO);
        dup2(out_pipe[1], STDOUT_FILENO);
        dup2(err_pipe[1], STDERR_FILENO);

        close(in_pipe[0]);
        close(out_pipe[1]);
        close(err_pipe[1]);

        for (int fd = 3; fd < 256; ++fd) close(fd);

        execv(exec_str.c_str(), const_cast<char* const*>(argv.data()));
        _exit(127);
    }

    close(in_pipe[0]);
    close(out_pipe[1]);
    close(err_pipe[1]);

    return std::make_unique<PosixInteractiveSession>(pid, in_pipe[1], out_pipe[0], err_pipe[0]);
}

} // namespace UmaAssistant
