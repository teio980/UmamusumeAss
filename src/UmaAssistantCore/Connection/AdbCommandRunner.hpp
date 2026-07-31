#pragma once

#include "Connection/ConnectionProfile.hpp"

#include <chrono>
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
};




class IAdbCommandRunner
{
public:
    virtual ~IAdbCommandRunner() = default;

    [[nodiscard]] virtual AdbCommandResult run(
        AdbInvocation const&      invocation,
        std::chrono::milliseconds timeout,
        std::stop_token           cancellation) = 0;
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
    };

    virtual ~IWin32Process() = default;

    [[nodiscard]] virtual Result execute(
        std::filesystem::path const& executable,
        std::wstring const&          command_line,
        std::stop_token              cancellation,
        std::chrono::milliseconds    timeout) = 0;
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
        std::stop_token           cancellation) override;

private:
    IWin32Process& process_;
};













[[nodiscard]] std::wstring quote_windows_argument(std::wstring_view arg);





[[nodiscard]] std::wstring build_windows_command_line(
    std::filesystem::path const& executable,
    std::vector<std::string> const& arguments);




[[nodiscard]] std::unique_ptr<IWin32Process> create_win32_process();

}
