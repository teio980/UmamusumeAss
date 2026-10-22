#include <catch2/catch_test_macros.hpp>

#include "UmaAssistant/UmaCaller.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <filesystem>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <type_traits>
#include <utility>
#include <vector>

#include <nlohmann/json.hpp>

#if defined(_WIN32)
#include <windows.h>
inline uint32_t current_process_id() { return ::GetCurrentProcessId(); }
#else
#include <unistd.h>
inline uint32_t current_process_id() { return static_cast<uint32_t>(getpid()); }
#define GetCurrentProcessId current_process_id
using DWORD = uint32_t;

inline int SetEnvironmentVariableW(wchar_t const* name, wchar_t const* value)
{
    std::filesystem::path p_name(name ? name : L"");
    std::string s_name = p_name.string();
    if (value && *value)
    {
        std::filesystem::path p_val(value);
        return setenv(s_name.c_str(), p_val.string().c_str(), 1) == 0 ? 1 : 0;
    }
    else
    {
        return unsetenv(s_name.c_str()) == 0 ? 1 : 0;
    }
}

inline int SetEnvironmentVariableW(wchar_t const* name, char const* value)
{
    std::filesystem::path p_name(name ? name : L"");
    std::string s_name = p_name.string();
    if (value && *value)
    {
        return setenv(s_name.c_str(), value, 1) == 0 ? 1 : 0;
    }
    else
    {
        return unsetenv(s_name.c_str()) == 0 ? 1 : 0;
    }
}

inline int SetEnvironmentVariableW(wchar_t const* name, std::nullptr_t)
{
    std::filesystem::path p_name(name ? name : L"");
    return unsetenv(p_name.string().c_str()) == 0 ? 1 : 0;
}

inline DWORD GetEnvironmentVariableW(wchar_t const* name, wchar_t* buffer, DWORD size)
{
    std::filesystem::path p_name(name ? name : L"");
    char const* val = std::getenv(p_name.string().c_str());
    if (!val) return 0;
    std::filesystem::path p_val(val);
    std::wstring w_val = p_val.wstring();
    if (buffer == nullptr || size == 0)
    {
        return static_cast<DWORD>(w_val.size() + 1);
    }
    if (w_val.size() < size)
    {
        std::copy(w_val.begin(), w_val.end(), buffer);
        buffer[w_val.size()] = L'\0';
        return static_cast<DWORD>(w_val.size());
    }
    return static_cast<DWORD>(w_val.size() + 1);
}
#endif

#ifndef CMAKE_SOURCE_DIR
#define CMAKE_SOURCE_DIR "."
#endif

namespace
{

using RunGameTaskSignature = UmaStartResult(UMA_CALL*)(UmaHandle, char const*, char const*);
static_assert(std::is_same_v<decltype(&UmaRunGameTaskAsync), RunGameTaskSignature>);

void UMA_CALL CountCallback(std::int32_t, char const*, void* custom_arg)
{
    auto* count = static_cast<std::atomic<int>*>(custom_arg);
    ++*count;
}

struct PipelineCallbackState
{
    struct Event
    {
        std::int32_t message{};
        std::uint64_t operationId{};
        nlohmann::json details;
    };

    static void UMA_CALL Callback(std::int32_t message, char const* json, void* custom_arg)
    {
        auto& self = *static_cast<PipelineCallbackState*>(custom_arg);
        std::uint64_t operationId{};
        nlohmann::json details = nlohmann::json::object();
        try
        {
            details = nlohmann::json::parse(json);
            operationId = details.value("operation_id", std::uint64_t{});
        }
        catch (...)
        {
        }

        std::unique_lock lock(self.mutex);
        self.events.push_back({message, operationId, std::move(details)});
        if (message == UMA_MSG_PIPELINE_COMPLETED && !self.firstPipelineCompletionEntered)
        {
            self.firstPipelineCompletionEntered = true;
            self.changed.notify_all();
            self.changed.wait(lock, [&] { return self.releaseFirstPipelineCompletion; });
        }
        self.changed.notify_all();
    }

    bool WaitFor(std::int32_t message, std::uint64_t operationId,
        std::chrono::milliseconds timeout = std::chrono::seconds(10))
    {
        std::unique_lock lock(mutex);
        return changed.wait_for(lock, timeout, [&] {
            return std::any_of(events.begin(), events.end(), [&](Event const& event) {
                return event.message == message && event.operationId == operationId;
            });
        });
    }

    bool WaitForFirstPipelineCompletion(
        std::chrono::milliseconds timeout = std::chrono::seconds(10))
    {
        std::unique_lock lock(mutex);
        return changed.wait_for(lock, timeout, [&] { return firstPipelineCompletionEntered; });
    }

    std::optional<Event> WaitForPipelineResult(std::uint64_t operationId,
        std::chrono::milliseconds timeout = std::chrono::seconds(30))
    {
        std::unique_lock lock(mutex);
        auto const hasResult = [&] {
            return std::any_of(events.begin(), events.end(), [&](Event const& event) {
                return event.operationId == operationId
                    && (event.message == UMA_MSG_PIPELINE_COMPLETED
                        || event.message == UMA_MSG_PIPELINE_FAILED);
            });
        };
        if (!changed.wait_for(lock, timeout, hasResult)) return std::nullopt;
        auto const found = std::find_if(events.begin(), events.end(), [&](Event const& event) {
            return event.operationId == operationId
                && (event.message == UMA_MSG_PIPELINE_COMPLETED
                    || event.message == UMA_MSG_PIPELINE_FAILED);
        });
        return found == events.end() ? std::nullopt : std::optional<Event>{*found};
    }

    std::string DiagnosticSnapshot() const
    {
        std::lock_guard lock(mutex);
        std::string result;
        for (auto const& event : events)
        {
            if (!result.empty()) result += "\n";
            result += "message=" + std::to_string(event.message)
                + " operation=" + std::to_string(event.operationId)
                + " details=" + event.details.dump();
        }
        return result.empty() ? "No callback events were observed." : result;
    }

    void ReleaseFirstPipelineCompletion()
    {
        {
            std::lock_guard lock(mutex);
            releaseFirstPipelineCompletion = true;
        }
        changed.notify_all();
    }

    mutable std::mutex mutex;
    std::condition_variable changed;
    std::vector<Event> events;
    bool firstPipelineCompletionEntered{};
    bool releaseFirstPipelineCompletion{};
};

struct HandleGuard
{
    UmaHandle value{};
    ~HandleGuard() { if (value) UmaDestroy(value); }
};

struct ReleaseCallbackGuard
{
    PipelineCallbackState& callbacks;
    ~ReleaseCallbackGuard() { callbacks.ReleaseFirstPipelineCompletion(); }
};

struct TemporaryDirectory
{
    std::filesystem::path path;
    ~TemporaryDirectory()
    {
        std::error_code ignored;
        std::filesystem::remove_all(path, ignored);
    }
};

struct UserDirectoryReset
{
    ~UserDirectoryReset() { (void)UmaSetUserDir(CMAKE_SOURCE_DIR); }
};

struct EnvironmentVariableGuard
{
    explicit EnvironmentVariableGuard(std::wstring envName) : name(std::move(envName))
    {
        auto const required = GetEnvironmentVariableW(this->name.c_str(), nullptr, 0);
        if (required == 0) return;
        std::wstring value(required, L'\0');
        auto const length = GetEnvironmentVariableW(
            this->name.c_str(), value.data(), static_cast<DWORD>(value.size()));
        if (length != 0)
        {
            value.resize(length);
            previous = std::move(value);
        }
    }

    ~EnvironmentVariableGuard()
    {
        if (previous)
            (void)SetEnvironmentVariableW(name.c_str(), previous->c_str());
        else
            (void)SetEnvironmentVariableW(name.c_str(), nullptr);
    }

    std::wstring name;
    std::optional<std::wstring> previous;
};

std::string PathUtf8(std::filesystem::path const& path)
{
    auto const value = path.u8string();
    return {reinterpret_cast<char const*>(value.data()), value.size()};
}

} // namespace

TEST_CASE("API 10 game-task starts reject invalid requests synchronously without callbacks", "[phase3][api]")
{
    REQUIRE(UmaGetApiVersion() == 11u);

    std::atomic<int> callbackCount{};
    REQUIRE(UmaSetUserDir(CMAKE_SOURCE_DIR) == UMA_SUCCESS);
    REQUIRE(UmaLoadResource(CMAKE_SOURCE_DIR) == UMA_SUCCESS);
    UmaHandle handle = UmaCreate(&CountCallback, &callbackCount);
    REQUIRE(handle != nullptr);

    UmaStartResult nullHandle = UmaRunGameTaskAsync(nullptr, "mail-collection", "{}");
    CHECK(nullHandle.operation_id == 0);
    CHECK(nullHandle.error_code == UMA_ERROR_INVALID_ARGUMENT);

    UmaStartResult unknownTask = UmaRunGameTaskAsync(handle, "not-a-game-task", "{}");
    CHECK(unknownTask.operation_id == 0);
    CHECK(unknownTask.error_code == UMA_ERROR_INVALID_ARGUMENT);

    std::string malformedUtf8(1, static_cast<char>(0xff));
    UmaStartResult invalidTaskId = UmaRunGameTaskAsync(handle, malformedUtf8.c_str(), "{}");
    CHECK(invalidTaskId.operation_id == 0);
    CHECK(invalidTaskId.error_code == UMA_ERROR_INVALID_ARGUMENT);

    UmaStartResult malformedSettings = UmaRunGameTaskAsync(handle, "mail-collection", "{not-json}");
    CHECK(malformedSettings.operation_id == 0);
    CHECK(malformedSettings.error_code == UMA_ERROR_INVALID_ARGUMENT);

    CHECK(callbackCount.load() == 0);
    UmaDestroy(handle);
}

TEST_CASE("accepted pipeline keeps its user-directory snapshot while waiting for the previous worker",
    "[phase3][api][resources][lifecycle]")
{
    using namespace std::chrono_literals;
    namespace fs = std::filesystem;

    UserDirectoryReset resetUserDirectory;
    REQUIRE(UmaSetUserDir(CMAKE_SOURCE_DIR) == UMA_SUCCESS);
    REQUIRE(UmaLoadResource(CMAKE_SOURCE_DIR) == UMA_SUCCESS);

    TemporaryDirectory temporary{
        fs::temp_directory_path() / (L"uma-userdir-snapshot-"
            + std::to_wstring(GetCurrentProcessId()) + L"-"
            + std::to_wstring(std::chrono::steady_clock::now().time_since_epoch().count()))};
    EnvironmentVariableGuard captureResolution{L"UMA_FAKE_ADB_RESOLUTION"};
    EnvironmentVariableGuard screenshotPath{L"UMA_FAKE_ADB_SCREENSHOT_PATH"};
    auto const originalDirectory = temporary.path / "accepted";
    auto const changedDirectory = temporary.path / "changed";
    fs::create_directories(originalDirectory);
    fs::create_directories(changedDirectory);
    auto const capturePath = fs::path(CMAKE_SOURCE_DIR)
        / "testdata" / "hachimi" / "ura" / "captures"
        / "career_skill_race_day_490_sample.png";
    auto const probeName = "snapshot-probe.png";
    REQUIRE(fs::exists(capturePath));
    fs::copy_file(capturePath, originalDirectory / probeName);
    REQUIRE(SetEnvironmentVariableW(L"UMA_FAKE_ADB_RESOLUTION", L"900x1600") != 0);
    REQUIRE(SetEnvironmentVariableW(L"UMA_FAKE_ADB_SCREENSHOT_PATH", capturePath.c_str()) != 0);

    auto const originalDirectoryUtf8 = PathUtf8(originalDirectory);
    auto const changedDirectoryUtf8 = PathUtf8(changedDirectory);
    REQUIRE(UmaSetUserDir(originalDirectoryUtf8.c_str()) == UMA_SUCCESS);

    PipelineCallbackState callbacks;
    HandleGuard handle{UmaCreate(&PipelineCallbackState::Callback, &callbacks)};
    REQUIRE(handle.value != nullptr);
    // Keep a guard alive before any blocking callback can arrive. If any
    // later assertion fails, it releases that callback before HandleGuard
    // destroys the handle and joins its worker.
    ReleaseCallbackGuard releaseOnEarlyFailure{callbacks};

    auto const connection = UmaConnectAsync(
        handle.value, UMA_FAKE_ADB_PATH, "test-serial", "General");
    REQUIRE(connection.error_code == UMA_SUCCESS);
    REQUIRE(callbacks.WaitFor(UMA_MSG_CONNECTION_SUCCEEDED, connection.operation_id));

    auto const firstPipeline = UmaRunPipelineAsync(handle.value,
        R"({"schemaVersion":1,"start":"Done","tasks":{"Done":{"action":"DoNothing"}}})",
        nullptr);
    REQUIRE(firstPipeline.error_code == UMA_SUCCESS);
    auto const firstCompletionObserved = callbacks.WaitForFirstPipelineCompletion();
    INFO(callbacks.DiagnosticSnapshot());
    REQUIRE(firstCompletionObserved);

    std::string const secondPipelineJson =
        std::string(R"({"schemaVersion":1,"start":"Probe","tasks":{"Probe":{"algorithm":"MatchTemplate","action":"FindText","timeoutMs":5000,"template":")")
        + probeName
        + R"(","templThreshold":0.99}}})";

    // The second call accepts its operation before joining the first worker,
    // which is held inside the completion callback above. BUSY is the
    // observable synchronization point proving acceptance without a timing
    // assumption or a sleep-based race.
    UmaStartResult secondPipeline{};
    std::jthread starter([&] {
        secondPipeline = UmaRunPipelineAsync(handle.value, secondPipelineJson.c_str(), nullptr);
    });
    // This later guard is destroyed before starter, ensuring failure after
    // starting the second API call also unblocks the prior worker first.
    ReleaseCallbackGuard releaseCallback{callbacks};
    auto const deadline = std::chrono::steady_clock::now() + 10s;
    bool acceptedWhileJoining = false;
    while (std::chrono::steady_clock::now() < deadline)
    {
        auto const probe = UmaSetInstanceOptions(handle.value, R"({"version":1})");
        if (probe == UMA_ERROR_BUSY)
        {
            acceptedWhileJoining = true;
            break;
        }
        REQUIRE(probe == UMA_SUCCESS);
        std::this_thread::yield();
    }
    REQUIRE(acceptedWhileJoining);

    REQUIRE(UmaSetUserDir(changedDirectoryUtf8.c_str()) == UMA_SUCCESS);
    callbacks.ReleaseFirstPipelineCompletion();
    starter.join();
    REQUIRE(secondPipeline.error_code == UMA_SUCCESS);
    auto const result = callbacks.WaitForPipelineResult(secondPipeline.operation_id);
    INFO(callbacks.DiagnosticSnapshot());
    REQUIRE(result.has_value());
    INFO(result->details.dump(2));
    CHECK(result->message == UMA_MSG_PIPELINE_COMPLETED);

    UmaDestroy(handle.value);
    handle.value = nullptr;
}
