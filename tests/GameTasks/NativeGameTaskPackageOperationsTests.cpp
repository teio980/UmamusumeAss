#include "GameTasks/NativeGameTaskPackageOperations.hpp"

#include <catch2/catch_test_macros.hpp>
#include <windows.h>

#include <chrono>
#include <filesystem>
#include <fstream>
#include <map>
#include <memory>
#include <stop_token>
#include <string>
#include <thread>

namespace
{
using namespace UmaAssistant;
using namespace std::chrono_literals;

struct PackageFixture
{
    PackageFixture()
    {
        auto const nonce = std::chrono::steady_clock::now().time_since_epoch().count();
        probeFile = std::filesystem::temp_directory_path()
            / ("uma-launch-probes-" + std::to_string(GetCurrentProcessId())
                + "-" + std::to_string(nonce));
        Set(L"UMA_FAKE_ADB_PROCESS_PROBE_FILE", probeFile.wstring());
        Set(L"UMA_FAKE_ADB_PACKAGE_PROCESS_MODE", L"");
        Set(L"UMA_FAKE_ADB_AM_START_FAIL", L"");
        Set(L"UMA_FAKE_ADB_RESOLVE_ACTIVITY_MODE", L"");
        Set(L"UMA_FAKE_ADB_DELAY_MS", L"0");
        auto profile = std::make_shared<ConnectionProfile const>(ConnectionProfile::default_profile());
        session = std::make_shared<DeviceSession>(profile, std::filesystem::path(UMA_FAKE_ADB_PATH),
            "General", ConnectedDevice{"emulator-5554", "test", "14", 900, 1600, 900, 1600}, 1);
    }

    void Set(const std::wstring& name, const std::wstring& value)
    {
        if (!previous.contains(name))
        {
            auto const size = GetEnvironmentVariableW(name.c_str(), nullptr, 0);
            std::wstring original(size, L'\0');
            if (size > 0)
            {
                GetEnvironmentVariableW(name.c_str(), original.data(), size);
                original.resize(size - 1);
            }
            previous.emplace(name, std::move(original));
        }
        REQUIRE(SetEnvironmentVariableW(name.c_str(), value.empty() ? nullptr : value.c_str()));
    }

    int Probes() const
    {
        int count = 0;
        std::ifstream input(probeFile);
        input >> count;
        return count;
    }

    ~PackageFixture()
    {
        for (auto const& [name, value] : previous)
            SetEnvironmentVariableW(name.c_str(), value.empty() ? nullptr : value.c_str());
        std::error_code error;
        std::filesystem::remove(probeFile, error);
    }

    std::filesystem::path probeFile;
    std::map<std::wstring, std::wstring> previous;
    std::shared_ptr<DeviceSession> session;
};
}

TEST_CASE("Native launcher confirms the process after a successful launch command", "[native-launch]")
{
    PackageFixture fixture;
    std::string activity;
    SECTION("resolved package") {}
    SECTION("explicit activity") { activity = ".MainActivity"; }
    SECTION("launcher intent fallback")
    {
        fixture.Set(L"UMA_FAKE_ADB_RESOLVE_ACTIVITY_MODE", L"fail");
    }
    SECTION("pidof failure uses ps")
    {
        fixture.Set(L"UMA_FAKE_ADB_PACKAGE_PROCESS_MODE", L"pidof-fail-ps-running");
    }
    GameTasks::NativeGameTaskPackageOperations operations(fixture.session, {});
    auto const result = operations.Start("com.example.game", activity);
    CHECK(result.succeeded);
    CHECK(result.processDetected);
    CHECK(result.errorCode == 0);
    CHECK_FALSE(result.message.empty());
    CHECK(fixture.Probes() == 1);
}

TEST_CASE("Native launcher retries when a cold process appears after the first probe window", "[native-launch]")
{
    PackageFixture fixture;
    fixture.Set(L"UMA_FAKE_ADB_PACKAGE_PROCESS_MODE", L"delayed-running");
    GameTasks::NativeGameTaskPackageOperations operations(fixture.session, {});
    auto const result = operations.Start("com.example.game", "");
    CHECK(result.succeeded);
    CHECK(result.processDetected);
    CHECK(fixture.Probes() == 3);
}

TEST_CASE("Native launcher cancellation interrupts process and launch retries", "[native-launch]")
{
    PackageFixture fixture;
    SECTION("accepted launch without a running process")
    {
        fixture.Set(L"UMA_FAKE_ADB_PACKAGE_PROCESS_MODE", L"pidof-empty");
    }
    SECTION("launch command failed")
    {
        fixture.Set(L"UMA_FAKE_ADB_AM_START_FAIL", L"1");
    }
    std::stop_source cancellation;
    std::jthread cancel([&] { std::this_thread::sleep_for(600ms); cancellation.request_stop(); });
    GameTasks::NativeGameTaskPackageOperations operations(fixture.session, cancellation.get_token());
    auto const started = std::chrono::steady_clock::now();
    auto const result = operations.Start("com.example.game", "");
    CHECK_FALSE(result.succeeded);
    CHECK_FALSE(result.processDetected);
    CHECK(result.cancelled);
    CHECK(result.errorCode == static_cast<int>(ConnectionErrorCode::Canceled));
    CHECK(std::chrono::steady_clock::now() - started < 2s);
    CHECK_FALSE(result.message.empty());
}

TEST_CASE("Native launcher rejects invalid settings without retrying", "[native-launch]")
{
    PackageFixture fixture;
    GameTasks::NativeGameTaskPackageOperations operations(fixture.session, {});
    auto const result = operations.Start("invalid package", "");
    CHECK_FALSE(result.succeeded);
    CHECK(result.errorCode == static_cast<int>(ConnectionErrorCode::InvalidArgument));
    CHECK(fixture.Probes() == 0);
}
