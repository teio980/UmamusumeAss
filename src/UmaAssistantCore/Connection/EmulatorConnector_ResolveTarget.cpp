#include "EmulatorConnector.hpp"
#include "EmulatorConnectorDetail.hpp"

#include <string>
#include <thread>

namespace UmaAssistant {

std::optional<ConnectionFailure> EmulatorConnector::step_resolve_target(
    ConnectionRequest const& request,
    std::stop_token const&   cancellation,
    PhaseCallback const&     on_phase)
{
    if (on_phase) on_phase("adb_devices");
    auto devices_inv = profile_.expand(
        request.profile_name, "list_devices", request.adb_path, request.serial);

    auto const devices_result = runner_.run(
        *devices_inv, timings_.devices, cancellation);
    {
        auto err = detail::check_runner_result(
            devices_result, "list_devices", cancellation);
        if (err) return err;
    }

    auto const entries = detail::parse_devices_output(
        devices_result.standard_output);

    detail::DeviceEntry const* target_entry = nullptr;
    for (auto const& entry : entries)
    {
        if (entry.serial == request.serial)
        {
            target_entry = &entry;
            break;
        }
    }

    if (target_entry)
    {
        if (target_entry->state == "device")
        {
            if (on_phase) on_phase("adb_get_state");
            auto state_inv = profile_.expand(
                request.profile_name, "get_state", request.adb_path, request.serial);
            if (!state_inv)
            {
                return ConnectionFailure{
                    ConnectionErrorCode::InvalidDeviceResponse, "get_state",
                    "failed to expand get_state command"
                };
            }
            auto const state_result = runner_.run(
                *state_inv, timings_.device_query, cancellation);
            auto const state_trimmed = detail::trim(state_result.standard_output);
            if (state_trimmed != "device")
            {
                return ConnectionFailure{
                    ConnectionErrorCode::InvalidDeviceResponse, "get_state",
                    "get-state returned \"" + std::string(state_trimmed)
                    + "\", expected \"device\""
                };
            }
        }
        else if (target_entry->state == "offline")
        {
            return ConnectionFailure{
                ConnectionErrorCode::DeviceOffline, "resolve_target",
                "device is offline"
            };
        }
        else if (target_entry->state == "unauthorized")
        {
            return ConnectionFailure{
                ConnectionErrorCode::DeviceUnauthorized, "resolve_target",
                "device is unauthorized; accept RSA key on device"
            };
        }
        else
        {
            return ConnectionFailure{
                ConnectionErrorCode::DeviceUnavailable, "resolve_target",
                "device state is \"" + target_entry->state + "\""
            };
        }
    }
    else
    {
        if (!detail::is_tcp_endpoint(request.serial))
        {
            return ConnectionFailure{
                ConnectionErrorCode::DeviceUnavailable, "resolve_target",
                "serial not found and is not connectable as host:port"
            };
        }

        auto connect_inv = profile_.expand(
            request.profile_name, "connect", request.adb_path, request.serial);
        if (!connect_inv)
        {
            return ConnectionFailure{
                ConnectionErrorCode::InvalidDeviceResponse, "connect",
                "failed to expand connect command"
            };
        }

        if (on_phase) on_phase("adb_connect");
        auto const connect_result = runner_.run(
            *connect_inv, timings_.connect, cancellation);

        auto const combined = connect_result.standard_output
                            + connect_result.standard_error;
        if (combined.find("connected") == std::string_view::npos
            && combined.find("already connected") == std::string_view::npos)
        {
            return ConnectionFailure{
                ConnectionErrorCode::DeviceUnavailable, "connect",
                "connect did not report success: " + combined
            };
        }

        if (on_phase) on_phase("ready_poll");
        auto const poll_start = std::chrono::steady_clock::now();
        while (true)
        {
            if (cancellation.stop_requested())
            {
                return ConnectionFailure{
                    ConnectionErrorCode::Canceled, "resolve_target",
                    "canceled during device poll after connect"
                };
            }

            auto const poll_result = runner_.run(
                *devices_inv, timings_.devices, cancellation);

            auto const poll_entries = detail::parse_devices_output(
                poll_result.standard_output);
            bool found_device = false;
            for (auto const& entry : poll_entries)
            {
                if (entry.serial == request.serial && entry.state == "device")
                {
                    found_device = true;
                    break;
                }
            }
            if (found_device) break;

            auto const elapsed = std::chrono::steady_clock::now() - poll_start;
            if (elapsed >= timings_.ready_poll_timeout)
            {
                return ConnectionFailure{
                    ConnectionErrorCode::DeviceNotReady, "resolve_target",
                    "device did not become ready after connect"
                };
            }
            std::this_thread::sleep_for(timings_.ready_poll_interval);
        }

        if (on_phase) on_phase("adb_get_state");
        auto state_inv = profile_.expand(
            request.profile_name, "get_state", request.adb_path, request.serial);
        if (!state_inv)
        {
            return ConnectionFailure{
                ConnectionErrorCode::InvalidDeviceResponse, "get_state",
                "failed to expand get_state command"
            };
        }
        auto const state_result = runner_.run(
            *state_inv, timings_.device_query, cancellation);
        auto const state_trimmed = detail::trim(state_result.standard_output);
        if (state_trimmed != "device")
        {
            return ConnectionFailure{
                ConnectionErrorCode::InvalidDeviceResponse, "get_state",
                "get-state returned \"" + std::string(state_trimmed)
                + "\" after connect"
            };
        }
    }

    return std::nullopt;
}

} // namespace UmaAssistant
