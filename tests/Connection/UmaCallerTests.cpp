#include <catch2/catch_test_macros.hpp>

#include "UmaAssistant/UmaCaller.h"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <type_traits>
#include <vector>

#include <windows.h>

using namespace std::chrono_literals;
namespace fs = std::filesystem;

namespace {

using CancelConnectSignature = std::int32_t(UMA_CALL*)(UmaHandle, std::uint64_t);
using CaptureSignature = UmaStartResult(UMA_CALL*)(UmaHandle);
using CopyFrameSignature = std::int32_t(UMA_CALL*)(
    UmaHandle, std::uint64_t, std::uint8_t*, std::uint64_t);
using SwipeSignature = UmaStartResult(UMA_CALL*)(
    UmaHandle, std::uint64_t, std::int32_t, std::int32_t,
    std::int32_t, std::int32_t, std::int32_t);

static_assert(std::is_same_v<decltype(&UmaCancelConnect), CancelConnectSignature>);
static_assert(std::is_same_v<decltype(&UmaCaptureAsync), CaptureSignature>);
static_assert(std::is_same_v<decltype(&UmaCopyFramePng), CopyFrameSignature>);
static_assert(std::is_same_v<decltype(&UmaSwipeAsync), SwipeSignature>);
static_assert(std::is_same_v<UmaApiCallback, void(UMA_CALL*)(
    std::int32_t, char const*, void*)>);

struct Message
{
    std::int32_t id{};
    nlohmann::json details;
};

class Collector
{
public:
    static void UMA_CALL callback(
        std::int32_t message, char const* details_json, void* custom_arg)
    {
        auto& self = *static_cast<Collector*>(custom_arg);
        auto details = nlohmann::json::parse(details_json);
        {
            std::lock_guard lock(self.mutex_);
            self.messages_.push_back({message, std::move(details)});
        }
        self.cv_.notify_all();
    }

    [[nodiscard]] bool wait_for_terminal(
        std::chrono::milliseconds timeout = 5s)
    {
        std::unique_lock lock(mutex_);
        return cv_.wait_for(lock, timeout, [this] {
            return !messages_.empty() && is_terminal(messages_.back().id);
        });
    }

    [[nodiscard]] std::vector<Message> messages() const
    {
        std::lock_guard lock(mutex_);
        return messages_;
    }

    void clear()
    {
        std::lock_guard lock(mutex_);
        messages_.clear();
    }

    [[nodiscard]] static bool is_terminal(std::int32_t id) noexcept
    {
        return id == UMA_MSG_CONNECTION_SUCCEEDED
            || id == UMA_MSG_CONNECTION_FAILED;
    }

private:
    mutable std::mutex mutex_;
    std::condition_variable cv_;
    std::vector<Message> messages_;
};

class RegistrationBuffer
{
public:
    static void UMA_CALL callback(
        std::int32_t message, char const* details_json, void* custom_arg)
    {
        auto& self = *static_cast<RegistrationBuffer*>(custom_arg);
        self.accept({message, nlohmann::json::parse(details_json)});
    }

    void accept(Message message)
    {
        {
            std::lock_guard lock(mutex_);
            if (operation_id_)
            {
                route_locked(std::move(message));
            }
            else
            {
                buffered_.push_back(std::move(message));
            }
        }
        condition_.notify_all();
    }

    void bind(std::uint64_t operation_id)
    {
        {
            std::lock_guard lock(mutex_);
            operation_id_ = operation_id;
            for (auto& message : buffered_)
            {
                route_locked(std::move(message));
            }
            buffered_.clear();
        }
        condition_.notify_all();
    }

    [[nodiscard]] bool wait_for_terminal(
        std::chrono::milliseconds timeout = 5s)
    {
        std::unique_lock lock(mutex_);
        return condition_.wait_for(lock, timeout, [this] {
            return !routed_.empty() && Collector::is_terminal(routed_.back().id);
        });
    }

    [[nodiscard]] bool has_mismatched_operation() const
    {
        std::lock_guard lock(mutex_);
        return mismatched_operation_;
    }

    [[nodiscard]] std::vector<Message> messages() const
    {
        std::lock_guard lock(mutex_);
        return routed_;
    }

private:
    void route_locked(Message message)
    {
        auto const message_operation_id =
            message.details.at("operation_id").get<std::uint64_t>();
        if (!operation_id_ || message_operation_id != *operation_id_)
        {
            mismatched_operation_ = true;
            return;
        }
        routed_.push_back(std::move(message));
    }

    mutable std::mutex mutex_;
    std::condition_variable condition_;
    std::optional<std::uint64_t> operation_id_;
    std::vector<Message> buffered_;
    std::vector<Message> routed_;
    bool mismatched_operation_ = false;
};

class FakeAdbDelay
{
public:
    explicit FakeAdbDelay(wchar_t const* value)
    {
        wchar_t buffer[64]{};
        auto const length = GetEnvironmentVariableW(
            L"UMA_FAKE_ADB_DELAY_MS", buffer, static_cast<DWORD>(std::size(buffer)));
        if (length > 0 && length < std::size(buffer)) previous_ = buffer;
        SetEnvironmentVariableW(L"UMA_FAKE_ADB_DELAY_MS", value);
    }

    ~FakeAdbDelay()
    {
        SetEnvironmentVariableW(
            L"UMA_FAKE_ADB_DELAY_MS",
            previous_.empty() ? nullptr : previous_.c_str());
    }

    FakeAdbDelay(FakeAdbDelay const&) = delete;
    FakeAdbDelay& operator=(FakeAdbDelay const&) = delete;

private:
    std::wstring previous_;
};

[[nodiscard]] char const* source_root() noexcept
{
    return CMAKE_SOURCE_DIR;
}

[[nodiscard]] char const* fake_adb() noexcept
{
    return UMA_FAKE_ADB_PATH;
}

[[nodiscard]] UmaHandle make_handle(Collector& collector)
{
    REQUIRE(UmaLoadResource(source_root()) == 0);
    auto handle = UmaCreate(&Collector::callback, &collector);
    REQUIRE(handle != nullptr);
    return handle;
}

[[nodiscard]] UmaStartResult start_connect(UmaHandle handle)
{
    return UmaConnectAsync(handle, fake_adb(), "test-serial", "General");
}

void require_envelope(Message const& message, std::uint64_t operation_id)
{
    REQUIRE(message.details.at("version") == 1);
    REQUIRE(message.details.at("operation_id") == operation_id);
    REQUIRE(message.details.at("type").is_string());
    REQUIRE(message.details.at("payload").is_object());
    REQUIRE(message.details.size() == 4);
}

[[nodiscard]] std::string path_utf8(fs::path const& path)
{
    auto const value = path.u8string();
    return {
        reinterpret_cast<char const*>(value.data()),
        value.size(),
    };
}

class UnicodeResourceRoot
{
public:
    UnicodeResourceRoot()
        : path_(fs::temp_directory_path()
            / (L"uma-resource-\u9a6c-" + std::to_wstring(GetCurrentProcessId())))
    {
        fs::create_directories(path_ / L"resource");
        fs::copy_file(
            fs::path(CMAKE_SOURCE_DIR) / L"resource" / L"connection.json",
            path_ / L"resource" / L"connection.json",
            fs::copy_options::overwrite_existing);
    }

    ~UnicodeResourceRoot()
    {
        std::error_code error;
        fs::remove_all(path_, error);
    }

    [[nodiscard]] std::string utf8() const
    {
        return path_utf8(path_);
    }

private:
    fs::path path_;
};

}

TEST_CASE("C ABI rejects creation without a callback", "[UmaCaller][abi]")
{
    REQUIRE(UmaLoadResource(source_root()) == 0);
    REQUIRE(UmaCreate(nullptr, nullptr) == nullptr);
}

TEST_CASE("resource loading decodes UTF-8 Windows paths", "[UmaCaller][utf8]")
{
    UnicodeResourceRoot resource;
    auto const utf8_root = resource.utf8();
    REQUIRE(UmaLoadResource(utf8_root.c_str()) == 0);
}

TEST_CASE("synchronous validation failure emits no callback", "[UmaCaller][start]")
{
    Collector collector;
    auto handle = make_handle(collector);

    auto const result = UmaConnectAsync(handle, "missing.exe", "test-serial", "General");

    REQUIRE(result.operation_id == 0);
    REQUIRE(result.error_code == UMA_ERROR_ADB_EXECUTABLE_NOT_FOUND);
    std::this_thread::sleep_for(20ms);
    REQUIRE(collector.messages().empty());
    UmaDestroy(handle);
}

TEST_CASE("accepted connect emits ordered versioned callbacks", "[UmaCaller][connect]")
{
    FakeAdbDelay no_delay{L"0"};
    Collector collector;
    auto handle = make_handle(collector);

    auto const start = start_connect(handle);
    REQUIRE(start.error_code == UMA_SUCCESS);
    REQUIRE(start.operation_id != 0);
    REQUIRE(collector.wait_for_terminal());

    auto const messages = collector.messages();
    REQUIRE(messages.front().id == UMA_MSG_CONNECTION_STARTED);
    REQUIRE(messages.back().id == UMA_MSG_CONNECTION_SUCCEEDED);

    for (auto const& message : messages) require_envelope(message, start.operation_id);

    std::vector<std::string> const expected_types{
        "ConnectionStarted",
        "ConnectionProgress",
        "ConnectionProgress",
        "ConnectionProgress",
        "ConnectionProgress",
        "ConnectionProgress",
        "ConnectionProgress",
        "ConnectionSucceeded",
    };
    std::vector<std::string> const expected_phases{
        "adb_devices",
        "adb_get_state",
        "boot_poll",
        "android_id",
        "android_version",
        "wm_size",
    };
    REQUIRE(messages.size() == expected_types.size());
    for (std::size_t index = 0; index < messages.size(); ++index)
    {
        REQUIRE(messages[index].details.at("type") == expected_types[index]);
        if (index > 0 && index + 1 < messages.size())
        {
            REQUIRE(messages[index].id == UMA_MSG_CONNECTION_PROGRESS);
            REQUIRE(messages[index].details.at("payload").at("phase")
                    == expected_phases[index - 1]);
        }
    }
    REQUIRE(messages.front().details.at("payload").empty());

    auto const& payload = messages.back().details.at("payload");
    REQUIRE(payload.at("serial") == "test-serial");
    REQUIRE(payload.at("android_id") == "0123456789abcdef");
    REQUIRE(payload.at("android_version") == "14");
    REQUIRE(payload.at("width") == 1080);
    REQUIRE(payload.at("height") == 1920);
    REQUIRE(payload.at("physical_width") == 1080);
    REQUIRE(payload.at("physical_height") == 1920);
    REQUIRE(payload.at("size_source") == "physical");

    UmaDestroy(handle);
}

TEST_CASE("accepted connect survives pre-registration callbacks", "[UmaCaller][registration-buffer]")
{
    FakeAdbDelay no_delay{L"0"};
    RegistrationBuffer buffer;
    REQUIRE(UmaLoadResource(source_root()) == 0);
    auto handle = UmaCreate(&RegistrationBuffer::callback, &buffer);
    REQUIRE(handle != nullptr);

    auto const start = UmaConnectAsync(handle, fake_adb(), "test-serial", "General");
    REQUIRE(start.error_code == UMA_SUCCESS);
    REQUIRE(start.operation_id != 0);

    buffer.bind(start.operation_id);
    REQUIRE(buffer.wait_for_terminal());
    REQUIRE_FALSE(buffer.has_mismatched_operation());

    auto const messages = buffer.messages();
    REQUIRE_FALSE(messages.empty());
    REQUIRE(messages.front().id == UMA_MSG_CONNECTION_STARTED);
    REQUIRE(messages.back().id == UMA_MSG_CONNECTION_SUCCEEDED);
    REQUIRE(std::count_if(
        messages.begin(), messages.end(), [](Message const& message) {
            return Collector::is_terminal(message.id);
        }) == 1);
    for (auto const& message : messages)
    {
        require_envelope(message, start.operation_id);
    }

    UmaDestroy(handle);
}

TEST_CASE("concurrent start returns Busy without a second callback sequence", "[UmaCaller][busy]")
{
    FakeAdbDelay delay{L"1000"};
    Collector collector;
    auto handle = make_handle(collector);

    auto const first = start_connect(handle);
    auto const second = UmaConnectAsync(handle, fake_adb(), "test-serial", "General");

    REQUIRE(first.error_code == UMA_SUCCESS);
    REQUIRE(second.operation_id == 0);
    REQUIRE(second.error_code == UMA_ERROR_BUSY);
    REQUIRE(UmaCancelOperation(handle, first.operation_id) == 0);
    REQUIRE(collector.wait_for_terminal());

    auto const messages = collector.messages();
    auto const terminal_count = std::count_if(
        messages.begin(), messages.end(), [](Message const& message) {
            return Collector::is_terminal(message.id);
        });
    REQUIRE(terminal_count == 1);
    UmaDestroy(handle);
}

TEST_CASE("cancellation is idempotent and reports Canceled exactly once", "[UmaCaller][cancel]")
{
    FakeAdbDelay delay{L"5000"};
    Collector collector;
    auto handle = make_handle(collector);
    auto const start = start_connect(handle);

    REQUIRE(UmaCancelConnect(handle, start.operation_id) == 0);
    REQUIRE(UmaCancelOperation(handle, start.operation_id) == 0);
    REQUIRE(collector.wait_for_terminal());
    REQUIRE(UmaCancelOperation(handle, start.operation_id) == 0);
    REQUIRE(UmaCancelOperation(handle, start.operation_id + 1) == UMA_ERROR_INVALID_ARGUMENT);

    auto const messages = collector.messages();
    auto const terminals = std::count_if(
        messages.begin(), messages.end(), [](Message const& message) {
            return Collector::is_terminal(message.id);
        });
    REQUIRE(terminals == 1);
    REQUIRE(messages.back().id == UMA_MSG_CONNECTION_FAILED);
    REQUIRE(messages.back().details.at("payload").at("error_code") == UMA_ERROR_CANCELED);
    UmaDestroy(handle);
}

TEST_CASE("destroy joins active work and prevents later callbacks", "[UmaCaller][destroy]")
{
    FakeAdbDelay delay{L"5000"};
    Collector collector;
    auto handle = make_handle(collector);
    auto const start = start_connect(handle);
    REQUIRE(start.error_code == UMA_SUCCESS);

    UmaDestroy(handle);
    auto const count_after_destroy = collector.messages().size();
    std::this_thread::sleep_for(50ms);
    REQUIRE(collector.messages().size() == count_after_destroy);
}

TEST_CASE("cancellation remains idempotent for earlier terminal operations", "[UmaCaller][cancel]")
{
    FakeAdbDelay no_delay{L"0"};
    Collector collector;
    auto handle = make_handle(collector);
    auto const first = start_connect(handle);
    REQUIRE(collector.wait_for_terminal());
    collector.clear();
    auto const second = start_connect(handle);
    REQUIRE(collector.wait_for_terminal());

    REQUIRE(UmaCancelOperation(handle, second.operation_id) == 0);
    REQUIRE(UmaCancelOperation(handle, first.operation_id) == 0);
    UmaDestroy(handle);
}

TEST_CASE("unsupported S2 ABI functions fail synchronously", "[UmaCaller][s2-stub]")
{
    Collector collector;
    auto handle = make_handle(collector);
    std::uint8_t buffer[1]{};
    std::uint64_t size{};

    REQUIRE(UmaVerifyGameAsync(handle, "package").error_code == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaCaptureAsync(handle).error_code == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaGetFramePngSize(handle, 1, &size) == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaCopyFramePng(handle, 1, buffer, sizeof(buffer)) == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaReleaseFrame(handle, 1) == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaTapAsync(handle, 1, 10, 20).error_code == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaSwipeAsync(handle, 1, 10, 20, 30, 40, 500).error_code
            == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(collector.messages().empty());
    UmaDestroy(handle);
}
