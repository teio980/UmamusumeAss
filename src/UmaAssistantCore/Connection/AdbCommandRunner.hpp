#pragma once

#include "Connection/ConnectionProfile.hpp"   // AdbInvocation

#include <chrono>
#include <cstdint>
#include <filesystem>
#include <memory>
#include <stop_token>
#include <string>
#include <string_view>
#include <vector>

namespace UmaAssistant {

// ---------------------------------------------------------------------------
// AdbCommandResult — produced by IAdbCommandRunner::run.
// Maps only runner/process state, not protocol errors.
// ---------------------------------------------------------------------------
struct AdbCommandResult
{
    int         exit_code{};
    std::string standard_output;
    std::string standard_error;
    bool        started{};
    bool        timed_out{};
    bool        canceled{};
};

// ---------------------------------------------------------------------------
// IAdbCommandRunner — injectable abstraction for executing adb invocations.
// ---------------------------------------------------------------------------
class IAdbCommandRunner
{
public:
    virtual ~IAdbCommandRunner() = default;

    [[nodiscard]] virtual AdbCommandResult run(
        AdbInvocation const&      invocation,
        std::chrono::milliseconds timeout,
        std::stop_token           cancellation) = 0;
};

// ---------------------------------------------------------------------------
// IWin32Process — low-level process-launch seam for testability.
// ---------------------------------------------------------------------------
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

// ---------------------------------------------------------------------------
// AdbCommandRunnerWin32 — production implementation using CreateProcessW.
// Accepts an injected IWin32Process seam for unit testing.
// ---------------------------------------------------------------------------
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

// ---------------------------------------------------------------------------
// Windows command-line quoting helpers (free functions for testability)
//
// Implements the reverse of CommandLineToArgvW quoting rules:
//   - Arguments containing whitespace or quotes are surrounded by "..."
//   - Backslashes preceding a quote are doubled
//   - Trailing backslashes are doubled before the closing quote
//   - Arguments without whitespace or quotes are returned as-is
// ---------------------------------------------------------------------------

/// Quotes a single argument for use in a Windows command line.
/// The result is suitable for concatenation with spaces between arguments.
[[nodiscard]] std::wstring quote_windows_argument(std::wstring_view arg);

/// Builds a complete writable command line from an executable path and
/// argument vector.  Each argument is individually quoted via
/// quote_windows_argument; arguments are separated by single spaces.
/// The executable path itself is also quoted if it contains whitespace.
[[nodiscard]] std::wstring build_windows_command_line(
    std::filesystem::path const& executable,
    std::vector<std::string> const& arguments);

/// Creates the production Win32 process implementation.
/// The caller owns the returned object and must keep it alive for the
/// lifetime of any AdbCommandRunnerWin32 that references it.
[[nodiscard]] std::unique_ptr<IWin32Process> create_win32_process();

} // namespace UmaAssistant
