#include "EmulatorConnector.hpp"
#include "EmulatorConnectorDetail.hpp"

#include <algorithm>
#include <chrono>
#include <cstddef>
#include <cwctype>
#include <filesystem>
#include <optional>
#include <stop_token>
#include <string>
#include <thread>
#include <vector>

namespace UmaAssistant {

namespace {

[[nodiscard]] bool wait_with_cancellation(
    std::chrono::milliseconds duration,
    std::stop_token              cancellation)
{
    if (duration <= 0ms) return !cancellation.stop_requested();

    auto const deadline = std::chrono::steady_clock::now() + duration;
    while (true)
    {
        if (cancellation.stop_requested()) return false;

        auto const remaining = std::chrono::duration_cast<std::chrono::milliseconds>(
            deadline - std::chrono::steady_clock::now());
        if (remaining <= 0ms) return true;
        std::this_thread::sleep_for(std::min(remaining, 10ms));
    }
}

[[nodiscard]] bool wait_for_poll_interval(
    std::chrono::milliseconds duration,
    std::stop_token              cancellation)
{



    return wait_with_cancellation(duration, cancellation);
}

[[nodiscard]] bool is_retryable_failure(ConnectionFailure const& failure) noexcept
{
    switch (failure.error_code)
    {
    case ConnectionErrorCode::DeviceOffline:
        return failure.phase != "resolve_target";
    case ConnectionErrorCode::DeviceNotReady:
        return true;
    case ConnectionErrorCode::ProcessStartFailed:
    case ConnectionErrorCode::CommandTimedOut:
        return failure.phase != "preflight";
    case ConnectionErrorCode::DeviceUnavailable:
        return failure.phase == "connect" || failure.phase == "ready_poll";
    case ConnectionErrorCode::CommandFailed:
        return failure.phase == "list_devices" || failure.phase == "connect"
               || failure.phase == "ready_poll" || failure.phase == "get_state"
               || failure.phase == "boot_poll" || failure.phase == "android_id"
               || failure.phase == "android_version" || failure.phase == "get_size";
    default:
        return false;
    }
}

[[nodiscard]] ConnectionFailure with_attempt_metadata(
    ConnectionFailure failure,
    int               attempt,
    int               max_attempts)
{
    failure.attempt      = attempt;
    failure.max_attempts = max_attempts;
    failure.message += " (attempt " + std::to_string(attempt) + "/"
                       + std::to_string(max_attempts) + ")";
    return failure;
}

}

EmulatorConnector::EmulatorConnector(
    ConnectionProfile const& profile,
    IAdbCommandRunner&       runner,
    ConnectionTimings        timings) noexcept
    : profile_{profile}
    , runner_{runner}
    , timings_{timings}
{
}

std::optional<ConnectionFailure> EmulatorConnector::validate_request(
    ConnectionRequest const& request)
{
    if (request.adb_path.empty())
    {
        return ConnectionFailure{
            ConnectionErrorCode::InvalidArgument, "preflight", "ADB path is empty"
        };
    }
    if (request.serial.empty())
    {
        return ConnectionFailure{
            ConnectionErrorCode::InvalidArgument, "preflight", "serial is empty"
        };
    }
    if (request.profile_name.empty())
    {
        return ConnectionFailure{
            ConnectionErrorCode::InvalidArgument, "preflight", "profile name is empty"
        };
    }
    auto const path_str = request.adb_path.string();
    if (detail::has_control_characters(path_str))
    {
        return ConnectionFailure{
            ConnectionErrorCode::InvalidArgument, "preflight",
            "ADB path contains control characters"
        };
    }
    if (detail::has_control_characters(request.serial))
    {
        return ConnectionFailure{
            ConnectionErrorCode::InvalidArgument, "preflight",
            "serial contains control characters"
        };
    }
    if (detail::has_control_characters(request.profile_name))
    {
        return ConnectionFailure{
            ConnectionErrorCode::InvalidArgument, "preflight",
            "profile name contains control characters"
        };
    }
    std::error_code file_error;
    if (!std::filesystem::is_regular_file(request.adb_path, file_error))
    {
        return ConnectionFailure{
            ConnectionErrorCode::AdbExecutableNotFound, "preflight",
            "ADB executable not found: " + request.adb_path.string()
        };
    }
    auto const ext = request.adb_path.extension().wstring();
    bool is_executable_extension = ext.size() == 4;
    if (is_executable_extension)
    {
        is_executable_extension = std::towlower(ext[0]) == L'.'
            && std::towlower(ext[1]) == L'e'
            && std::towlower(ext[2]) == L'x'
            && std::towlower(ext[3]) == L'e';
    }
    if (!is_executable_extension)
    {
        return ConnectionFailure{
            ConnectionErrorCode::AdbExecutableNotFound, "preflight",
            "ADB path is not an .exe: " + request.adb_path.string()
        };
    }
    return std::nullopt;
}

ConnectionResult EmulatorConnector::connect(
    ConnectionRequest const& request,
    std::stop_token          cancellation,
    PhaseCallback            on_phase)
{
    {
        auto err = validate_request(request);
        if (err) return ConnectionResult{*err};
    }
    if (cancellation.stop_requested())
    {
        return ConnectionResult{ConnectionFailure{
            ConnectionErrorCode::Canceled, "preflight", "canceled before connect"
        }};
    }

    auto const max_attempts = std::max(timings_.max_attempts, 1);
    for (int attempt = 1; attempt <= max_attempts; ++attempt)
    {
        if (cancellation.stop_requested())
        {
            return ConnectionResult{with_attempt_metadata(
                ConnectionFailure{
                    ConnectionErrorCode::Canceled,
                    "retry",
                    "canceled before connection attempt",
                },
                attempt,
                max_attempts)};
        }

        ConnectedDevice device;
        device.serial = request.serial;

        std::optional<ConnectionFailure> failure;
        if (auto err = step_resolve_target(request, cancellation, on_phase))
        {
            failure = std::move(*err);
        }
        else if (on_phase)
        {
            on_phase("boot_poll");
            failure = step_boot_poll(request, cancellation);
        }
        else
        {
            failure = step_boot_poll(request, cancellation);
        }

        if (!failure)
        {
            if (on_phase) on_phase("android_id");
            failure = step_android_id(request, cancellation, device);
        }
        if (!failure)
        {
            if (on_phase) on_phase("android_version");
            failure = step_android_version(request, cancellation, device);
        }
        if (!failure)
        {
            if (on_phase) on_phase("wm_size");
            failure = step_get_size(request, cancellation, device);
        }

        if (!failure) return ConnectionResult{std::move(device)};

        auto annotated = with_attempt_metadata(
            std::move(*failure), attempt, max_attempts);
        if (!is_retryable_failure(annotated) || attempt == max_attempts)
        {
            return ConnectionResult{std::move(annotated)};
        }

        if (!wait_with_cancellation(timings_.retry_interval, cancellation))
        {
            return ConnectionResult{with_attempt_metadata(
                ConnectionFailure{
                    ConnectionErrorCode::Canceled,
                    "retry",
                    "canceled before next connection attempt",
                },
                attempt,
                max_attempts)};
        }
    }

    return ConnectionResult{ConnectionFailure{
        ConnectionErrorCode::Canceled, "retry", "connection loop terminated"
    }};
}

std::optional<ConnectionFailure> EmulatorConnector::step_boot_poll(
    ConnectionRequest const& request,
    std::stop_token const&   cancellation)
{
    auto inv = profile_.expand(
        request.profile_name, "boot_completed", request.adb_path, request.serial);
    if (!inv)
    {
        return ConnectionFailure{
            ConnectionErrorCode::InvalidDeviceResponse, "boot_poll",
            "failed to expand boot_completed command"
        };
    }
    auto const poll_start = std::chrono::steady_clock::now();
    while (true)
    {
        if (cancellation.stop_requested())
        {
            return ConnectionFailure{
                ConnectionErrorCode::Canceled, "boot_poll", "canceled during boot poll"
            };
        }
        auto const result = runner_.run(*inv, timings_.device_query, cancellation);
        auto err = detail::check_runner_result(result, "boot_poll", cancellation);
        if (err) return err;
        if (detail::trim(result.standard_output) == "1")
        {
            return std::nullopt;
        }
        auto const elapsed = std::chrono::steady_clock::now() - poll_start;
        if (elapsed >= timings_.boot_poll_timeout)
        {
            return ConnectionFailure{
                ConnectionErrorCode::BootNotCompleted, "boot_poll",
                "sys.boot_completed did not return 1 within timeout"
            };
        }
        if (!wait_for_poll_interval(timings_.boot_poll_interval, cancellation))
        {
            return ConnectionFailure{
                ConnectionErrorCode::Canceled, "boot_poll", "canceled during boot poll"
            };
        }
    }
}

std::optional<ConnectionFailure> EmulatorConnector::step_android_id(
    ConnectionRequest const& request,
    std::stop_token const&   cancellation,
    ConnectedDevice&         device)
{
    auto inv = profile_.expand(
        request.profile_name, "android_id", request.adb_path, request.serial);
    if (!inv)
    {
        return ConnectionFailure{
            ConnectionErrorCode::InvalidDeviceResponse, "android_id",
            "failed to expand android_id command"
        };
    }
    auto const result = runner_.run(*inv, timings_.device_query, cancellation);
    {
        auto err = detail::check_runner_result(result, "android_id", cancellation);
        if (err) return err;
    }
    auto const id = detail::trim(result.standard_output);
    if (id.empty() || id.size() < 8 || !detail::is_hex(id))
    {
        return ConnectionFailure{
            ConnectionErrorCode::InvalidDeviceResponse, "android_id",
            "invalid android_id: \"" + std::string(id) + "\""
        };
    }
    device.android_id = std::string(id);
    return std::nullopt;
}

std::optional<ConnectionFailure> EmulatorConnector::step_android_version(
    ConnectionRequest const& request,
    std::stop_token const&   cancellation,
    ConnectedDevice&         device)
{
    auto inv = profile_.expand(
        request.profile_name, "android_version", request.adb_path, request.serial);
    if (!inv)
    {
        return ConnectionFailure{
            ConnectionErrorCode::InvalidDeviceResponse, "android_version",
            "failed to expand android_version command"
        };
    }
    auto const result = runner_.run(*inv, timings_.device_query, cancellation);
    {
        auto err = detail::check_runner_result(result, "android_version", cancellation);
        if (err) return err;
    }
    auto const ver = detail::trim(result.standard_output);
    if (ver.empty() || detail::has_control_characters(ver)
        || !std::isdigit(static_cast<unsigned char>(ver.front())))
    {
        return ConnectionFailure{
            ConnectionErrorCode::InvalidDeviceResponse, "android_version",
            "invalid android_version: \"" + std::string(ver) + "\""
        };
    }
    device.android_version = std::string(ver);
    return std::nullopt;
}

std::optional<ConnectionFailure> EmulatorConnector::step_get_size(
    ConnectionRequest const& request,
    std::stop_token const&   cancellation,
    ConnectedDevice&         device)
{
    auto inv = profile_.expand(
        request.profile_name, "get_size", request.adb_path, request.serial);
    if (!inv)
    {
        return ConnectionFailure{
            ConnectionErrorCode::InvalidDeviceResponse, "get_size",
            "failed to expand get_size command"
        };
    }
    auto const result = runner_.run(*inv, timings_.device_query, cancellation);
    {
        auto err = detail::check_runner_result(result, "get_size", cancellation);
        if (err) return err;
    }
    auto const parsed = detail::parse_wm_size_output(result.standard_output);
    if (!parsed)
    {
        return ConnectionFailure{
            ConnectionErrorCode::InvalidDeviceResponse, "get_size",
            "unable to parse wm size output"
        };
    }
    device.physical_width  = parsed->physical_width;
    device.physical_height = parsed->physical_height;
    if (parsed->has_override)
    {
        device.width  = parsed->override_width;
        device.height = parsed->override_height;
    }
    else
    {
        device.width  = parsed->physical_width;
        device.height = parsed->physical_height;
    }
    return std::nullopt;
}

}
