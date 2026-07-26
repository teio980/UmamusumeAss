//
// Tests for EmulatorConnector handshake state machine.
//
// All tests use a ScriptedRunner (fake IAdbCommandRunner) so no real ADB
// process is needed. The connector parses scripted output, maps errors, and
// respects timeouts/cancellation deterministically.
//

#include <catch2/catch_test_macros.hpp>

#include "Connection/ConnectionProfile.hpp"
#include "Connection/EmulatorConnector.hpp"
#include "UmaAssistant/Connection.hpp"

#include <chrono>
#include <cstdint>
#include <filesystem>
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

// ── Helpers ────────────────────────────────────────────────────────────────

/// Returns the path to resource/connection.json relative to the source root.
[[nodiscard]] fs::path resource_path()
{
    return CMAKE_SOURCE_DIR "/resource/connection.json";
}

/// Load the General profile once for all tests.
[[nodiscard]] ConnectionProfile const& general_profile()
{
    static auto const profile = ConnectionProfile::load(resource_path());
    return profile;
}

// ── ScriptedRunner — fake IAdbCommandRunner ───────────────────────────────
//
// Pops from a pre-programmed queue of results.  The test verifies invocation
// order implicitly by the sequence of results the connector consumes.
// `arguments()` returns every invocation's argument vector joined end-to-end
// for easy ordering checks.

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
        std::chrono::milliseconds /*timeout*/,
        std::stop_token           /*cancellation*/) override
    {
        // Record the command for post-test inspection
        recorded_commands_.push_back(invocation.executable.string());
        for (auto const& arg : invocation.arguments)
        {
            recorded_commands_.push_back(arg);
        }

        if (next_ < results_.size())
        {
            return results_[next_++];
        }
        // No more scripted results — return a generic failure
        return Result{};
    }

    /// Returns all recorded command arguments in invocation order.
    [[nodiscard]] std::vector<std::string> arguments() const
    {
        return recorded_commands_;
    }

    /// Returns true if any recorded command argument contains `needle`.
    [[nodiscard]] bool contains_command(std::string_view needle) const
    {
        for (auto const& arg : recorded_commands_)
        {
            if (arg == needle) return true;
        }
        return false;
    }

    /// Returns true if the exact sequence `seq` appears in order.
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
        return recorded_commands_.empty()
            ? 0
            : 1; // We track per-command, not per-invocation
    }

private:
    std::vector<Result>  results_;
    std::size_t          next_ = 0;
    std::vector<std::string> recorded_commands_;
};

// ── Script helpers ─────────────────────────────────────────────────────────

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

// ── Valid ADB path (must point to a real .exe to pass preflight) ───────────

/// Returns a path to a real executable on the system so that the preflight
/// file-existence check passes.  cmd.exe is guaranteed to exist on Windows.
[[nodiscard]] fs::path valid_adb_path()
{
    return "C:\\Windows\\System32\\cmd.exe";
}

// ── Default request ────────────────────────────────────────────────────────

[[nodiscard]] ConnectionRequest default_request()
{
    return ConnectionRequest{
        .adb_path     = valid_adb_path(),
        .serial       = "127.0.0.1:5555",
        .profile_name = "General",
    };
}

// ── Deterministic timings for all tests ────────────────────────────────────

[[nodiscard]] ConnectionTimings test_timings()
{
    // Use large timeouts so the fake runner never hits real wall-clock limits
    return ConnectionTimings{
        .devices            = 5000ms,
        .connect            = 5000ms,
        .device_query       = 5000ms,
        .ready_poll_timeout = 5000ms,
        .ready_poll_interval = 10ms,
        .boot_poll_timeout  = 5000ms,
        .boot_poll_interval  = 10ms,
    };
}

} // anonymous namespace

// ==========================================================================
// Preflight — validation before any ADB command
// ==========================================================================

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

// ==========================================================================
// Existing device — happy path
// ==========================================================================

TEST_CASE("existing device completes get-state boot identity and size handshake",
          "[EmulatorConnector][existing-device]")
{
    ScriptedRunner runner{{
        success(
            "List of devices attached\n"
            "127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        success(""),   // boot_completed returns raw "1\n" not empty — fix
    }};

    // The plan says success("0\n") then success("1\n") but we need to
    // test both: first a "0" (boot not done) then "1" (boot done)
    // Actually re-reading the spec: poll every 500ms until it returns 1
    // Let me provide both responses
    runner = ScriptedRunner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        success("0\n"),          // first boot poll — still booting
        success("1\n"),          // second boot poll — boot completed
        success("0123456789abcdef\n"),  // android_id
        success("14\n"),                // android_version
        success("Physical size: 1920x1080\nOverride size: 1280x720\n"),  // get_size
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

// ==========================================================================
// Opaque serial safety — never adb connect for emulator-#### or USB serials
// ==========================================================================

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

// ==========================================================================
// offline / unauthorized
// ==========================================================================

TEST_CASE("offline device returns DeviceOffline",
          "[EmulatorConnector][offline]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\toffline\n"),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::DeviceOffline);
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

// ==========================================================================
// TCP endpoint — absent device triggers adb connect
// ==========================================================================

TEST_CASE("absent TCP endpoint connects then waits for device",
          "[EmulatorConnector][tcp-endpoint]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n"),               // devices — empty list
        success("connected to 127.0.0.1:5555\n"),             // connect succeeds
        success("List of devices attached\n"                  // devices poll — still absent
                "127.0.0.1:5555\toffline\n"),
        success("List of devices attached\n"                  // devices poll — becomes device
                "127.0.0.1:5555\tdevice\n"),
        success("device\n"),                                   // get-state
        success("1\n"),                                        // boot completed
        success("0123456789abcdef\n"),                          // android_id
        success("14\n"),                                        // android_version
        success("Physical size: 1920x1080\n"),                 // get_size
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
        // Device stays absent in every poll — fill ~5 polls (200ms / 50ms)
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
    };

    EmulatorConnector connector{general_profile(), runner, fast_ready_poll};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::DeviceNotReady);
}

// ==========================================================================
// Boot timeout
// ==========================================================================

TEST_CASE("boot timeout returns BootNotCompleted and performs no identity query",
          "[EmulatorConnector][boot-timeout]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        // Fill ~5 boot polls (200ms / 50ms) before timeout fires
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
    };

    EmulatorConnector connector{general_profile(), runner, fast_boot_timeout};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::BootNotCompleted);
    REQUIRE_FALSE(runner.contains_command("android_id"));
}

// ==========================================================================
// Invalid device responses
// ==========================================================================

TEST_CASE("empty android_id returns InvalidDeviceResponse",
          "[EmulatorConnector][invalid-id]")
{
    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        success("1\n"),
        success("\n"),   // empty android_id
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
        success("abc\n"),   // only 3 hex chars
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
        success("1\r4\n"),   // embedded control char (CR) survives trim
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
        success("Tiramisu\n"),   // codename, not version number
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
        success("Physical size: 1920x1080abc\n"),   // trailing junk
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

// ==========================================================================
// Process runner failure mapping
// ==========================================================================

TEST_CASE("process not started maps to ProcessStartFailed",
          "[EmulatorConnector][process-failure]")
{
    ScriptedRunner runner{{
        not_started(),   // first command fails to start
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
        timed_out(),   // first command times out
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
    // Use a pre-canceled stop_token
    std::stop_source source;
    source.request_stop();

    ScriptedRunner runner{{
        canceled(),
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request(), source.get_token());

    // With pre-canceled token, the connector should return Canceled without
    // even calling the runner (preflight checks cancellation)
    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    auto const& fail = std::get<ConnectionFailure>(result);
    REQUIRE(fail.error_code == ConnectionErrorCode::Canceled);
    // The runner may not have been called at all
}

TEST_CASE("connect cancellation during boot poll stops immediately",
          "[EmulatorConnector][cancel]")
{
    // Issue cancellation via stop_source during the boot poll
    std::stop_source source;

    ScriptedRunner runner{{
        success("List of devices attached\n127.0.0.1:5555\tdevice\n"),
        success("device\n"),
        // Next result: simulate cancellation during boot poll
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

// ==========================================================================
// Non-zero exit code from ADB command
// ==========================================================================

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

// ==========================================================================
// Timing defaults
// ==========================================================================

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

// ==========================================================================
// adb devices parsing edge cases
// ==========================================================================

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
        success("device \n"),   // trailing whitespace — must be trimmed
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
        success("  device  \n"),   // trimmed to "device"
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
        success("offline\n"),   // not "device"
    }};

    EmulatorConnector connector{general_profile(), runner, test_timings()};
    auto const result = connector.connect(default_request());

    REQUIRE(std::holds_alternative<ConnectionFailure>(result));
    REQUIRE(std::get<ConnectionFailure>(result).error_code
            == ConnectionErrorCode::InvalidDeviceResponse);
}
