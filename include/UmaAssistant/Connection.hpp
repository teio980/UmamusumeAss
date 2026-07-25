#pragma once

#include <cstdint>
#include <string>
#include <variant>

namespace UmaAssistant {

enum class ConnectionErrorCode : std::int32_t
{
    Success                 =  0,
    AdbExecutableNotFound   =  1,
    ProcessStartFailed      =  2,
    CommandTimedOut         =  3,
    DeviceUnauthorized      =  4,
    DeviceOffline           =  5,
    DeviceUnavailable       =  6,
    CommandFailed           =  7,
    InvalidDeviceResponse   =  8,
    Canceled                =  9,
    DeviceNotReady          = 10,
    InvalidArgument         = 11,
    Busy                    = 12,
    BootNotCompleted        = 13,
    TargetGameNotInstalled  = 14,
    DeviceDisconnected      = 15,
};

struct ConnectedDevice
{
    std::string  serial;
    std::string  android_id;
    std::string  android_version;
    std::int32_t width            = 0;
    std::int32_t height           = 0;
    std::int32_t physical_width   = 0;
    std::int32_t physical_height  = 0;
};

struct ConnectionFailure
{
    ConnectionErrorCode  error_code;
    std::string          phase;
    std::string          message;
};

using ConnectionResult = std::variant<ConnectedDevice, ConnectionFailure>;

[[nodiscard]] inline ConnectionErrorCode get_error_code(ConnectionResult const& result) noexcept
{
    struct overload_visitor
    {
        ConnectionErrorCode operator()(ConnectedDevice const&) const noexcept { return ConnectionErrorCode::Success; }
        ConnectionErrorCode operator()(ConnectionFailure const& failure) const noexcept { return failure.error_code; }
    };

    return std::visit(overload_visitor{}, result);
}

}
