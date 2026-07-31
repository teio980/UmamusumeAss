#include "AdbCommandRunner.hpp"

#include <chrono>
#include <string>
#include <stop_token>
#include <utility>

namespace UmaAssistant {





AdbCommandResult AdbCommandRunnerWin32::run(
    AdbInvocation const&      invocation,
    std::chrono::milliseconds timeout,
    std::stop_token           cancellation)
{
    auto const cmdline = build_windows_command_line(
        invocation.executable, invocation.arguments);

    auto const proc_result = process_.execute(
        invocation.executable, cmdline, cancellation, timeout);

    AdbCommandResult result;
    result.exit_code       = proc_result.exit_code;
    result.standard_output = std::move(proc_result.standard_output);
    result.standard_error  = std::move(proc_result.standard_error);
    result.started         = proc_result.started;
    result.timed_out       = proc_result.timed_out;
    result.canceled        = proc_result.canceled;
    return result;
}

}
