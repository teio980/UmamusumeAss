//
// These tests verify the shape of ConnectionErrorCode, ConnectedDevice,
// ConnectionFailure, ConnectionResult, and the get_error_code accessor.
//
// Given:  The connection API contract specified in docs/superpowers.md
// When:   The compiler attempts to include UmaAssistant/Connection.hpp
// Then:   The translation unit must compile and every test below must pass.

#include <catch2/catch_test_macros.hpp>
#include "UmaAssistant/Connection.hpp"

#include <cstdint>
#include <string>
#include <type_traits>
#include <variant>

using UmaAssistant::ConnectedDevice;
using UmaAssistant::ConnectionErrorCode;
using UmaAssistant::ConnectionFailure;
using UmaAssistant::ConnectionResult;
using UmaAssistant::get_error_code;

// ==========================================================================
// ConnectionErrorCode — all 16 numeric values (Success = 0, errors 1–15)
// ==========================================================================

TEST_CASE("ConnectionErrorCode::Success is 0", "[ConnectionErrorCode][numeric]") {
    STATIC_REQUIRE(static_cast<int>(ConnectionErrorCode::Success) == 0);
}

TEST_CASE("ConnectionErrorCode::AdbExecutableNotFound is 1", "[ConnectionErrorCode][numeric]") {
    STATIC_REQUIRE(static_cast<int>(ConnectionErrorCode::AdbExecutableNotFound) == 1);
}

TEST_CASE("ConnectionErrorCode::ProcessStartFailed is 2", "[ConnectionErrorCode][numeric]") {
    STATIC_REQUIRE(static_cast<int>(ConnectionErrorCode::ProcessStartFailed) == 2);
}

TEST_CASE("ConnectionErrorCode::CommandTimedOut is 3", "[ConnectionErrorCode][numeric]") {
    STATIC_REQUIRE(static_cast<int>(ConnectionErrorCode::CommandTimedOut) == 3);
}

TEST_CASE("ConnectionErrorCode::DeviceUnauthorized is 4", "[ConnectionErrorCode][numeric]") {
    STATIC_REQUIRE(static_cast<int>(ConnectionErrorCode::DeviceUnauthorized) == 4);
}

TEST_CASE("ConnectionErrorCode::DeviceOffline is 5", "[ConnectionErrorCode][numeric]") {
    STATIC_REQUIRE(static_cast<int>(ConnectionErrorCode::DeviceOffline) == 5);
}

TEST_CASE("ConnectionErrorCode::DeviceUnavailable is 6", "[ConnectionErrorCode][numeric]") {
    STATIC_REQUIRE(static_cast<int>(ConnectionErrorCode::DeviceUnavailable) == 6);
}

TEST_CASE("ConnectionErrorCode::CommandFailed is 7", "[ConnectionErrorCode][numeric]") {
    STATIC_REQUIRE(static_cast<int>(ConnectionErrorCode::CommandFailed) == 7);
}

TEST_CASE("ConnectionErrorCode::InvalidDeviceResponse is 8", "[ConnectionErrorCode][numeric]") {
    STATIC_REQUIRE(static_cast<int>(ConnectionErrorCode::InvalidDeviceResponse) == 8);
}

TEST_CASE("ConnectionErrorCode::Canceled is 9", "[ConnectionErrorCode][numeric]") {
    STATIC_REQUIRE(static_cast<int>(ConnectionErrorCode::Canceled) == 9);
}

TEST_CASE("ConnectionErrorCode::DeviceNotReady is 10", "[ConnectionErrorCode][numeric]") {
    STATIC_REQUIRE(static_cast<int>(ConnectionErrorCode::DeviceNotReady) == 10);
}

TEST_CASE("ConnectionErrorCode::InvalidArgument is 11", "[ConnectionErrorCode][numeric]") {
    STATIC_REQUIRE(static_cast<int>(ConnectionErrorCode::InvalidArgument) == 11);
}

TEST_CASE("ConnectionErrorCode::Busy is 12", "[ConnectionErrorCode][numeric]") {
    STATIC_REQUIRE(static_cast<int>(ConnectionErrorCode::Busy) == 12);
}

TEST_CASE("ConnectionErrorCode::BootNotCompleted is 13", "[ConnectionErrorCode][numeric]") {
    STATIC_REQUIRE(static_cast<int>(ConnectionErrorCode::BootNotCompleted) == 13);
}

TEST_CASE("ConnectionErrorCode::TargetGameNotInstalled is 14", "[ConnectionErrorCode][numeric]") {
    STATIC_REQUIRE(static_cast<int>(ConnectionErrorCode::TargetGameNotInstalled) == 14);
}

TEST_CASE("ConnectionErrorCode::DeviceDisconnected is 15", "[ConnectionErrorCode][numeric]") {
    STATIC_REQUIRE(static_cast<int>(ConnectionErrorCode::DeviceDisconnected) == 15);
}

// ==========================================================================
// ConnectedDevice — struct field types and values
// ==========================================================================

TEST_CASE("ConnectedDevice::serial is std::string", "[ConnectedDevice][fields]") {
    STATIC_REQUIRE(std::is_same_v<decltype(ConnectedDevice::serial), std::string>);
}

TEST_CASE("ConnectedDevice::android_id is std::string", "[ConnectedDevice][fields]") {
    STATIC_REQUIRE(std::is_same_v<decltype(ConnectedDevice::android_id), std::string>);
}

TEST_CASE("ConnectedDevice::android_version is std::string", "[ConnectedDevice][fields]") {
    STATIC_REQUIRE(std::is_same_v<decltype(ConnectedDevice::android_version), std::string>);
}

TEST_CASE("ConnectedDevice::width is int32_t", "[ConnectedDevice][fields]") {
    STATIC_REQUIRE(std::is_same_v<decltype(ConnectedDevice::width), int32_t>);
}

TEST_CASE("ConnectedDevice::height is int32_t", "[ConnectedDevice][fields]") {
    STATIC_REQUIRE(std::is_same_v<decltype(ConnectedDevice::height), int32_t>);
}

TEST_CASE("ConnectedDevice::physical_width is int32_t", "[ConnectedDevice][fields]") {
    STATIC_REQUIRE(std::is_same_v<decltype(ConnectedDevice::physical_width), int32_t>);
}

TEST_CASE("ConnectedDevice::physical_height is int32_t", "[ConnectedDevice][fields]") {
    STATIC_REQUIRE(std::is_same_v<decltype(ConnectedDevice::physical_height), int32_t>);
}

TEST_CASE("ConnectedDevice fields hold assigned values", "[ConnectedDevice][values]") {
    ConnectedDevice dev;
    dev.serial          = "127.0.0.1:5555";
    dev.android_id      = "0123456789abcdef";
    dev.android_version = "14";
    dev.width           = 1920;
    dev.height          = 1080;
    dev.physical_width  = 1920;
    dev.physical_height = 1080;

    REQUIRE(dev.serial          == "127.0.0.1:5555");
    REQUIRE(dev.android_id      == "0123456789abcdef");
    REQUIRE(dev.android_version == "14");
    REQUIRE(dev.width           == 1920);
    REQUIRE(dev.height          == 1080);
    REQUIRE(dev.physical_width  == 1920);
    REQUIRE(dev.physical_height == 1080);
}

// ==========================================================================
// ConnectionFailure — struct field types and values
// ==========================================================================

TEST_CASE("ConnectionFailure::error_code is ConnectionErrorCode", "[ConnectionFailure][fields]") {
    STATIC_REQUIRE(std::is_same_v<decltype(ConnectionFailure::error_code), ConnectionErrorCode>);
}

TEST_CASE("ConnectionFailure::phase is std::string", "[ConnectionFailure][fields]") {
    STATIC_REQUIRE(std::is_same_v<decltype(ConnectionFailure::phase), std::string>);
}

TEST_CASE("ConnectionFailure::message is std::string", "[ConnectionFailure][fields]") {
    STATIC_REQUIRE(std::is_same_v<decltype(ConnectionFailure::message), std::string>);
}

TEST_CASE("ConnectionFailure fields hold assigned values", "[ConnectionFailure][values]") {
    ConnectionFailure fail;
    fail.error_code = ConnectionErrorCode::DeviceUnavailable;
    fail.phase      = "adb_devices";
    fail.message    = "device '127.0.0.1:5555' not found";

    REQUIRE(fail.error_code == ConnectionErrorCode::DeviceUnavailable);
    REQUIRE(fail.phase      == "adb_devices");
    REQUIRE(fail.message    == "device '127.0.0.1:5555' not found");
}

// ==========================================================================
// ConnectionResult — variant<ConnectedDevice, ConnectionFailure>
// ==========================================================================

TEST_CASE("ConnectionResult is std::variant<ConnectedDevice, ConnectionFailure>",
          "[ConnectionResult][type]") {
    STATIC_REQUIRE(std::is_same_v<ConnectionResult,
                    std::variant<ConnectedDevice, ConnectionFailure>>);
}

TEST_CASE("ConnectionResult alternative 0 is ConnectedDevice", "[ConnectionResult][type]") {
    STATIC_REQUIRE(std::is_same_v<
                    std::variant_alternative_t<0, ConnectionResult>,
                    ConnectedDevice>);
}

TEST_CASE("ConnectionResult alternative 1 is ConnectionFailure", "[ConnectionResult][type]") {
    STATIC_REQUIRE(std::is_same_v<
                    std::variant_alternative_t<1, ConnectionResult>,
                    ConnectionFailure>);
}

TEST_CASE("ConnectionResult holds ConnectedDevice when constructed from ConnectedDevice",
          "[ConnectionResult][discrimination]") {
    ConnectedDevice dev{};
    ConnectionResult const result = dev;
    REQUIRE(std::holds_alternative<ConnectedDevice>(result));
    REQUIRE_FALSE(std::holds_alternative<ConnectionFailure>(result));
}

TEST_CASE("ConnectionResult holds ConnectionFailure when constructed from ConnectionFailure",
          "[ConnectionResult][discrimination]") {
    ConnectionFailure fail{};
    ConnectionResult const result = fail;
    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE_FALSE(std::holds_alternative<ConnectedDevice>(result));
}

// ==========================================================================
// get_error_code — accessor that returns the effective error code
// ==========================================================================

TEST_CASE("get_error_code returns Success for a ConnectedDevice result",
          "[get_error_code][success]") {
    ConnectedDevice dev{};
    ConnectionResult const result = dev;
    REQUIRE(get_error_code(result) == ConnectionErrorCode::Success);
}

TEST_CASE("get_error_code returns the error_code from a ConnectionFailure result",
          "[get_error_code][failure]") {
    ConnectionFailure const fail{
        ConnectionErrorCode::DeviceUnavailable, "adb_devices", "not found"
    };
    ConnectionResult const result = fail;
    REQUIRE(get_error_code(result) == ConnectionErrorCode::DeviceUnavailable);
}

TEST_CASE("get_error_code works with every error code value",
          "[get_error_code][exhaustive]") {
    auto check = [](ConnectionErrorCode code) {
        ConnectionFailure const fail{code, "phase", "msg"};
        ConnectionResult const result = fail;
        REQUIRE(get_error_code(result) == code);
    };

    check(ConnectionErrorCode::AdbExecutableNotFound);
    check(ConnectionErrorCode::ProcessStartFailed);
    check(ConnectionErrorCode::CommandTimedOut);
    check(ConnectionErrorCode::DeviceUnauthorized);
    check(ConnectionErrorCode::DeviceOffline);
    check(ConnectionErrorCode::DeviceUnavailable);
    check(ConnectionErrorCode::CommandFailed);
    check(ConnectionErrorCode::InvalidDeviceResponse);
    check(ConnectionErrorCode::Canceled);
    check(ConnectionErrorCode::DeviceNotReady);
    check(ConnectionErrorCode::InvalidArgument);
    check(ConnectionErrorCode::Busy);
    check(ConnectionErrorCode::BootNotCompleted);
    check(ConnectionErrorCode::TargetGameNotInstalled);
    check(ConnectionErrorCode::DeviceDisconnected);
}
