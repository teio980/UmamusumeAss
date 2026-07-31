//
// connect_smoke — smoke-test CLI for the ADB connection pipeline.
//
// Usage:  connect_smoke <adb_path> <serial>
//
// Uses resource/connection.json (resolved relative to the executable or the
// source tree) when present, otherwise the built-in default profile,
// connects to the device via the General profile, and prints device info
// on success or a structured error message on failure.
//
// Moved from src/UmaAssistantCore/uma_connect_smoke_main.cpp to tools/
// as part of phase-2-adb-protocol cleanup.  No behaviour change.
//

#include "Connection/AdbCommandRunner.hpp"
#include "Connection/ConnectionProfile.hpp"
#include "Connection/EmulatorConnector.hpp"
#include "Connection/SmokeCliParser.hpp"
#include "UmaAssistant/Connection.hpp"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <cstdlib>
#include <filesystem>
#include <iostream>
#include <string>
#include <variant>

namespace {

namespace fs = std::filesystem;
using namespace UmaAssistant;

// ---------------------------------------------------------------------------
// resolve_resource_path — find resource/connection.json relative to the
// executable or the source tree.
// ---------------------------------------------------------------------------
[[nodiscard]] fs::path resolve_resource_path()
{
    // Strategy 1: relative to the executable
    wchar_t exe_path[MAX_PATH + 1] = {};
    DWORD const len = ::GetModuleFileNameW(nullptr, exe_path,
                                            static_cast<DWORD>(std::size(exe_path)));
    if (len > 0 && len < std::size(exe_path))
    {
        auto const exe_dir = fs::path{exe_path}.parent_path();
        auto const candidate = exe_dir / "resource" / "connection.json";
        if (fs::exists(candidate)) return candidate;
    }

    // Strategy 2: relative to CMAKE_SOURCE_DIR (build-tree fallback)
    // This string is baked in at CMake configure time.
    if (fs::exists(CMAKE_SOURCE_DIR "/resource/connection.json"))
    {
        return CMAKE_SOURCE_DIR "/resource/connection.json";
    }

    return {};
}

// ---------------------------------------------------------------------------
// print_connected_device — output device info to stdout.
// ---------------------------------------------------------------------------
void print_connected_device(ConnectedDevice const& device)
{
    std::cout << "serial:           " << device.serial          << "\n"
              << "android_id:       " << device.android_id      << "\n"
              << "android_version:  " << device.android_version << "\n"
              << "effective:        " << device.width << "x" << device.height << "\n"
              << "physical:         " << device.physical_width
              << "x" << device.physical_height << "\n";
}

// ---------------------------------------------------------------------------
// print_connection_failure — output error info to stderr.
// ---------------------------------------------------------------------------
void print_connection_failure(ConnectionFailure const& failure)
{
    std::cerr << "ERROR [" << failure.phase << "] (code "
              << static_cast<int>(failure.error_code) << "): "
              << failure.message << "\n";
}

} // anonymous namespace

// ===========================================================================

int main(int argc, char* argv[])
{
    // ---- Parse arguments ----
    auto const parse_result = parse_smoke_args(argc, argv);
    if (std::holds_alternative<SmokCliError>(parse_result))
    {
        std::cerr << std::get<SmokCliError>(parse_result).message << "\n";
        return 1;
    }

    auto const& args = std::get<SmokCliArgs>(parse_result);

    // ---- Resolve resource file (optional) ----
    auto const resource_path = resolve_resource_path();

    // ---- Load connection profile ----
    ConnectionProfile profile = [&]() -> ConnectionProfile {
        if (resource_path.empty())
        {
            return ConnectionProfile::default_profile();
        }
        try
        {
            return ConnectionProfile::load(resource_path);
        }
        catch (ProfileError const& e)
        {
            std::cerr << "ERROR: " << e.what() << "\n";
            std::exit(1);
        }
    }();

    // ---- Create real runner ----
    auto process = create_win32_process();
    AdbCommandRunnerWin32 runner{*process};

    // ---- Connect ----
    EmulatorConnector connector{profile, runner};

    ConnectionRequest request{
        .adb_path     = args.adb_path,
        .serial       = args.serial,
        .profile_name = "General",
    };

    auto const result = connector.connect(request);

    // ---- Print result ----
    if (std::holds_alternative<ConnectedDevice>(result))
    {
        print_connected_device(std::get<ConnectedDevice>(result));
        return 0;
    }

    print_connection_failure(std::get<ConnectionFailure>(result));
    return 1;
}
