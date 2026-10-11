#pragma once

#include "Connection/ConnectionProfile.hpp"

#include <chrono>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <memory>
#include <stop_token>
#include <string>
#include <string_view>
#include <vector>

namespace UmaAssistant {





struct AdbCommandResult
{
    int         exit_code{};
    std::string standard_output;
    std::string standard_error;
    bool        started{};
    bool        timed_out{};
    bool        canceled{};
    bool        standard_output_truncated{};
};




class IAdbCommandRunner
{
public:
    virtual ~IAdbCommandRunner() = default;

    [[nodiscard]] virtual AdbCommandResult run(
        AdbInvocation const&      invocation,
        std::chrono::milliseconds timeout,
        std::stop_token           cancellation,
        std::size_t               max_standard_output_bytes = 64 * 1024) = 0;
};




class IWin32Process
{
public:
    struct Result
    {
        int         exit_code{};
        std::string standard_output;
        std::string standard_error;
        bool        started{};
        bool        timed_out{};
        bool        canceled{};
        bool        standard_output_truncated{};
    };

    virtual ~IWin32Process() = default;

    [[nodiscard]] virtual Result execute(
        std::filesystem::path const& executable,
        std::wstring const&          command_line,
        std::stop_token              cancellation,
        std::chrono::milliseconds    timeout,
        std::size_t                  max_standard_output_bytes,
        bool                         leave_children_running_on_success) = 0;
};

class IWin32InteractiveSession
{
public:
    struct Output
    {
        std::string standard_output;
        std::string standard_error;
        bool standard_output_truncated{};
        bool standard_error_truncated{};
    };

    virtual ~IWin32InteractiveSession() = default;

    [[nodiscard]] virtual bool has_exited() const noexcept = 0;
    [[nodiscard]] virtual bool write(
        std::string_view bytes, std::stop_token cancellation) noexcept = 0;
    [[nodiscard]] virtual Output read(
        std::chrono::milliseconds timeout, std::stop_token cancellation) noexcept = 0;
    virtual void close() noexcept = 0;
};





class AdbCommandRunnerWin32 final : public IAdbCommandRunner
{
public:
    explicit AdbCommandRunnerWin32(IWin32Process& process) noexcept
        : process_{process}
    {
    }

    [[nodiscard]] AdbCommandResult run(
        AdbInvocation const&      invocation,
        std::chrono::milliseconds timeout,
        std::stop_token           cancellation,
        std::size_t               max_standard_output_bytes = 64 * 1024) override;

private:
    IWin32Process& process_;
};













[[nodiscard]] std::wstring quote_windows_argument(std::wstring_view arg);





[[nodiscard]] std::wstring build_windows_command_line(
    std::filesystem::path const& executable,
    std::vector<std::string> const& arguments);




[[nodiscard]] std::unique_ptr<IWin32Process> create_win32_process();

[[nodiscard]] std::unique_ptr<IWin32InteractiveSession> start_win32_interactive_session(
    std::filesystem::path const& executable,
    std::vector<std::string> const& arguments,
    std::string& error);

}
