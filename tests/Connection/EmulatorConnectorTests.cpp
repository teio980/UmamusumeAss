







#include <catch2/catch_test_macros.hpp>

#include "Connection/ConnectionProfile.hpp"
#include "Connection/EmulatorConnector.hpp"
#include "UmaAssistant/Connection.hpp"

#include <chrono>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <optional>
#include <source_location>
#include <stop_token>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

using namespace std::chrono_literals;
using UmaAssistant::AdbCommandResult;
using UmaAssistant::AdbInvocation;
using UmaAssistant::ConnectedDevice;
using UmaAssistant::ConnectionErrorCode;
using UmaAssistant::ConnectionFailure;
using UmaAssistant::ConnectionProfile;
using UmaAssistant::ConnectionRequest;
using UmaAssistant::ConnectionResult;
using UmaAssistant::ConnectionTimings;
using UmaAssistant::EmulatorConnector;
using UmaAssistant::IAdbCommandRunner;

namespace {

namespace fs = std::filesystem;




[[nodiscard]] ConnectionProfile const& general_profile()
{
    static auto const profile = ConnectionProfile::default_profile();
    return profile;
}








class ScriptedRunner final : public IAdbCommandRunner
{
public:
    using Result = AdbCommandResult;

    explicit ScriptedRunner(std::vector<Result> results)
        : results_(std::move(results))
    {
    }

    Result run(
        AdbInvocation const&      invocation,
        std::chrono::milliseconds  ,
        std::stop_token            ) override
    {

        recorded_commands_.push_back(invocation.executable.string());
        for (auto const& arg : invocation.arguments)
        {
            recorded_commands_.push_back(arg);
        }
        ++invocation_count_;
        if (invocation_hook_) invocation_hook_(invocation);

        if (next_ < results_.size())
        {
            return results_[next_++];
        }

        return Result{};
    }


    [[nodiscard]] std::vector<std::string> arguments() const
    {
        return recorded_commands_;
    }


    [[nodiscard]] bool contains_command(std::string_view needle) const
    {
        for (auto const& arg : recorded_commands_)
        {
            if (arg == needle) return true;
        }
        return false;
    }


    [[nodiscard]] bool contains_arguments(
        std::vector<std::string> const& seq) const
    {
        if (seq.empty()) return false;
        if (seq.size() > recorded_commands_.size()) return false;

        for (std::size_t i = 0; i <= recorded_commands_.size() - seq.size(); ++i)
        {
            bool match = true;
            for (std::size_t j = 0; j < seq.size(); ++j)
            {
                if (recorded_commands_[i + j] != seq[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    }

    [[nodiscard]] std::size_t invocation_count() const
    {
        return invocation_count_;
    }

    [[nodiscard]] std::size_t count_command(std::string_view command) const
    {
        std::size_t count = 0;
        for (auto const& argument : recorded_commands_)
        {
            if (argument == command) ++count;
        }
        return count;
    }

    void set_invocation_hook(std::function<void(AdbInvocation const&)> hook)
    {
        invocation_hook_ = std::move(hook);
    }

private:
    std::vector<Result>                  results_;
    std::size_t                          next_ = 0;
    std::size_t                          invocation_count_ = 0;
    std::vector<std::string> recorded_commands_;
    std::function<void(AdbInvocation const&)> invocation_hook_;
};



[[nodiscard]] AdbCommandResult success(std::string stdout_content)
{
    return AdbCommandResult{
        .exit_code       = 0,
        .standard_output = std::move(stdout_content),
        .standard_error  = {},
        .started         = true,
        .timed_out       = {},
        .canceled        = {},
    };
}

[[nodiscard]] AdbCommandResult failure(int exit_code, std::string stderr_content)
{
    return AdbCommandResult{
        .exit_code       = exit_code,
        .standard_output = {},
        .standard_error  = std::move(stderr_content),
        .started         = true,
        .timed_out       = {},
        .canceled        = {},
    };
}

[[nodiscard]] AdbCommandResult timed_out()
{
    return AdbCommandResult{
        .exit_code       = {},
        .standard_output = {},
        .standard_error  = {},
        .started         = true,
        .timed_out       = true,
        .canceled        = {},
    };
}

[[nodiscard]] AdbCommandResult canceled()
{
    return AdbCommandResult{
        .exit_code       = {},
        .standard_output = {},
        .standard_error  = {},
        .started         = true,
        .timed_out       = {},
        .canceled        = true,
    };
}

[[nodiscard]] AdbCommandResult not_started()
{
    return AdbCommandResult{};
}





[[nodiscard]] fs::path valid_adb_path()
{
    return "C:\\Windows\\System32\\cmd.exe";
}



[[nodiscard]] ConnectionRequest default_request()
{
    return ConnectionRequest{
        .adb_path     = valid_adb_path(),
        .serial       = "127.0.0.1:5555",
        .profile_name = "General",
    };
}



[[nodiscard]] ConnectionTimings test_timings()
{

    return ConnectionTimings{
        .devices            = 5000ms,
        .connect            = 5000ms,
        .device_query       = 5000ms,
        .ready_poll_timeout = 5000ms,
        .ready_poll_interval = 10ms,
        .boot_poll_timeout  = 5000ms,
        .boot_poll_interval  = 10ms,
        .max_attempts        = 1,
        .retry_interval      = 0ms,
    };
}

[[nodiscard]] ConnectionTimings retry_timings()
{
    auto timings = test_timings();
    timings.max_attempts = 3;
    return timings;
}

}





TEST_CASE("connector rejects nonexistent ADB path", "[EmulatorConnector][preflight]")
{
    ScriptedRunner runner{{}};
    EmulatorConnector connector{general_profile(), runner, test_timings()};

    auto const result = connector.connect({
        .adb_path = R"(C:\does_not_exist\nonexistent.exe)",
        .serial   = "127.0.0.1:5555",
    });

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    auto const& fail = std::get<ConnectionFailure>(result);
    REQUIRE(fail.error_code == ConnectionErrorCode::AdbExecutableNotFound);
    REQUIRE(fail.phase == "preflight");
    REQUIRE(runner.arguments().empty());
}

TEST_CASE("connector rejects non-exe ADB path", "[EmulatorConnector][preflight]")
{
    ScriptedRunner runner{{}};
    EmulatorConnector connector{general_profile(), runner, test_timings()};

    auto const result = connector.connect({
        .adb_path = R"(C:\adb\adb.bat)",
        .serial   = "127.0.0.1:5555",
    });

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    auto const& fail = std::get<ConnectionFailure>(result);
    REQUIRE(fail.error_code == ConnectionErrorCode::AdbExecutableNotFound);
    REQUIRE(fail.phase == "preflight");
    REQUIRE(runner.arguments().empty());
}

TEST_CASE("connector rejects empty serial", "[EmulatorConnector][preflight]")
{
    ScriptedRunner runner{{}};
    EmulatorConnector connector{general_profile(), runner, test_timings()};

    auto const result = connector.connect({
        .adb_path = R"(C:\adb\adb.exe)",
        .serial   = "",
    });

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::InvalidArgument);
    REQUIRE(std::get<ConnectionFailure>(result).phase == "preflight");
    REQUIRE(runner.arguments().empty());
}

TEST_CASE("connector rejects control-character serial", "[EmulatorConnector][preflight]")
{
    ScriptedRunner runner{{}};
    EmulatorConnector connector{general_profile(), runner, test_timings()};

    auto const result = connector.connect({
        .adb_path = R"(C:\adb\adb.exe)",
        .serial   = "127.0.0.1:5555\n",
    });

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::InvalidArgument);
    REQUIRE(runner.arguments().empty());
}

TEST_CASE("connector rejects empty profile name", "[EmulatorConnector][preflight]")
{
    ScriptedRunner runner{{}};
    EmulatorConnector connector{general_profile(), runner, test_timings()};

    auto const result = connector.connect({
        .adb_path     = R"(C:\adb\adb.exe)",
        .serial       = "127.0.0.1:5555",
        .profile_name = "",
    });

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::InvalidArgument);
    REQUIRE(runner.arguments().empty());
}

TEST_CASE("connector rejects empty ADB path", "[EmulatorConnector][preflight]")
{
    ScriptedRunner runner{{}};
    EmulatorConnector connector{general_profile(), runner, test_timings()};

    auto const result = connector.connect({
        .adb_path = "",
        .serial   = "127.0.0.1:5555",
    });

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::InvalidArgument);
    REQUIRE(runner.arguments().empty());
}

TEST_CASE("connector rejects control-character in ADB path", "[EmulatorConnector][preflight]")
{
    ScriptedRunner runner{{}};
    EmulatorConnector connector{general_profile(), runner, test_timings()};

    auto const result = connector.connect({
        .adb_path = "C:\\ad\tb\\adb.exe",
        .serial   = "127.0.0.1:5555",
    });

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::InvalidArgument);
    REQUIRE(runner.arguments().empty());
}





TEST_CASE("existing device completes get-state boot identity and size handshake",
          "[EmulatorConnector][existing-device]")
{
    ScriptedRunner runner{{
        success(
            "List of devices attached\n"
            "127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        success(""),
    }};





    runner = ScriptedRunner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        success("0\n"),
        success("1\n"),
        success("0123456789abcdef\n"),
        success("14\n"),
        success("Physical size: 1920x1080\nOverride size: 1280x720\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};

    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectedDevice>(result));
    auto const& device = std::get<ConnectedDevice>(result);
    REQUIRE(device.android_id == "0123456789abcdef");
    REQUIRE(device.android_version == "14");
    REQUIRE(device.width == 1280);
    REQUIRE(device.height == 720);
    REQUIRE(device.physical_width == 1920);
    REQUIRE(device.physical_height == 1080);
    REQUIRE(device.serial == "127.0.0.1:5555");
}

TEST_CASE("existing device without override uses physical dimensions as effective",
          "[EmulatorConnector][existing-device][no-override]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        success("1\n"),
        success("0123456789abcdef\n"),
        success("14\n"),
        success("Physical size: 1080x1920\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectedDevice>(result));
    auto const& device = std::get<ConnectedDevice>(result);
    REQUIRE(device.width == 1080);
    REQUIRE(device.height == 1920);
    REQUIRE(device.physical_width == 1080);
    REQUIRE(device.physical_height == 1920);
}





TEST_CASE("missing opaque serial never invokes adb connect",
          "[EmulatorConnector][opaque-serial]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect({
        .adb_path = valid_adb_path(),
        .serial   = "emulator-5554",
    });

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    auto const& fail = std::get<ConnectionFailure>(result);
    REQUIRE(fail.error_code == ConnectionErrorCode::DeviceUnavailable);
    REQUIRE_FALSE(runner.contains_command("connect"));
}

TEST_CASE("missing USB serial never invokes adb connect",
          "[EmulatorConnector][opaque-serial]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect({
        .adb_path = valid_adb_path(),
        .serial   = "0123456789ABCDEF",
    });

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::DeviceUnavailable);
    REQUIRE_FALSE(runner.contains_command("connect"));
}





TEST_CASE("offline device returns DeviceOffline",
          "[EmulatorConnector][offline]")
{
    ScriptedRunner runner{{
        success("List of devices attached\nemulator-5554\toffline\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, retry_timings()};
    auto request = default_request();
    request.serial = "emulator-5554";
    auto const result = connector.connect(request);

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    auto const& failure_result = std::get<ConnectionFailure>(result);
    REQUIRE(failure_result.error_code == ConnectionErrorCode::DeviceOffline);
    REQUIRE(failure_result.attempt == 1);
    REQUIRE(failure_result.max_attempts == 3);
    REQUIRE(runner.invocation_count() == 1);
}

TEST_CASE("offline TCP endpoint reconnects and completes the handshake",
          "[EmulatorConnector][retry][offline]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\toffline\n"),
        success("connected to 127.0.0.1:5555\n"),
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        success("1\n"),
        success("0123456789abcdef\n"),
        success("14\n"),
        success("Physical size: 1920x1080\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, retry_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectedDevice>(result));
    REQUIRE(runner.count_command("connect") == 1);
}

TEST_CASE("transient TCP connect failure retries from target resolution",
          "[EmulatorConnector][retry][connect]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n"),
        failure(1, "failed to connect\n"),
        success("List of devices attached\n"),
        success("connected to 127.0.0.1:5555\n"),
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        success("1\n"),
        success("0123456789abcdef\n"),
        success("14\n"),
        success("Physical size: 1920x1080\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, retry_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectedDevice>(result));
    REQUIRE(runner.count_command("devices") == 3);
    REQUIRE(runner.count_command("connect") == 2);
}

TEST_CASE("retry exhaustion reports the final attempt metadata",
          "[EmulatorConnector][retry][exhausted]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n"),
        failure(1, "failed to connect 1\n"),
        success("List of devices attached\n"),
        failure(1, "failed to connect 2\n"),
        success("List of devices attached\n"),
        failure(1, "failed to connect 3\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, retry_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    auto const& failure_result = std::get<ConnectionFailure>(result);
    REQUIRE(failure_result.error_code == ConnectionErrorCode::CommandFailed);
    REQUIRE(failure_result.attempt == 3);
    REQUIRE(failure_result.max_attempts == 3);
    REQUIRE(runner.count_command("connect") == 3);
}

TEST_CASE("cancellation during retry prevents the next attempt",
          "[EmulatorConnector][retry][cancel]")
{
    std::stop_source source;
    ScriptedRunner runner{{
        success("List of devices attached\n"),
        failure(1, "failed to connect\n"),
    }};
    runner.set_invocation_hook([&source](AdbInvocation const& invocation) {
        if (!invocation.arguments.empty() && invocation.arguments.front() == "connect")
        {
            source.request_stop();
        }
    });

    EmulatorConnector connector{general_profile(), runner, retry_timings()};
    auto const result = connector.connect(default_request(), source.get_token());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    auto const& failure_result = std::get<ConnectionFailure>(result);
    REQUIRE(failure_result.error_code == ConnectionErrorCode::Canceled);
    REQUIRE(failure_result.attempt == 1);
    REQUIRE(failure_result.max_attempts == 3);
    REQUIRE(runner.invocation_count() == 2);
}

TEST_CASE("invalid identity does not retry the handshake",
          "[EmulatorConnector][retry][non-retryable]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        success("1\n"),
        success("not-a-valid-android-id\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, retry_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    auto const& failure_result = std::get<ConnectionFailure>(result);
    REQUIRE(failure_result.error_code == ConnectionErrorCode::InvalidDeviceResponse);
    REQUIRE(failure_result.attempt == 1);
    REQUIRE(failure_result.max_attempts == 3);
    REQUIRE(runner.count_command("devices") == 1);
}

TEST_CASE("retryable handshake failure restarts from target resolution",
          "[EmulatorConnector][retry][handshake]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        success("1\n"),
        failure(1, "transport disappeared\n"),
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        success("1\n"),
        success("0123456789abcdef\n"),
        success("14\n"),
        success("Physical size: 1920x1080\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, retry_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectedDevice>(result));
    REQUIRE(runner.count_command("devices") == 2);
}

TEST_CASE("unauthorized device returns DeviceUnauthorized",
          "[EmulatorConnector][unauthorized]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tunauthorized\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::DeviceUnauthorized);
}





TEST_CASE("absent TCP endpoint connects then waits for device",
          "[EmulatorConnector][tcp-endpoint]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n"),
        success("connected to 127.0.0.1:5555\n"),
        success("List of devices attached\n"
                "127.0.0.1:5555\toffline\n"),
        success("List of devices attached\n"
                "127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        success("1\n"),
        success("0123456789abcdef\n"),
        success("14\n"),
        success("Physical size: 1920x1080\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectedDevice>(result));
    REQUIRE(runner.contains_arguments({"connect", "127.0.0.1:5555"}));
}

TEST_CASE("connect success but device never becomes ready reports DeviceNotReady",
          "[EmulatorConnector][tcp-endpoint][ready-timeout]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n"),
        success("connected to 127.0.0.1:5555\n"),

        success("List of devices attached\n"),
        success("List of devices attached\n"),
        success("List of devices attached\n"),
        success("List of devices attached\n"),
        success("List of devices attached\n"),
        success("List of devices attached\n"),
    }};

    ConnectionTimings fast_ready_poll{
        .devices            = 5000ms,
        .connect            = 5000ms,
        .device_query       = 5000ms,
        .ready_poll_timeout = 200ms,
        .ready_poll_interval = 50ms,
        .boot_poll_timeout  = 5000ms,
        .boot_poll_interval  = 10ms,
        .max_attempts        = 1,
        .retry_interval      = 0ms,
    };

    EmulatorConnector connector{general_profile(), runner, fast_ready_poll};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::DeviceNotReady);
}





TEST_CASE("boot timeout returns BootNotCompleted and performs no identity query",
          "[EmulatorConnector][boot-timeout]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),

        success("0\n"),
        success("0\n"),
        success("0\n"),
        success("0\n"),
        success("0\n"),
    }};

    ConnectionTimings fast_boot_timeout{
        .devices             = 5000ms,
        .connect             = 5000ms,
        .device_query        = 5000ms,
        .ready_poll_timeout  = 5000ms,
        .ready_poll_interval = 10ms,
        .boot_poll_timeout   = 200ms,
        .boot_poll_interval   = 50ms,
        .max_attempts         = 1,
        .retry_interval       = 0ms,
    };

    EmulatorConnector connector{general_profile(), runner, fast_boot_timeout};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::BootNotCompleted);
    REQUIRE_FALSE(runner.contains_command("android_id"));
}





TEST_CASE("empty android_id returns InvalidDeviceResponse",
          "[EmulatorConnector][invalid-id]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        success("1\n"),
        success("\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::InvalidDeviceResponse);
}

TEST_CASE("non-hex android_id returns InvalidDeviceResponse",
          "[EmulatorConnector][invalid-id]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        success("1\n"),
        success("not_hex_!!\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::InvalidDeviceResponse);
}

TEST_CASE("short android_id (< 8 hex chars) returns InvalidDeviceResponse",
          "[EmulatorConnector][invalid-id]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        success("1\n"),
        success("abc\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::InvalidDeviceResponse);
}

TEST_CASE("android_version with control character returns InvalidDeviceResponse",
          "[EmulatorConnector][invalid-version]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        success("1\n"),
        success("0123456789abcdef\n"),
        success("1\r4\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::InvalidDeviceResponse);
}

TEST_CASE("android_version not starting with digit returns InvalidDeviceResponse",
          "[EmulatorConnector][invalid-version]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        success("1\n"),
        success("0123456789abcdef\n"),
        success("Tiramisu\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::InvalidDeviceResponse);
}

TEST_CASE("unparseable physical size returns InvalidDeviceResponse",
          "[EmulatorConnector][invalid-size]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        success("1\n"),
        success("0123456789abcdef\n"),
        success("14\n"),
        success("Physical size: 1920x1080abc\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::InvalidDeviceResponse);
}

TEST_CASE("zero dimension in physical size returns InvalidDeviceResponse",
          "[EmulatorConnector][invalid-size]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        success("1\n"),
        success("0123456789abcdef\n"),
        success("14\n"),
        success("Physical size: 0x1920\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::InvalidDeviceResponse);
}





TEST_CASE("process not started maps to ProcessStartFailed",
          "[EmulatorConnector][process-failure]")
{
    ScriptedRunner runner{{
        not_started(),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::ProcessStartFailed);
}

TEST_CASE("command timeout maps to CommandTimedOut",
          "[EmulatorConnector][timeout]")
{
    ScriptedRunner runner{{
        timed_out(),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::CommandTimedOut);
}

TEST_CASE("canceled command maps to Canceled",
          "[EmulatorConnector][cancel]")
{

    std::stop_source source;
    source.request_stop();

    ScriptedRunner runner{{
        canceled(),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request(), source.get_token());



    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    auto const& fail = std::get<ConnectionFailure>(result);
    REQUIRE(fail.error_code == ConnectionErrorCode::Canceled);

}

TEST_CASE("connect cancellation during boot poll stops immediately",
          "[EmulatorConnector][cancel]")
{

    std::stop_source source;

    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),

        [&source]() {
            source.request_stop();
            return AdbCommandResult{
                .exit_code       = {},
                .standard_output = {},
                .standard_error  = {},
                .started         = true,
                .timed_out       = {},
                .canceled        = true,
            };
        }(),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request(), source.get_token());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::Canceled);
}





TEST_CASE("devices command non-zero exit returns CommandFailed",
          "[EmulatorConnector][command-failed]")
{
    ScriptedRunner runner{{
        failure(1, "error: no devices/emulators found"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::CommandFailed);
}

TEST_CASE("unknown profile fails before list_devices invocation is dereferenced",
          "[EmulatorConnector][profile][regression]")
{
    ScriptedRunner runner{{}};
    EmulatorConnector connector{general_profile(), runner, test_timings()};

    auto request = default_request();
    request.profile_name = "MissingProfile";
    auto const result = connector.connect(request);

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    auto const& fail = std::get<ConnectionFailure>(result);
    REQUIRE(fail.error_code == ConnectionErrorCode::InvalidDeviceResponse);
    REQUIRE(fail.phase == "list_devices");
    REQUIRE(runner.arguments().empty());
}

TEST_CASE("existing-device get-state start failure maps to ProcessStartFailed",
          "[EmulatorConnector][get-state][regression]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        not_started(),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    auto const& fail = std::get<ConnectionFailure>(result);
    REQUIRE(fail.error_code == ConnectionErrorCode::ProcessStartFailed);
    REQUIRE(fail.phase == "get_state");
}

TEST_CASE("adb connect non-zero exit maps to CommandFailed even when output mentions connected",
          "[EmulatorConnector][tcp-endpoint][regression]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n"),
        failure(1, "failed to connect to 127.0.0.1:5555\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    auto const& fail = std::get<ConnectionFailure>(result);
    REQUIRE(fail.error_code == ConnectionErrorCode::CommandFailed);
    REQUIRE(fail.phase == "connect");
}

TEST_CASE("ready poll process start failure maps to ProcessStartFailed",
          "[EmulatorConnector][tcp-endpoint][regression]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n"),
        success("connected to 127.0.0.1:5555\n"),
        not_started(),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    auto const& fail = std::get<ConnectionFailure>(result);
    REQUIRE(fail.error_code == ConnectionErrorCode::ProcessStartFailed);
    REQUIRE(fail.phase == "ready_poll");
}





TEST_CASE("ConnectionTimings default max_attempts and retry_interval match contract",
          "[EmulatorConnector][timing]")
{
    auto const timings = ConnectionTimings{};
    REQUIRE(timings.max_attempts == 3);
    REQUIRE(timings.retry_interval == 2000ms);
}

TEST_CASE("ConnectionFailure aggregate init preserves attempt defaults",
          "[EmulatorConnector][failure]")
{
    ConnectionFailure const failure{
        ConnectionErrorCode::AdbExecutableNotFound,
        "preflight",
        "some message",
    };
    REQUIRE(failure.attempt == 1);
    REQUIRE(failure.max_attempts == 1);
}

TEST_CASE("connector default timings match the protocol contract",
          "[EmulatorConnector][timing]")
{
    auto const timings = ConnectionTimings{};
    REQUIRE(timings.devices            == 15000ms);
    REQUIRE(timings.connect            == 30000ms);
    REQUIRE(timings.device_query       == 15000ms);
    REQUIRE(timings.ready_poll_timeout == 30000ms);
    REQUIRE(timings.ready_poll_interval == 250ms);
    REQUIRE(timings.boot_poll_timeout   == 60000ms);
    REQUIRE(timings.boot_poll_interval  == 500ms);
}





TEST_CASE("devices output with blank lines and extra whitespace parses correctly",
          "[EmulatorConnector][devices-parsing]")
{
    ScriptedRunner runner{{
        success(
            "List of devices attached\n"
            "\n"
            "127.0.0.1:5555\tdevice\n"
            "\n"),
        success("device\n"),
        success("1\n"),
        success("0123456789abcdef\n"),
        success("14\n"),
        success("Physical size: 1920x1080\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectedDevice>(result));
}

TEST_CASE("get-state output trimmed of whitespace matches device",
          "[EmulatorConnector][get-state]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device \n"),
        success("1\n"),
        success("0123456789abcdef\n"),
        success("14\n"),
        success("Physical size: 1920x1080\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectedDevice>(result));
}

TEST_CASE("get-state output 'device' with leading/trailing whitespace is accepted",
          "[EmulatorConnector][get-state]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("  device  \n"),
        success("1\n"),
        success("0123456789abcdef\n"),
        success("14\n"),
        success("Physical size: 1920x1080\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectedDevice>(result));
}

TEST_CASE("get-state output not matching device returns InvalidDeviceResponse",
          "[EmulatorConnector][get-state]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("offline\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::InvalidDeviceResponse);
}
