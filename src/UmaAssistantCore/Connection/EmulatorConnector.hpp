#pragma once

#include "AdbCommandRunner.hpp"
#include "ConnectionProfile.hpp"
#include "UmaAssistant/Connection.hpp"

#include <chrono>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <stop_token>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

using namespace std::chrono_literals;

namespace UmaAssistant {

/// Optional callback invoked before each connection phase with the phase name.
using PhaseCallback = std::function<void(std::string_view)>;

struct ConnectionRequest
{
    std::filesystem::path adb_path;
    std::string           serial;
    std::string           profile_name{"General"};
};

struct ConnectionTimings
{
    std::chrono::milliseconds devices             = 15000ms;
    std::chrono::milliseconds connect             = 30000ms;
    std::chrono::milliseconds device_query        = 15000ms;
    std::chrono::milliseconds ready_poll_timeout  = 30000ms;
    std::chrono::milliseconds ready_poll_interval = 250ms;
    std::chrono::milliseconds boot_poll_timeout   = 60000ms;
    std::chrono::milliseconds boot_poll_interval  = 500ms;
    int                      max_attempts         = 3;
    std::chrono::milliseconds retry_interval       = 2000ms;
};

class EmulatorConnector
{
public:
    explicit EmulatorConnector(
        ConnectionProfile const& profile,
        IAdbCommandRunner&       runner,
        ConnectionTimings        timings = {}) noexcept;

    [[nodiscard]] ConnectionResult connect(
        ConnectionRequest const& request,
        std::stop_token          cancellation = {},
        PhaseCallback            on_phase    = nullptr);

    [[nodiscard]] static std::optional<ConnectionFailure> validate_request(
        ConnectionRequest const& request);

private:
    [[nodiscard]] std::optional<ConnectionFailure> step_resolve_target(
        ConnectionRequest const& request,
        std::stop_token const&   cancellation,
        PhaseCallback const&     on_phase);

    [[nodiscard]] std::optional<ConnectionFailure> step_boot_poll(
        ConnectionRequest const& request,
        std::stop_token const&   cancellation);

    [[nodiscard]] std::optional<ConnectionFailure> step_android_id(
        ConnectionRequest const& request,
        std::stop_token const&   cancellation,
        ConnectedDevice&         device);

    [[nodiscard]] std::optional<ConnectionFailure> step_android_version(
        ConnectionRequest const& request,
        std::stop_token const&   cancellation,
        ConnectedDevice&         device);

    [[nodiscard]] std::optional<ConnectionFailure> step_get_size(
        ConnectionRequest const& request,
        std::stop_token const&   cancellation,
        ConnectedDevice&         device);

    ConnectionProfile const& profile_;
    IAdbCommandRunner&       runner_;
    ConnectionTimings const  timings_;
};

} // namespace UmaAssistant
