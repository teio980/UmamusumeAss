#include "EmulatorConnector.hpp"
#include "EmulatorConnectorDetail.hpp"

#include <algorithm>
#include <cstddef>
#include <filesystem>
#include <optional>
#include <stop_token>
#include <string>
#include <thread>
#include <vector>

namespace UmaAssistant {

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
    if (!std::filesystem::exists(request.adb_path))
    {
        return ConnectionFailure{
            ConnectionErrorCode::AdbExecutableNotFound, "preflight",
            "ADB executable not found: " + request.adb_path.string()
        };
    }
    auto const ext = request.adb_path.extension().wstring();
    if (ext != L".exe" && ext != L".EXE")
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

    ConnectedDevice device;
    device.serial = request.serial;

    if (auto err = step_resolve_target(request, cancellation, on_phase))
        return ConnectionResult{*err};
    if (on_phase) on_phase("boot_poll");
    if (auto err = step_boot_poll(request, cancellation))
        return ConnectionResult{*err};
    if (on_phase) on_phase("android_id");
    if (auto err = step_android_id(request, cancellation, device))
        return ConnectionResult{*err};
    if (on_phase) on_phase("android_version");
    if (auto err = step_android_version(request, cancellation, device))
        return ConnectionResult{*err};
    if (on_phase) on_phase("wm_size");
    if (auto err = step_get_size(request, cancellation, device))
        return ConnectionResult{*err};

    return ConnectionResult{std::move(device)};
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
        if (err && err->error_code != ConnectionErrorCode::CommandFailed)
        {
            return err;
        }
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
        std::this_thread::sleep_for(timings_.boot_poll_interval);
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

} // namespace UmaAssistant
