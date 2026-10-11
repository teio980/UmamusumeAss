#include <catch2/catch_test_macros.hpp>

#include "UmaAssistant/UmaCaller.h"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <map>
#include <limits>
#include <optional>
#include <string>
#include <string_view>
#include <thread>
#include <type_traits>
#include <vector>

#include <windows.h>
#include <wincodec.h>
#include <wrl/client.h>

using namespace std::chrono_literals;
namespace fs = std::filesystem;

static_assert(sizeof(UmaDecodedImageInfo) == 32);
static_assert(sizeof(UmaImageTransformOptions) == 32);
static_assert(sizeof(UmaColorRule) == 96);
static_assert(sizeof(UmaImageRect) == 16);
static_assert(sizeof(UmaColorBlob) == 20);

namespace {

using CancelConnectSignature = std::int32_t(UMA_CALL*)(UmaHandle, std::uint64_t);
using CaptureSignature = UmaStartResult(UMA_CALL*)(UmaHandle);
using CopyFrameSignature = std::int32_t(UMA_CALL*)(
    UmaHandle, std::uint64_t, std::uint8_t*, std::uint64_t);
using SwipeSignature = UmaStartResult(UMA_CALL*)(
    UmaHandle, std::uint64_t, std::int32_t, std::int32_t,
    std::int32_t, std::int32_t, std::int32_t);
using TouchSignature = UmaStartResult(UMA_CALL*)(
    UmaHandle, std::uint64_t, std::int32_t, std::int32_t, std::int32_t);
using PrepareTouchSignature = UmaStartResult(UMA_CALL*)(UmaHandle, std::uint64_t);
using KeyEventSignature = UmaStartResult(UMA_CALL*)(UmaHandle, std::uint64_t, char const*);
using InputTextSignature = UmaStartResult(UMA_CALL*)(UmaHandle, std::uint64_t, char const*);
using PackageOperationSignature = UmaStartResult(UMA_CALL*)(
    UmaHandle, char const*, char const*, std::int32_t);

static_assert(std::is_same_v<decltype(&UmaCancelConnect), CancelConnectSignature>);
static_assert(std::is_same_v<decltype(&UmaCaptureAsync), CaptureSignature>);
static_assert(std::is_same_v<decltype(&UmaCopyFramePng), CopyFrameSignature>);
static_assert(std::is_same_v<decltype(&UmaSwipeAsync), SwipeSignature>);
static_assert(std::is_same_v<decltype(&UmaTouchAsync), TouchSignature>);
static_assert(std::is_same_v<decltype(&UmaPrepareTouchAsync), PrepareTouchSignature>);
static_assert(std::is_same_v<decltype(&UmaKeyEventAsync), KeyEventSignature>);
static_assert(std::is_same_v<decltype(&UmaInputTextAsync), InputTextSignature>);
static_assert(std::is_same_v<decltype(&UmaPackageOperationAsync), PackageOperationSignature>);
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

    [[nodiscard]] bool wait_for_message(
        std::int32_t message, std::chrono::milliseconds timeout)
    {
        std::unique_lock lock(mutex_);
        return cv_.wait_for(lock, timeout, [this, message] {
            return std::any_of(messages_.begin(), messages_.end(), [message](Message const& item) {
                return item.id == message;
            });
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
            || id == UMA_MSG_CONNECTION_FAILED
            || id == UMA_MSG_GAME_VERIFIED
            || id == UMA_MSG_GAME_VERIFICATION_FAILED
            || id == UMA_MSG_FRAME_CAPTURED
            || id == UMA_MSG_INPUT_SUCCEEDED
            || id == UMA_MSG_INPUT_FAILED
            || id == UMA_MSG_OPERATION_FAILED
            || id == UMA_MSG_PACKAGE_OPERATION_COMPLETED;
    }

    [[nodiscard]] bool wait_for_operation_terminal(
        std::uint64_t operation_id, std::chrono::milliseconds timeout = 10s)
    {
        std::unique_lock lock(mutex_);
        return cv_.wait_for(lock, timeout, [this, operation_id] {
            return std::any_of(messages_.begin(), messages_.end(), [operation_id](auto const& message) {
                return message.details.value("operation_id", std::uint64_t{}) == operation_id
                    && is_terminal(message.id);
            });
        });
    }

    [[nodiscard]] std::optional<Message> terminal_for(std::uint64_t operation_id) const
    {
        std::lock_guard lock(mutex_);
        auto const found = std::find_if(messages_.rbegin(), messages_.rend(), [operation_id](auto const& message) {
            return message.details.value("operation_id", std::uint64_t{}) == operation_id
                && is_terminal(message.id);
        });
        return found == messages_.rend() ? std::nullopt : std::optional<Message>{*found};
    }

private:
    mutable std::mutex mutex_;
    std::condition_variable cv_;
    std::vector<Message> messages_;
};

static void prepare_held_touch(Collector& collector, UmaHandle handle, std::uint64_t frame_id)
{
    auto const prepared = UmaPrepareTouchAsync(handle, frame_id);
    REQUIRE(prepared.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(prepared.operation_id));
    REQUIRE(collector.terminal_for(prepared.operation_id)->id == UMA_MSG_INPUT_SUCCEEDED);
}

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

class ReentrantDestroyer
{
public:
    explicit ReentrantDestroyer(std::int32_t destroy_on_message)
        : destroy_on_message_{destroy_on_message}
    {
    }

    ~ReentrantDestroyer() { if (handle_ != nullptr) UmaDestroy(handle_); }

    static void UMA_CALL callback(
        std::int32_t message, char const*, void* custom_arg)
    {
        auto& self = *static_cast<ReentrantDestroyer*>(custom_arg);
        if (self.destroyed_.load(std::memory_order_acquire))
        {
            self.callbacks_after_destroy_.fetch_add(1, std::memory_order_relaxed);
            return;
        }
        if (message == self.destroy_on_message_)
        {
            auto const handle = self.handle_;
            self.handle_ = nullptr;
            UmaDestroy(handle);
            self.destroyed_.store(true, std::memory_order_release);
            self.cv_.notify_all();
        }
    }

    [[nodiscard]] bool wait_for_destroy(std::chrono::milliseconds timeout = 10s)
    {
        std::unique_lock lock(mutex_);
        return cv_.wait_for(lock, timeout, [this] {
            return destroyed_.load(std::memory_order_acquire);
        });
    }

    UmaHandle handle_{};
    std::atomic<bool> destroyed_{};
    std::atomic<int> callbacks_after_destroy_{};
    std::mutex mutex_;
    std::condition_variable cv_;

private:
    std::int32_t destroy_on_message_{};
};

class ReentrantCaptureStarter
{
public:
    ~ReentrantCaptureStarter() { if (handle != nullptr) UmaDestroy(handle); }

    UmaHandle handle{};
    std::mutex mutex;
    std::condition_variable cv;
    std::optional<UmaStartResult> capture_start;
    std::vector<Message> messages;

    static void UMA_CALL callback(std::int32_t message, char const* json, void* custom_arg)
    {
        auto& self = *static_cast<ReentrantCaptureStarter*>(custom_arg);
        if (message == UMA_MSG_CONNECTION_SUCCEEDED)
        {
            auto const start = UmaCaptureAsync(self.handle);
            {
                std::lock_guard lock(self.mutex);
                self.capture_start = start;
            }
            self.cv.notify_all();
        }
        auto details = nlohmann::json::parse(json);
        {
            std::lock_guard lock(self.mutex);
            self.messages.push_back({message, std::move(details)});
        }
        self.cv.notify_all();
    }
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

class FakeAdbSettings
{
public:
    void set(std::wstring name, std::wstring value)
    {
        if (!previous_.contains(name))
        {
            std::wstring previous(32768, L'\0');
            auto const size = GetEnvironmentVariableW(
                name.c_str(), previous.data(), static_cast<DWORD>(previous.size()));
            if (size > 0 && size < previous.size())
            {
                previous.resize(size);
                previous_[name] = std::move(previous);
            }
            else previous_[name] = std::nullopt;
        }
        SetEnvironmentVariableW(name.c_str(), value.c_str());
    }

    ~FakeAdbSettings()
    {
        for (auto const& [name, value] : previous_)
            SetEnvironmentVariableW(name.c_str(), value ? value->c_str() : nullptr);
    }

private:
    std::map<std::wstring, std::optional<std::wstring>> previous_;
};

[[nodiscard]] std::vector<std::uint8_t> read_binary_file(fs::path const& path)
{
    std::ifstream input(path, std::ios::binary);
    return {std::istreambuf_iterator<char>{input}, std::istreambuf_iterator<char>{}};
}

[[nodiscard]] bool write_solid_png(fs::path const& path, UINT width, UINT height)
{
    HRESULT const com_result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    bool const initialized = SUCCEEDED(com_result);
    if (FAILED(com_result) && com_result != RPC_E_CHANGED_MODE) return false;
    struct ComCleanup
    {
        bool initialized;
        ~ComCleanup() { if (initialized) CoUninitialize(); }
    } com_cleanup{initialized};

    using Microsoft::WRL::ComPtr;
    ComPtr<IWICImagingFactory> factory;
    HRESULT result = CoCreateInstance(
        CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(&factory));
    if (FAILED(result)) return false;
    ComPtr<IWICStream> stream;
    result = factory->CreateStream(&stream);
    if (FAILED(result)) return false;
    result = stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE);
    if (FAILED(result)) return false;
    ComPtr<IWICBitmapEncoder> encoder;
    result = factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder);
    if (FAILED(result)) return false;
    result = encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache);
    if (FAILED(result)) return false;
    ComPtr<IWICBitmapFrameEncode> frame;
    result = encoder->CreateNewFrame(&frame, nullptr);
    if (FAILED(result)) return false;
    result = frame->Initialize(nullptr);
    if (FAILED(result)) return false;
    result = frame->SetSize(width, height);
    if (FAILED(result)) return false;
    WICPixelFormatGUID format = GUID_WICPixelFormat32bppBGRA;
    result = frame->SetPixelFormat(&format);
    if (FAILED(result) || !IsEqualGUID(format, GUID_WICPixelFormat32bppBGRA)) return false;
    std::size_t const stride = static_cast<std::size_t>(width) * 4;
    std::vector<std::uint8_t> pixels(stride * height, 0xff);
    result = frame->WritePixels(
        height, static_cast<UINT>(stride), static_cast<UINT>(pixels.size()), pixels.data());
    if (FAILED(result)) return false;
    result = frame->Commit();
    if (FAILED(result)) return false;
    return SUCCEEDED(encoder->Commit());
}

[[nodiscard]] std::optional<std::vector<std::uint8_t>> decode_png_bgra(
    std::vector<std::uint8_t> const& bytes, UINT& width, UINT& height)
{
    HRESULT const com_result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    bool const initialized = SUCCEEDED(com_result);
    if (FAILED(com_result) && com_result != RPC_E_CHANGED_MODE) return std::nullopt;
    struct ComCleanup
    {
        bool initialized;
        ~ComCleanup() { if (initialized) CoUninitialize(); }
    } com_cleanup{initialized};
    using Microsoft::WRL::ComPtr;
    ComPtr<IWICImagingFactory> factory;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr,
            CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)))) return std::nullopt;
    ComPtr<IWICStream> stream;
    if (FAILED(factory->CreateStream(&stream))
        || FAILED(stream->InitializeFromMemory(
            const_cast<BYTE*>(bytes.data()), static_cast<DWORD>(bytes.size()))))
        return std::nullopt;
    ComPtr<IWICBitmapDecoder> decoder;
    if (FAILED(factory->CreateDecoderFromStream(
            stream.Get(), nullptr, WICDecodeMetadataCacheOnLoad, &decoder)))
        return std::nullopt;
    ComPtr<IWICBitmapFrameDecode> frame;
    if (FAILED(decoder->GetFrame(0, &frame)) || FAILED(frame->GetSize(&width, &height)))
        return std::nullopt;
    ComPtr<IWICFormatConverter> converter;
    if (FAILED(factory->CreateFormatConverter(&converter))
        || FAILED(converter->Initialize(
            frame.Get(), GUID_WICPixelFormat32bppBGRA,
            WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom)))
        return std::nullopt;
    auto const stride = static_cast<UINT64>(width) * 4;
    auto const byte_count = stride * height;
    if (byte_count > 64 * 1024 * 1024) return std::nullopt;
    std::vector<std::uint8_t> pixels(static_cast<std::size_t>(byte_count));
    if (FAILED(converter->CopyPixels(
            nullptr, static_cast<UINT>(stride), static_cast<UINT>(pixels.size()), pixels.data())))
        return std::nullopt;
    return pixels;
}

class UmaHandleGuard
{
public:
    explicit UmaHandleGuard(UmaHandle handle) : handle_{handle} { }
    ~UmaHandleGuard() { if (handle_ != nullptr) UmaDestroy(handle_); }
    UmaHandleGuard(UmaHandleGuard const&) = delete;
    UmaHandleGuard& operator=(UmaHandleGuard const&) = delete;
    [[nodiscard]] UmaHandle get() const noexcept { return handle_; }
    [[nodiscard]] UmaHandle release() noexcept
    {
        auto const result = handle_;
        handle_ = nullptr;
        return result;
    }
private:
    UmaHandle handle_{};
};

class FakeAdbOfflineThenReady
{
public:
    FakeAdbOfflineThenReady()
        : state_path_(fs::temp_directory_path()
            / (L"uma-fake-adb-state-" + std::to_wstring(GetCurrentProcessId()) + L".txt"))
    {
        std::error_code error;
        fs::remove(state_path_, error);
        SetEnvironmentVariableW(L"UMA_FAKE_ADB_OFFLINE_THEN_READY", L"1");
        SetEnvironmentVariableW(L"UMA_FAKE_ADB_STATE_FILE", state_path_.c_str());
    }

    ~FakeAdbOfflineThenReady()
    {
        SetEnvironmentVariableW(L"UMA_FAKE_ADB_OFFLINE_THEN_READY", nullptr);
        SetEnvironmentVariableW(L"UMA_FAKE_ADB_STATE_FILE", nullptr);
        std::error_code error;
        fs::remove(state_path_, error);
    }

    FakeAdbOfflineThenReady(FakeAdbOfflineThenReady const&) = delete;
    FakeAdbOfflineThenReady& operator=(FakeAdbOfflineThenReady const&) = delete;

private:
    fs::path state_path_;
};

[[nodiscard]] char const* source_root() noexcept
{
    return CMAKE_SOURCE_DIR;
}

[[nodiscard]] char const* fake_adb() noexcept
{
    return UMA_FAKE_ADB_PATH;
}

[[nodiscard]] fs::path test_artifact(std::wstring const& name)
{
    return fs::path(source_root()) / "build" / "msvc-debug" / name;
}

[[nodiscard]] UmaHandle make_handle(Collector& collector)
{
    REQUIRE(UmaSetUserDir(source_root()) == 0);
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
        std::ofstream ofs(path_ / L"resource" / L"connection.json");
        ofs << R"({"connection":[{"configName":"General","baseConfig":null,)"
               R"("commands":{"get_size":["-s","[AdbSerial]","shell","wm","size"]}}]})";
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
    REQUIRE(UmaSetUserDir(source_root()) == 0);
    REQUIRE(UmaLoadResource(source_root()) == 0);
    REQUIRE(UmaCreate(nullptr, nullptr) == nullptr);
}

TEST_CASE("C ABI exposes native template matching for caller-owned image planes", "[UmaCaller][abi][recognition]")
{
    Collector collector;
    UmaHandleGuard handle{make_handle(collector)};
    std::array<std::uint8_t, 9> screen_pixels{100, 100, 100, 100, 0, 64, 100, 128, 255};
    std::array<std::uint8_t, 4> template_pixels{0, 64, 128, 255};
    UmaImageView screen{
        3, 3, screen_pixels.data(), screen_pixels.size(), nullptr, 0};
    UmaImageView templ{
        2, 2, template_pixels.data(), template_pixels.size(), nullptr, 0};
    UmaTemplateMatchOptions options{};
    options.method = UMA_MATCH_TEMPLATE_GRAY;
    options.has_roi = 1;
    options.roi_x = 1;
    options.roi_y = 1;
    options.roi_width = 2;
    options.roi_height = 2;
    options.threshold = 0.99;
    options.reference_width = 3;
    options.reference_height = 3;
    UmaTemplateMatchResult result{};

    REQUIRE(UmaMatchTemplate(handle.get(), &screen, &templ, &options, &result) == UMA_SUCCESS);
    REQUIRE(result.found == 1);
    REQUIRE(result.x == 1);
    REQUIRE(result.y == 1);
    REQUIRE(result.width == 2);
    REQUIRE(result.height == 2);

    screen.gray_length = 8;
    REQUIRE(UmaMatchTemplate(handle.get(), &screen, &templ, &options, &result)
        == UMA_ERROR_INVALID_ARGUMENT);
}

TEST_CASE("C ABI clamps extreme ROI and scale inputs before integer conversion", "[UmaCaller][abi][recognition]")
{
    Collector collector;
    UmaHandleGuard handle{make_handle(collector)};
    std::array<std::uint8_t, 9> screen_pixels{100, 5, 100, 5, 5, 5, 5, 5, 5};
    std::array<std::uint8_t, 1> template_pixels{100};
    UmaImageView screen{3, 3, screen_pixels.data(), screen_pixels.size(), nullptr, 0};
    UmaImageView templ{1, 1, template_pixels.data(), template_pixels.size(), nullptr, 0};
    UmaTemplateMatchOptions options{};
    options.method = UMA_MATCH_TEMPLATE_GRAY;
    options.has_roi = 1;
    options.roi_x = (std::numeric_limits<std::int32_t>::max)();
    options.roi_y = (std::numeric_limits<std::int32_t>::min)();
    options.roi_width = (std::numeric_limits<std::int32_t>::max)();
    options.roi_height = (std::numeric_limits<std::int32_t>::max)();
    options.threshold = 1.0;
    options.reference_width = 1;
    options.reference_height = 1;
    UmaTemplateMatchResult result{};

    REQUIRE(UmaMatchTemplate(handle.get(), &screen, &templ, &options, &result) == UMA_SUCCESS);
    CHECK(result.found == 1);
    CHECK(result.x == 2);
    CHECK(result.y == 0);

    auto const huge_scale = (std::numeric_limits<double>::max)();
    options.method = UMA_MATCH_TEMPLATE_SCALED;
    options.has_roi = 0;
    options.reference_width = 3;
    options.reference_height = 3;
    options.scales = &huge_scale;
    options.scale_count = 1;
    REQUIRE(UmaMatchTemplate(handle.get(), &screen, &templ, &options, &result) == UMA_SUCCESS);
    CHECK(result.found == 0);

    options.method = UMA_MATCH_TEMPLATE_SCALED_MASKED;
    std::array<std::uint8_t, 4> template_rgba{100, 100, 100, 255};
    templ.rgba_pixels = template_rgba.data();
    templ.rgba_length = template_rgba.size();
    REQUIRE(UmaMatchTemplate(handle.get(), &screen, &templ, &options, &result) == UMA_SUCCESS);
    CHECK(result.found == 0);
}

TEST_CASE("resource loading decodes UTF-8 Windows paths", "[UmaCaller][utf8]")
{
    UnicodeResourceRoot resource;
    auto const utf8_root = resource.utf8();
    REQUIRE(UmaLoadResource(utf8_root.c_str()) == 0);
}

TEST_CASE("an existing idle handle uses the latest successful resource on its next connection", "[UmaCaller][resource]")
{
    FakeAdbDelay no_delay{L"0"};
    FakeAdbSettings settings;
    auto const root = fs::temp_directory_path()
        / (L"uma-resource-refresh-" + std::to_wstring(GetCurrentProcessId()));
    auto const first_root = root / L"first";
    auto const second_root = root / L"second";
    auto const command_log = test_artifact(L"fake-adb-resource-refresh.tsv");
    std::error_code ignored;
    fs::remove_all(root, ignored);
    fs::remove(command_log, ignored);
    fs::create_directories(first_root / L"resource");
    fs::create_directories(second_root / L"resource");
    auto write_profile = [](fs::path const& directory, std::string const& marker) {
        nlohmann::json const document = {
            {"connection", nlohmann::json::array({nlohmann::json{
                {"configName", "General"},
                {"commands", {
                    {"list_devices", {"devices"}},
                    {"get_state", {"-s", "[AdbSerial]", "get-state", marker}},
                    {"connect", {"connect", "[AdbSerial]"}},
                    {"boot_completed", {"-s", "[AdbSerial]", "shell", "getprop", "sys.boot_completed"}},
                    {"android_id", {"-s", "[AdbSerial]", "shell", "settings", "get", "secure", "android_id"}},
                    {"android_version", {"-s", "[AdbSerial]", "shell", "getprop", "ro.build.version.release"}},
                    {"get_size", {"-s", "[AdbSerial]", "shell", "wm", "size"}},
                }},
            }})},
        };
        std::ofstream output(directory / L"resource" / L"connection.json", std::ios::binary);
        output << document.dump();
    };
    write_profile(first_root, "resource-version-one");
    write_profile(second_root, "resource-version-two");

    auto const first_root_utf8 = first_root.string();
    auto const second_root_utf8 = second_root.string();
    REQUIRE(UmaSetUserDir(first_root_utf8.c_str()) == UMA_SUCCESS);
    REQUIRE(UmaLoadResource(first_root_utf8.c_str()) == UMA_SUCCESS);
    settings.set(L"UMA_FAKE_ADB_COMMANDS_FILE", command_log.wstring());
    Collector collector;
    UmaHandleGuard handle_guard{UmaCreate(&Collector::callback, &collector)};
    REQUIRE(handle_guard.get() != nullptr);

    REQUIRE(UmaLoadResource(second_root_utf8.c_str()) == UMA_SUCCESS);
    auto const second_connect = UmaConnectAsync(
        handle_guard.get(), fake_adb(), "test-serial", "General");
    REQUIRE(second_connect.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_terminal());
    REQUIRE(collector.messages().back().id == UMA_MSG_CONNECTION_SUCCEEDED);
    auto second_log_bytes = read_binary_file(command_log);
    std::string second_log(second_log_bytes.begin(), second_log_bytes.end());
    REQUIRE(second_log.find("resource-version-two") != std::string::npos);
    REQUIRE(second_log.find("resource-version-one") == std::string::npos);

    collector.clear();
    REQUIRE(UmaLoadResource(first_root_utf8.c_str()) == UMA_SUCCESS);
    auto const first_connect = UmaConnectAsync(
        handle_guard.get(), fake_adb(), "test-serial", "General");
    REQUIRE(first_connect.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_terminal());
    REQUIRE(collector.messages().back().id == UMA_MSG_CONNECTION_SUCCEEDED);
    auto first_log_bytes = read_binary_file(command_log);
    std::string first_log(first_log_bytes.begin(), first_log_bytes.end());
    REQUIRE(first_log.find("resource-version-one") != std::string::npos);
    fs::remove(command_log, ignored);
    fs::remove_all(root, ignored);
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

TEST_CASE("capture probes and caches raw, gzip, PNG, and shell screenshot routes", "[UmaCaller][device][screenshot]")
{
    FakeAdbDelay no_delay{L"0"};
    FakeAdbSettings settings;
    auto const command_log = test_artifact(L"fake-adb-capture-routes.tsv");
    auto const screenshot_path = test_artifact(L"capture-shell-route.png");
    std::error_code ignored;
    fs::remove(command_log, ignored);
    fs::remove(screenshot_path, ignored);
    REQUIRE(write_solid_png(screenshot_path, 90, 160));
    settings.set(L"UMA_FAKE_ADB_RESOLUTION", L"90x160");
    settings.set(L"UMA_FAKE_ADB_COMMANDS_FILE", command_log.wstring());
    settings.set(L"UMA_FAKE_ADB_SCREENSHOT_PATH", screenshot_path.wstring());
    settings.set(L"UMA_FAKE_ADB_CAPTURE_MODE", L"raw");
    Collector collector;
    UmaHandleGuard handle_guard{make_handle(collector)};
    auto const handle = handle_guard.get();

    auto const connection = start_connect(handle);
    REQUIRE(connection.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_terminal());
    REQUIRE(collector.messages().back().id == UMA_MSG_CONNECTION_SUCCEEDED);

    auto capture_and_release = [&]() {
        auto const start = UmaCaptureAsync(handle);
        REQUIRE(start.error_code == UMA_SUCCESS);
        REQUIRE(collector.wait_for_operation_terminal(start.operation_id));
        auto const event = collector.terminal_for(start.operation_id);
        REQUIRE(event.has_value());
        INFO(event->details.dump());
        REQUIRE(event->id == UMA_MSG_FRAME_CAPTURED);
        REQUIRE(event->details.at("payload").at("width") == 90);
        REQUIRE(event->details.at("payload").at("height") == 160);
        auto const frame_id = event->details.at("payload").at("frame_id").get<std::uint64_t>();
        std::uint64_t size = 0;
        REQUIRE(UmaGetFramePngSize(handle, frame_id, &size) == UMA_SUCCESS);
        REQUIRE(size > 64);
        std::vector<std::uint8_t> png(static_cast<std::size_t>(size));
        REQUIRE(UmaCopyFramePng(handle, frame_id, png.data(), size) == UMA_SUCCESS);
        REQUIRE(png.size() >= 8);
        REQUIRE(png[0] == 137);
        REQUIRE(png[1] == 'P');
        REQUIRE(png[2] == 'N');
        REQUIRE(png[3] == 'G');
        REQUIRE(UmaReleaseFrame(handle, frame_id) == UMA_SUCCESS);
    };

    capture_and_release();
    settings.set(L"UMA_FAKE_ADB_CAPTURE_MODE", L"gzip");
    capture_and_release();
    settings.set(L"UMA_FAKE_ADB_CAPTURE_MODE", L"shell-png");
    capture_and_release();
    capture_and_release();

    auto const log_bytes = read_binary_file(command_log);
    std::string const log(log_bytes.begin(), log_bytes.end());
    REQUIRE(log.find("exec-out\tscreencap") != std::string::npos);
    REQUIRE(log.find("shell\tsh\t-c\t'screencap | gzip -1'") != std::string::npos);
    REQUIRE(log.find("shell\tscreencap\t-p") != std::string::npos);
    fs::remove(command_log, ignored);
    fs::remove(screenshot_path, ignored);
}

TEST_CASE("instance options select a display and preserve raw screenshot pixels", "[UmaCaller][device][options][screenshot]")
{
    FakeAdbDelay no_delay{L"0"};
    FakeAdbSettings settings;
    auto const command_log = test_artifact(L"fake-adb-instance-options.tsv");
    std::error_code ignored;
    fs::remove(command_log, ignored);
    settings.set(L"UMA_FAKE_ADB_RESOLUTION", L"900x1600");
    settings.set(L"UMA_FAKE_ADB_DISPLAY_RESOLUTION", L"90x160");
    settings.set(L"UMA_FAKE_ADB_DISPLAY_ID", L"7");
    settings.set(L"UMA_FAKE_ADB_CAPTURE_MODE", L"raw");
    settings.set(L"UMA_FAKE_ADB_COMMANDS_FILE", command_log.wstring());

    Collector collector;
    UmaHandleGuard handle_guard{make_handle(collector)};
    auto const handle = handle_guard.get();
    REQUIRE(UmaSetInstanceOptions(handle, R"({"version":1,"unknown":0})") == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaSetInstanceOptions(handle, R"({"version":1.5})") == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaSetInstanceOptions(handle, R"({"version":429496729600000000000000000000000000000})") == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaSetInstanceOptions(handle, R"({"version":1,"display_id":-1})") == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaSetInstanceOptions(handle, R"({"version":1,"display_id":7})") == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaSetInstanceOptions(handle, R"({"version":1,"display_id":7,"surface_display_id":"0x7"})") == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaSetInstanceOptions(handle, R"({"version":1,"swipe_duration_multiplier":0})") == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaSetInstanceOptions(handle, R"({"version":1,"extra_swipe_distance":-1})") == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaSetInstanceOptions(handle, R"({"version":1,"screenshot_method":"not-a-method"})") == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaSetInstanceOptions(handle,
        R"({"version":1,"touch_backend":"minitouch"})")
        == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaSetInstanceOptions(handle,
        R"({"version":1,"touch_backend":"minitouch","touch_binary_path":"C:\\touch","touch_remote_path":"/data/local/tmp/"})")
        == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaSetInstanceOptions(handle,
        R"({"version":1,"touch_backend":"maatouch","touch_binary_path":"C:\\touch","touch_remote_path":"/data/local/tmp/."})")
        == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaSetInstanceOptions(handle,
        R"({"version":1,"touch_backend":"maatouch","touch_binary_path":"C:\\touch","touch_remote_path":"/data/local/tmp/sub//touch.apk"})")
        == UMA_ERROR_INVALID_ARGUMENT);

    auto const connection = start_connect(handle);
    REQUIRE(connection.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_terminal());
    REQUIRE(collector.messages().back().id == UMA_MSG_CONNECTION_SUCCEEDED);
    REQUIRE(UmaSetInstanceOptions(
        handle, R"({"version":1,"display_id":7,"surface_display_id":"7","screenshot_method":"raw"})") == UMA_SUCCESS);

    auto const capture = UmaCaptureAsync(handle);
    REQUIRE(capture.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(capture.operation_id));
    auto const event = collector.terminal_for(capture.operation_id);
    REQUIRE(event.has_value());
    INFO(event->details.dump());
    REQUIRE(event->id == UMA_MSG_FRAME_CAPTURED);
    REQUIRE(event->details.at("payload").at("width") == 90);
    REQUIRE(event->details.at("payload").at("height") == 160);
    auto const frame_id = event->details.at("payload").at("frame_id").get<std::uint64_t>();
    std::uint64_t size = 0;
    REQUIRE(UmaGetFramePngSize(handle, frame_id, &size) == UMA_SUCCESS);
    std::vector<std::uint8_t> png(static_cast<std::size_t>(size));
    REQUIRE(UmaCopyFramePng(handle, frame_id, png.data(), size) == UMA_SUCCESS);
    UINT width = 0;
    UINT height = 0;
    auto const bgra = decode_png_bgra(png, width, height);
    REQUIRE(bgra.has_value());
    REQUIRE(width == 90);
    REQUIRE(height == 160);
    REQUIRE(bgra->size() == 90u * 160u * 4u);
    REQUIRE((*bgra)[0] == 0xa5);
    REQUIRE((*bgra)[1] == 0x5a);
    REQUIRE((*bgra)[2] == 0x00);
    REQUIRE((*bgra)[3] == 0xff);
    REQUIRE((*bgra)[4] == 0xba);
    REQUIRE((*bgra)[5] == 0x45);
    REQUIRE((*bgra)[6] == 0x1f);
    REQUIRE((*bgra)[7] == 0xff);
    settings.set(L"UMA_FAKE_ADB_CAPTURE_MODE", L"gzip");
    REQUIRE(UmaSetInstanceOptions(handle,
        R"({"version":1,"display_id":7,"surface_display_id":"7","screenshot_method":"gzip"})")
        == UMA_SUCCESS);
    auto const gzip_capture = UmaCaptureAsync(handle);
    REQUIRE(gzip_capture.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(gzip_capture.operation_id));
    auto const gzip_event = collector.terminal_for(gzip_capture.operation_id);
    REQUIRE(gzip_event->id == UMA_MSG_FRAME_CAPTURED);
    REQUIRE(gzip_event->details.at("payload").at("width") == 90);
    REQUIRE(gzip_event->details.at("payload").at("height") == 160);
    auto const gzip_frame = gzip_event->details.at("payload").at("frame_id").get<std::uint64_t>();
    REQUIRE(UmaReleaseFrame(handle, gzip_frame) == UMA_SUCCESS);
    REQUIRE(UmaSetInstanceOptions(handle, R"({"version":1})") == UMA_SUCCESS);
    auto const stale_tap = UmaTapAsync(handle, frame_id, 540, 960);
    REQUIRE(stale_tap.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(stale_tap.operation_id));
    auto const stale_event = collector.terminal_for(stale_tap.operation_id);
    REQUIRE(stale_event.has_value());
    REQUIRE(stale_event->id == UMA_MSG_INPUT_FAILED);
    REQUIRE(stale_event->details.at("payload").at("error_code") == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaReleaseFrame(handle, frame_id) == UMA_ERROR_INVALID_ARGUMENT);

    auto const log_bytes = read_binary_file(command_log);
    std::string const log(log_bytes.begin(), log_bytes.end());
    REQUIRE(log.find("shell\twm\tsize\t-d\t7") != std::string::npos);
    REQUIRE(log.find("shell\tdumpsys\tdisplay") != std::string::npos);
    REQUIRE(log.find("exec-out\tscreencap\t-d\t7") != std::string::npos);
    REQUIRE(log.find("shell\tsh\t-c\t'screencap -d 7 | gzip -1'") != std::string::npos);
    REQUIRE(log.find("shell\tinput\ttap") == std::string::npos);
    fs::remove(command_log, ignored);
}

TEST_CASE("raw CRLF is normalized while truncated raw and gzip frames are rejected", "[UmaCaller][device][screenshot][codec]")
{
    FakeAdbDelay no_delay{L"0"};
    FakeAdbSettings settings;
    // Width 10 encodes 0x0A in the little-endian raw header. The simulated
    // Windows text-mode stream inserts CR before that LF, so only the legacy
    // normalization fallback can recover this actual frame.
    settings.set(L"UMA_FAKE_ADB_RESOLUTION", L"10x16");
    settings.set(L"UMA_FAKE_ADB_CAPTURE_MODE", L"raw-crlf");
    Collector collector;
    UmaHandleGuard handle_guard{make_handle(collector)};
    auto const handle = handle_guard.get();
    auto const connection = start_connect(handle);
    REQUIRE(connection.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_terminal());
    REQUIRE(collector.messages().back().id == UMA_MSG_CONNECTION_SUCCEEDED);

    auto capture = [&](std::string const& method) -> Message {
        auto const options = std::string{"{\"version\":1,\"screenshot_method\":\""}
            + method + "\"}";
        REQUIRE(UmaSetInstanceOptions(handle, options.c_str()) == UMA_SUCCESS);
        auto const started = UmaCaptureAsync(handle);
        REQUIRE(started.error_code == UMA_SUCCESS);
        REQUIRE(collector.wait_for_operation_terminal(started.operation_id));
        auto const terminal = collector.terminal_for(started.operation_id);
        REQUIRE(terminal.has_value());
        return *terminal;
    };

    auto const crlf = capture("raw");
    REQUIRE(crlf.id == UMA_MSG_FRAME_CAPTURED);
    auto const frame_id = crlf.details.at("payload").at("frame_id").get<std::uint64_t>();
    std::uint64_t png_size = 0;
    REQUIRE(UmaGetFramePngSize(handle, frame_id, &png_size) == UMA_SUCCESS);
    std::vector<std::uint8_t> png(static_cast<std::size_t>(png_size));
    REQUIRE(UmaCopyFramePng(handle, frame_id, png.data(), png_size) == UMA_SUCCESS);
    UINT decoded_width = 0;
    UINT decoded_height = 0;
    auto const bgra = decode_png_bgra(png, decoded_width, decoded_height);
    REQUIRE(bgra.has_value());
    REQUIRE(decoded_width == 10);
    REQUIRE(decoded_height == 16);
    REQUIRE(bgra->size() == 10u * 16u * 4u);
    REQUIRE((*bgra)[0] == 0xa5);
    REQUIRE((*bgra)[1] == 0x5a);
    REQUIRE((*bgra)[2] == 0x00);
    REQUIRE((*bgra)[3] == 0xff);
    REQUIRE((*bgra)[4] == 0xba);
    REQUIRE((*bgra)[5] == 0x45);
    REQUIRE((*bgra)[6] == 0x1f);
    REQUIRE((*bgra)[7] == 0xff);
    REQUIRE(UmaReleaseFrame(handle, frame_id) == UMA_SUCCESS);

    settings.set(L"UMA_FAKE_ADB_CAPTURE_MODE", L"raw-truncated");
    auto const raw_bad = capture("raw");
    REQUIRE(raw_bad.id == UMA_MSG_OPERATION_FAILED);
    REQUIRE(raw_bad.details.at("payload").at("error_code") == UMA_ERROR_INVALID_DEVICE_RESPONSE);

    settings.set(L"UMA_FAKE_ADB_CAPTURE_MODE", L"gzip-truncated");
    auto const gzip_bad = capture("gzip");
    REQUIRE(gzip_bad.id == UMA_MSG_OPERATION_FAILED);
    REQUIRE(gzip_bad.details.at("payload").at("error_code") == UMA_ERROR_INVALID_DEVICE_RESPONSE);

    settings.set(L"UMA_FAKE_ADB_CAPTURE_MODE", L"gzip-corrupt");
    auto const gzip_corrupt = capture("gzip");
    REQUIRE(gzip_corrupt.id == UMA_MSG_OPERATION_FAILED);
    REQUIRE(gzip_corrupt.details.at("payload").at("error_code") == UMA_ERROR_INVALID_DEVICE_RESPONSE);
}

TEST_CASE("device operation can start from the connection completion callback", "[UmaCaller][reentrant]")
{
    FakeAdbDelay no_delay{L"0"};
    FakeAdbSettings settings;
    settings.set(L"UMA_FAKE_ADB_RESOLUTION", L"900x1600");
    settings.set(L"UMA_FAKE_ADB_ORIENTATION", L"0");
    ReentrantCaptureStarter context;
    REQUIRE(UmaSetUserDir(source_root()) == UMA_SUCCESS);
    REQUIRE(UmaLoadResource(source_root()) == UMA_SUCCESS);
    context.handle = UmaCreate(&ReentrantCaptureStarter::callback, &context);
    REQUIRE(context.handle != nullptr);

    auto const connection = UmaConnectAsync(
        context.handle, fake_adb(), "test-serial", "General");
    REQUIRE(connection.error_code == UMA_SUCCESS);
    {
        std::unique_lock lock(context.mutex);
        REQUIRE(context.cv.wait_for(lock, 10s, [&context] {
            return context.capture_start.has_value();
        }));
    }
    REQUIRE(context.capture_start->error_code == UMA_SUCCESS);
    {
        std::unique_lock lock(context.mutex);
        REQUIRE(context.cv.wait_for(lock, 10s, [&context] {
            if (!context.capture_start) return false;
            return std::any_of(context.messages.begin(), context.messages.end(), [&context](auto const& message) {
                return message.id == UMA_MSG_FRAME_CAPTURED
                    && message.details.value("operation_id", std::uint64_t{})
                        == context.capture_start->operation_id;
            });
        }));
    }
    UmaDestroy(context.handle);
    context.handle = nullptr;
}

TEST_CASE("destroying from connection callbacks suppresses later callbacks", "[UmaCaller][reentrant][destroy]")
{
    FakeAdbDelay no_delay{L"0"};
    for (auto const target : {UMA_MSG_CONNECTION_STARTED, UMA_MSG_CONNECTION_SUCCEEDED})
    {
        ReentrantDestroyer context{target};
        REQUIRE(UmaSetUserDir(source_root()) == UMA_SUCCESS);
        REQUIRE(UmaLoadResource(source_root()) == UMA_SUCCESS);
        context.handle_ = UmaCreate(&ReentrantDestroyer::callback, &context);
        REQUIRE(context.handle_ != nullptr);
        auto const start = UmaConnectAsync(
            context.handle_, fake_adb(), "test-serial", "General");
        REQUIRE(start.error_code == UMA_SUCCESS);
        REQUIRE(context.wait_for_destroy());
        REQUIRE(context.handle_ == nullptr);
        std::this_thread::sleep_for(50ms);
        REQUIRE(context.callbacks_after_destroy_.load(std::memory_order_relaxed) == 0);
    }
}

TEST_CASE("offline TCP endpoint recovers through fake ADB", "[UmaCaller][connect][retry]")
{
    FakeAdbOfflineThenReady scenario;
    Collector collector;
    UmaHandleGuard handle_guard{make_handle(collector)};
    auto handle = handle_guard.get();

    auto const start = UmaConnectAsync(
        handle, fake_adb(), "127.0.0.1:16384", "General");
    REQUIRE(start.error_code == UMA_SUCCESS);
    REQUIRE(start.operation_id != 0);
    REQUIRE(collector.wait_for_terminal());

    auto const messages = collector.messages();
    REQUIRE(messages.back().id == UMA_MSG_CONNECTION_SUCCEEDED);
    REQUIRE(messages.back().details.at("payload").at("serial") == "127.0.0.1:16384");
}

TEST_CASE("idle session recovery publishes distinct session generations", "[UmaCaller][device][recovery]")
{
    FakeAdbDelay no_delay{L"0"};
    FakeAdbSettings settings;
    auto const test_id = std::to_wstring(GetCurrentProcessId());
    auto const state_path = test_artifact(L"fake-adb-monitor-get-state-" + test_id + L".txt");
    auto const recovery_path = test_artifact(L"fake-adb-monitor-recovery-once-" + test_id + L".txt");
    auto const state_log_path = test_artifact(L"fake-adb-monitor-state-log-" + test_id + L".tsv");
    std::error_code ignored;
    fs::remove(state_path, ignored);
    fs::remove(recovery_path, ignored);
    fs::remove(state_log_path, ignored);
    settings.set(L"UMA_FAKE_ADB_RESOLUTION", L"900x1600");
    {
        std::ofstream state(state_path, std::ios::trunc);
        state << '0';
    }
    settings.set(L"UMA_FAKE_ADB_GET_STATE_FILE", state_path.wstring());
    settings.set(L"UMA_FAKE_ADB_RECOVERY_ONCE_FILE", recovery_path.wstring());
    settings.set(L"UMA_FAKE_ADB_STATE_LOG_FILE", state_log_path.wstring());

    Collector collector;
    UmaHandleGuard handle_guard{make_handle(collector)};
    auto const handle = handle_guard.get();
    auto const connection = start_connect(handle);
    REQUIRE(connection.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_terminal());
    REQUIRE(collector.messages().back().id == UMA_MSG_CONNECTION_SUCCEEDED);
    auto const original_generation = connection.operation_id;

    auto const capture = UmaCaptureAsync(handle);
    REQUIRE(capture.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(capture.operation_id));
    auto const captured = collector.terminal_for(capture.operation_id);
    REQUIRE(captured.has_value());
    INFO(captured->details.dump());
    REQUIRE(captured->id == UMA_MSG_FRAME_CAPTURED);
    auto const old_frame = captured->details.at("payload").at("frame_id").get<std::uint64_t>();

    // Enable the offline probe only after the captured frame is available, so
    // recovery cannot race the setup capture on slow hosts.
    fs::remove(recovery_path, ignored);
    settings.set(L"UMA_FAKE_ADB_MONITOR_MODE", L"recover_once");
    auto const received_reconnect = collector.wait_for_message(UMA_MSG_DEVICE_RECONNECTED, 12s);
    if (!received_reconnect)
    {
        auto const state_log_bytes = read_binary_file(state_log_path);
        INFO(std::string(state_log_bytes.begin(), state_log_bytes.end()));
        for (auto const& message : collector.messages())
            INFO(std::to_string(message.id) + ":" + message.details.dump());
    }
    REQUIRE(received_reconnect);
    auto messages = collector.messages();
    auto const reconnected = std::find_if(messages.begin(), messages.end(), [](Message const& item) {
        return item.id == UMA_MSG_DEVICE_RECONNECTED;
    });
    REQUIRE(reconnected != messages.end());
    REQUIRE(reconnected->details.at("operation_id") == 0);
    REQUIRE(reconnected->details.at("payload").at("previous_session_generation") == original_generation);
    auto const replacement_generation = reconnected->details.at("payload").at("session_generation").get<std::uint64_t>();
    REQUIRE(replacement_generation > original_generation);
    std::uint64_t old_frame_size = 0;
    REQUIRE(UmaGetFramePngSize(handle, old_frame, &old_frame_size) == UMA_ERROR_INVALID_ARGUMENT);

    settings.set(L"UMA_FAKE_ADB_MONITOR_MODE", L"offline");
    REQUIRE(collector.wait_for_message(UMA_MSG_DEVICE_DISCONNECTED, 12s));
    messages = collector.messages();
    auto const disconnected = std::find_if(messages.begin(), messages.end(), [](Message const& item) {
        return item.id == UMA_MSG_DEVICE_DISCONNECTED;
    });
    REQUIRE(disconnected != messages.end());
    REQUIRE(disconnected->details.at("operation_id") == 0);
    REQUIRE(disconnected->details.at("payload").at("session_generation") == replacement_generation);
    fs::remove(state_path, ignored);
    fs::remove(recovery_path, ignored);
    fs::remove(state_log_path, ignored);
}

TEST_CASE("a missing or offline listed device disconnects without TCP recovery", "[UmaCaller][device][recovery]")
{
    FakeAdbDelay no_delay{L"0"};
    FakeAdbSettings settings;
    auto const serial = std::string{"127.0.0.1:16384"};
    auto const serial_wide = std::wstring(serial.begin(), serial.end());
    settings.set(L"UMA_FAKE_ADB_DEVICES_SERIAL", serial_wide);

    for (auto const mode : {"missing", "offline"})
    {
        DYNAMIC_SECTION("adb devices state: " << mode)
        {
            auto const log_path = test_artifact(
                std::wstring{L"fake-adb-monitor-"} + std::wstring(mode, mode + std::char_traits<char>::length(mode))
                + L"-" + std::to_wstring(GetCurrentProcessId()) + L".tsv");
            std::error_code ignored;
            fs::remove(log_path, ignored);
            settings.set(L"UMA_FAKE_ADB_COMMANDS_FILE", log_path.wstring());
            settings.set(L"UMA_FAKE_ADB_DEVICES_MODE", L"ready");

            Collector collector;
            UmaHandleGuard handle_guard{make_handle(collector)};
            auto const start = UmaConnectAsync(
                handle_guard.get(), fake_adb(), serial.c_str(), "General");
            REQUIRE(start.error_code == UMA_SUCCESS);
            REQUIRE(collector.wait_for_terminal());
            REQUIRE(collector.messages().back().id == UMA_MSG_CONNECTION_SUCCEEDED);

            settings.set(
                L"UMA_FAKE_ADB_DEVICES_MODE",
                std::wstring(mode, mode + std::char_traits<char>::length(mode)));
            REQUIRE(collector.wait_for_message(UMA_MSG_DEVICE_DISCONNECTED, 12s));
            auto const messages = collector.messages();
            auto const disconnected = std::find_if(
                messages.begin(), messages.end(), [](Message const& message) {
                    return message.id == UMA_MSG_DEVICE_DISCONNECTED;
                });
            REQUIRE(disconnected != messages.end());
            REQUIRE(disconnected->details.at("operation_id") == 0);

            auto const log_bytes = read_binary_file(log_path);
            std::string const log(log_bytes.begin(), log_bytes.end());
            auto occurrences = [&log](std::string_view token) {
                std::size_t count = 0;
                for (auto position = log.find(token); position != std::string::npos;
                     position = log.find(token, position + token.size()))
                    ++count;
                return count;
            };
            // Initial connection checks get-state once. The monitor must see the
            // missing/offline adb devices row first and never start TCP recovery.
            CHECK(occurrences("get-state") == 1);
            CHECK(occurrences("connect") == 0);
            fs::remove(log_path, ignored);
        }
    }
}

TEST_CASE("a new connect cancels an in-flight idle recovery before publishing it", "[UmaCaller][device][recovery]")
{
    FakeAdbDelay no_delay{L"0"};
    FakeAdbSettings settings;
    auto const test_id = std::to_wstring(GetCurrentProcessId());
    auto const state_path = test_artifact(L"fake-adb-recovery-race-state-" + test_id + L".txt");
    auto const marker_path = test_artifact(L"fake-adb-recovery-race-marker-" + test_id + L".txt");
    std::error_code ignored;
    fs::remove(state_path, ignored);
    fs::remove(marker_path, ignored);
    {
        std::ofstream state(state_path, std::ios::trunc);
        state << '0';
    }
    settings.set(L"UMA_FAKE_ADB_GET_STATE_FILE", state_path.wstring());
    settings.set(L"UMA_FAKE_ADB_RECOVERY_MARKER", marker_path.wstring());
    settings.set(L"UMA_FAKE_ADB_MONITOR_MODE", L"recover_block");

    Collector collector;
    UmaHandleGuard handle_guard{make_handle(collector)};
    auto const original = start_connect(handle_guard.get());
    REQUIRE(original.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_terminal());
    REQUIRE(collector.messages().back().id == UMA_MSG_CONNECTION_SUCCEEDED);
    collector.clear();

    auto const deadline = std::chrono::steady_clock::now() + 15s;
    while (!fs::exists(marker_path) && std::chrono::steady_clock::now() < deadline)
        std::this_thread::sleep_for(20ms);
    REQUIRE(fs::exists(marker_path));

    settings.set(L"UMA_FAKE_ADB_MONITOR_MODE", L"normal");
    auto const replacement = start_connect(handle_guard.get());
    REQUIRE(replacement.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_terminal());
    auto const messages = collector.messages();
    REQUIRE(messages.back().id == UMA_MSG_CONNECTION_SUCCEEDED);
    CHECK(std::none_of(messages.begin(), messages.end(), [](Message const& message) {
        return message.id == UMA_MSG_DEVICE_RECONNECTED;
    }));
    fs::remove(state_path, ignored);
    fs::remove(marker_path, ignored);
}

TEST_CASE("destroy cancels an in-flight idle recovery without a late callback", "[UmaCaller][device][recovery]")
{
    FakeAdbDelay no_delay{L"0"};
    FakeAdbSettings settings;
    auto const test_id = std::to_wstring(GetCurrentProcessId());
    auto const state_path = test_artifact(L"fake-adb-destroy-recovery-state-" + test_id + L".txt");
    auto const marker_path = test_artifact(L"fake-adb-destroy-recovery-marker-" + test_id + L".txt");
    std::error_code ignored;
    fs::remove(state_path, ignored);
    fs::remove(marker_path, ignored);
    {
        std::ofstream state(state_path, std::ios::trunc);
        state << '0';
    }
    settings.set(L"UMA_FAKE_ADB_GET_STATE_FILE", state_path.wstring());
    settings.set(L"UMA_FAKE_ADB_RECOVERY_MARKER", marker_path.wstring());
    settings.set(L"UMA_FAKE_ADB_MONITOR_MODE", L"recover_block");

    Collector collector;
    UmaHandleGuard handle_guard{make_handle(collector)};
    auto const original = start_connect(handle_guard.get());
    REQUIRE(original.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_terminal());
    REQUIRE(collector.messages().back().id == UMA_MSG_CONNECTION_SUCCEEDED);
    collector.clear();

    auto const deadline = std::chrono::steady_clock::now() + 15s;
    while (!fs::exists(marker_path) && std::chrono::steady_clock::now() < deadline)
        std::this_thread::sleep_for(20ms);
    REQUIRE(fs::exists(marker_path));

    auto const raw_handle = handle_guard.release();
    auto const destroy_start = std::chrono::steady_clock::now();
    UmaDestroy(raw_handle);
    CHECK(std::chrono::steady_clock::now() - destroy_start < 5s);
    std::this_thread::sleep_for(50ms);
    CHECK(collector.messages().empty());
    fs::remove(state_path, ignored);
    fs::remove(marker_path, ignored);
}

TEST_CASE("accepted connect survives pre-registration callbacks", "[UmaCaller][registration-buffer]")
{
    FakeAdbDelay no_delay{L"0"};
    RegistrationBuffer buffer;
    REQUIRE(UmaSetUserDir(source_root()) == 0);
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

TEST_CASE("package lifecycle C ABI preserves launch resolution and process checks", "[UmaCaller][device][package]")
{
    FakeAdbSettings settings;
    auto const log_path = test_artifact(
        L"uma-fake-adb-package-commands-" + std::to_wstring(GetCurrentProcessId()) + L".txt");
    std::error_code fs_error;
    fs::remove(log_path, fs_error);
    settings.set(L"UMA_FAKE_ADB_COMMANDS_FILE", log_path.wstring());

    Collector collector;
    UmaHandleGuard handle{make_handle(collector)};
    auto const connection = start_connect(handle.get());
    REQUIRE(connection.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(connection.operation_id));
    REQUIRE(collector.terminal_for(connection.operation_id)->id == UMA_MSG_CONNECTION_SUCCEEDED);

    auto clear_log = [&] {
        std::ofstream clear(log_path, std::ios::trunc | std::ios::binary);
    };
    auto read_log = [&] {
        std::ifstream input(log_path, std::ios::binary);
        return std::string{std::istreambuf_iterator<char>{input}, std::istreambuf_iterator<char>{}};
    };
    auto run_package_operation = [&](std::int32_t operation, char const* activity = nullptr) {
        auto const start = UmaPackageOperationAsync(
            handle.get(), "com.example.game", activity, operation);
        REQUIRE(start.error_code == UMA_SUCCESS);
        REQUIRE(collector.wait_for_operation_terminal(start.operation_id));
        auto const terminal = collector.terminal_for(start.operation_id);
        REQUIRE(terminal.has_value());
        REQUIRE(terminal->id == UMA_MSG_PACKAGE_OPERATION_COMPLETED);
        REQUIRE(terminal->details.at("type") == "PackageOperationCompleted");
        REQUIRE(terminal->details.at("version") == 2);
        REQUIRE(terminal->details.at("payload").at("operation") == operation);
        return *terminal;
    };

    REQUIRE(UmaPackageOperationAsync(nullptr, "com.example.game", nullptr,
                                     UMA_PACKAGE_START).error_code == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaPackageOperationAsync(handle.get(), nullptr, nullptr,
                                     UMA_PACKAGE_START).error_code == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaPackageOperationAsync(handle.get(), "com.example.game", nullptr,
                                     UMA_PACKAGE_START_ACTIVITY).error_code == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaPackageOperationAsync(handle.get(), "com.example.game", nullptr, 99)
                .error_code == UMA_ERROR_INVALID_ARGUMENT);

    settings.set(L"UMA_FAKE_ADB_RESOLVE_ACTIVITY_MODE", L"component");
    clear_log();
    auto const resolved_start = run_package_operation(UMA_PACKAGE_START);
    REQUIRE(resolved_start.details.at("payload").at("command").at("error_code") == 0);
    REQUIRE(resolved_start.details.at("payload").at("command").at("exit_code") == 0);
    REQUIRE(read_log().find("cmd\tpackage\tresolve-activity\t--brief\tcom.example.game")
            != std::string::npos);
    REQUIRE(read_log().find("am\tstart\t-n\tcom.example.game/.MainActivity") != std::string::npos);
    REQUIRE(read_log().find("android.intent.category.LAUNCHER") == std::string::npos);

    settings.set(L"UMA_FAKE_ADB_RESOLVE_ACTIVITY_MODE", L"fail");
    clear_log();
    auto const fallback_start = run_package_operation(UMA_PACKAGE_START);
    REQUIRE(fallback_start.details.at("payload").at("command").at("error_code") == 0);
    REQUIRE(fallback_start.details.at("payload").at("command").at("error_message") == "");
    REQUIRE(read_log().find("am\tstart\t-a\tandroid.intent.action.MAIN\t-c\tandroid.intent.category.LAUNCHER\t-p\tcom.example.game")
            != std::string::npos);

    settings.set(L"UMA_FAKE_ADB_RESOLVE_ACTIVITY_MODE", L"mixed-case-no-activity");
    clear_log();
    auto const no_activity = run_package_operation(UMA_PACKAGE_START);
    REQUIRE(no_activity.details.at("payload").at("command").at("error_code") == 0);
    REQUIRE(read_log().find("android.intent.action.MAIN") != std::string::npos);

    clear_log();
    auto const explicit_activity = run_package_operation(
        UMA_PACKAGE_START_ACTIVITY, "com.example.game/com.example.Launcher");
    REQUIRE(explicit_activity.details.at("payload").at("command").at("exit_code") == 0);
    REQUIRE(read_log().find("am\tstart\t-n\tcom.example.game/com.example.Launcher")
            != std::string::npos);

    settings.set(L"UMA_FAKE_ADB_PACKAGE_PROCESS_MODE", L"pidof-fail-ps-running");
    clear_log();
    auto const ps_running = run_package_operation(UMA_PACKAGE_QUERY_RUNNING);
    REQUIRE(ps_running.details.at("payload").at("process_running") == true);
    REQUIRE(read_log().find("pidof\tcom.example.game") != std::string::npos);
    REQUIRE(read_log().find("ps\t-A") != std::string::npos);

    settings.set(L"UMA_FAKE_ADB_PACKAGE_PROCESS_MODE", L"pidof-fail-ps-lookalike");
    auto const lookalike = run_package_operation(UMA_PACKAGE_QUERY_RUNNING);
    REQUIRE(lookalike.details.at("payload").at("process_running") == false);

    auto const stopped = run_package_operation(UMA_PACKAGE_STOP);
    REQUIRE(stopped.details.at("payload").at("command").at("error_code") == 0);
    REQUIRE(stopped.details.at("payload").at("command").at("exit_code") == 0);
    UmaDestroy(handle.release());
}

TEST_CASE("device C ABI captures binary frames and applies guarded input", "[UmaCaller][device]")
{
    auto const suffix = std::to_wstring(GetCurrentProcessId());
    auto const log_path = test_artifact(L"uma-fake-adb-commands-" + suffix + L".txt");
    auto const truncated_path = test_artifact(L"uma-truncated-frame-" + suffix + L".png");
    auto const corrupt_path = test_artifact(L"uma-corrupt-frame-" + suffix + L".png");
    auto const landscape_path = test_artifact(L"uma-landscape-frame-" + suffix + L".png");
    auto const natural_landscape_path = test_artifact(L"uma-natural-landscape-frame-" + suffix + L".png");
    auto const wide_path = test_artifact(L"uma-wide-frame-" + suffix + L".png");
    std::error_code fs_error;
    fs::remove(log_path, fs_error);

    FakeAdbSettings settings;
    settings.set(L"UMA_FAKE_ADB_RESOLUTION", L"900x1600");
    settings.set(L"UMA_FAKE_ADB_ORIENTATION", L"0");
    settings.set(L"UMA_FAKE_ADB_COMMANDS_FILE", log_path.wstring());

    Collector collector;
    UmaHandleGuard handle_guard{make_handle(collector)};
    auto handle = handle_guard.get();
    auto const connection = start_connect(handle);
    REQUIRE(connection.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(connection.operation_id));
    REQUIRE(collector.terminal_for(connection.operation_id)->id == UMA_MSG_CONNECTION_SUCCEEDED);

    auto const verify = UmaVerifyGameAsync(handle, "com.example.game");
    REQUIRE(verify.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(verify.operation_id));
    auto const verified = collector.terminal_for(verify.operation_id);
    REQUIRE(verified.has_value());
    REQUIRE(verified->id == UMA_MSG_GAME_VERIFIED);
    REQUIRE(verified->details.at("version") == 2);
    REQUIRE(verified->details.at("type") == "GameVerified");
    REQUIRE(verified->details.at("payload").at("package_id") == "com.example.game");
    REQUIRE(verified->details.at("sequence").get<std::uint64_t>() > 0);

    auto const missing = UmaVerifyGameAsync(handle, "com.example.missing");
    REQUIRE(missing.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(missing.operation_id));
    auto const missing_event = collector.terminal_for(missing.operation_id);
    REQUIRE(missing_event->id == UMA_MSG_GAME_VERIFICATION_FAILED);
    REQUIRE(missing_event->details.at("payload").at("error_code") == UMA_ERROR_TARGET_GAME_NOT_INSTALLED);

    for (auto const& stderr_mode : {std::wstring{L"invalid-utf8"}, std::wstring{L"large"}})
    {
        settings.set(L"UMA_FAKE_ADB_STDERR_MODE", stderr_mode);
        auto const diagnostic = UmaVerifyGameAsync(handle, "com.example.game");
        REQUIRE(diagnostic.error_code == UMA_SUCCESS);
        REQUIRE(collector.wait_for_operation_terminal(diagnostic.operation_id));
        auto const diagnostic_event = collector.terminal_for(diagnostic.operation_id);
        REQUIRE(diagnostic_event->id == UMA_MSG_GAME_VERIFICATION_FAILED);
        REQUIRE(diagnostic_event->details.at("payload").at("error_code") == UMA_ERROR_COMMAND_FAILED);
        auto const message = diagnostic_event->details.at("payload").at("message").get<std::string>();
        REQUIRE_FALSE(message.empty());
        REQUIRE(message.size() <= 4096);
        if (stderr_mode == L"invalid-utf8")
            REQUIRE(message.find("\xef\xbf\xbd") != std::string::npos);
    }
    settings.set(L"UMA_FAKE_ADB_STDERR_MODE", L"");

    auto const capture = UmaCaptureAsync(handle);
    REQUIRE(capture.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(capture.operation_id));
    auto const captured = collector.terminal_for(capture.operation_id);
    REQUIRE(captured->id == UMA_MSG_FRAME_CAPTURED);
    REQUIRE(captured->details.at("version") == 2);
    REQUIRE(captured->details.at("payload").at("width") == 900);
    REQUIRE(captured->details.at("payload").at("height") == 1600);
    REQUIRE(captured->details.at("payload").at("orientation") == 0);
    REQUIRE(captured->details.at("payload").at("transform").at("canonical_width") == 1080);
    auto const first_frame = captured->details.at("payload").at("frame_id").get<std::uint64_t>();
    std::uint64_t byte_count{};
    REQUIRE(UmaGetFramePngSize(handle, first_frame, &byte_count) == UMA_SUCCESS);
    REQUIRE(byte_count > 64 * 1024);
    std::vector<std::uint8_t> actual_png(static_cast<std::size_t>(byte_count));
    REQUIRE(UmaCopyFramePng(handle, first_frame, actual_png.data(), actual_png.size()) == UMA_SUCCESS);
    auto const expected_png = read_binary_file(
        fs::path(source_root()) / "testdata/hachimi/ura/captures/career_skill_race_day_490_sample.png");
    REQUIRE(expected_png.size() == actual_png.size());
    REQUIRE(actual_png == expected_png);
    REQUIRE(std::find(actual_png.begin(), actual_png.end(), std::uint8_t{}) != actual_png.end());

    auto const second_capture = UmaCaptureAsync(handle);
    REQUIRE(second_capture.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(second_capture.operation_id));
    auto const second_event = collector.terminal_for(second_capture.operation_id);
    REQUIRE(second_event->id == UMA_MSG_FRAME_CAPTURED);
    REQUIRE(second_event->details.at("payload").at("geometry_generation")
            == captured->details.at("payload").at("geometry_generation"));
    std::uint64_t retained_size{};
    REQUIRE(UmaGetFramePngSize(handle, first_frame, &retained_size) == UMA_SUCCESS);
    REQUIRE(retained_size == byte_count);
    auto const second_frame = second_event->details.at("payload").at("frame_id").get<std::uint64_t>();

    REQUIRE(UmaKeyEventAsync(handle, 0, "KEYCODE_BACK").error_code == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaKeyEventAsync(handle, second_frame, nullptr).error_code == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaInputTextAsync(handle, 0, "text").error_code == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaInputTextAsync(handle, second_frame, nullptr).error_code == UMA_ERROR_INVALID_ARGUMENT);

    auto const key_event = UmaKeyEventAsync(handle, second_frame, "KEYCODE_BACK");
    REQUIRE(key_event.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(key_event.operation_id));
    auto const key_result = collector.terminal_for(key_event.operation_id);
    REQUIRE(key_result->id == UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(key_result->details.at("payload").at("source_frame").at("frame_id") == second_frame);

    auto const input_text = UmaInputTextAsync(handle, second_frame, "hello world&42 赛马娘");
    REQUIRE(input_text.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(input_text.operation_id));
    auto const text_result = collector.terminal_for(input_text.operation_id);
    REQUIRE(text_result->id == UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(text_result->details.at("payload").at("source_frame").at("frame_id") == second_frame);

    auto const tap = UmaTapAsync(handle, second_frame, 540, 960);
    REQUIRE(tap.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(tap.operation_id));
    auto const tapped = collector.terminal_for(tap.operation_id);
    REQUIRE(tapped->id == UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(tapped->details.at("payload").at("requested").at("first_point").at("canonical_x") == 540);
    REQUIRE(tapped->details.at("payload").at("actual").at("first_point").at("device_x") == 450);
    REQUIRE(tapped->details.at("payload").at("actual").at("first_point").at("device_y") == 800);

    settings.set(L"UMA_FAKE_ADB_INPUT_FAIL", L"1");
    auto const failed_text = UmaInputTextAsync(handle, second_frame, "failure");
    REQUIRE(failed_text.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(failed_text.operation_id));
    auto const failed_text_event = collector.terminal_for(failed_text.operation_id);
    REQUIRE(failed_text_event->id == UMA_MSG_INPUT_FAILED);
    REQUIRE(failed_text_event->details.at("payload").at("error_code") == UMA_ERROR_COMMAND_FAILED);
    settings.set(L"UMA_FAKE_ADB_INPUT_FAIL", L"");

    auto const swipe = UmaSwipeAsync(handle, second_frame, 0, 0, 1079, 1919, 350);
    REQUIRE(swipe.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(swipe.operation_id));
    auto const swiped = collector.terminal_for(swipe.operation_id);
    REQUIRE(swiped->id == UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(swiped->details.at("payload").at("requested").at("second_point").at("canonical_y") == 1919);
    REQUIRE(swiped->details.at("payload").at("actual").at("second_point").at("device_x") == 899);
    REQUIRE(swiped->details.at("payload").at("actual").at("second_point").at("device_y") == 1599);

    auto const legacy_extra_swipe = UmaSwipeWithOptionsAsync(
        handle, second_frame, 0, 1000, 1079, 1100, 350, 1);
    REQUIRE(legacy_extra_swipe.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(legacy_extra_swipe.operation_id));
    REQUIRE(collector.terminal_for(legacy_extra_swipe.operation_id)->id == UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(UmaSwipeWithOptionsAsync(handle, second_frame, 0, 0, 100, 100, 350, 2).error_code
            == UMA_ERROR_INVALID_ARGUMENT);

    REQUIRE(UmaSetInstanceOptions(handle,
        R"({"version":1,"swipe_duration_multiplier":2.5,"extra_swipe_distance":40,"extra_swipe_duration_ms":1234})")
        == UMA_SUCCESS);
    auto const configured_extra_swipe = UmaSwipeWithOptionsAsync(
        handle, second_frame, 0, 1000, 1079, 1100, 351, 1);
    REQUIRE(configured_extra_swipe.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(configured_extra_swipe.operation_id));
    REQUIRE(collector.terminal_for(configured_extra_swipe.operation_id)->id == UMA_MSG_INPUT_SUCCEEDED);

    prepare_held_touch(collector, handle, second_frame);
    auto const touch_down = UmaTouchAsync(
        handle, second_frame, 540, 960, UMA_TOUCH_DOWN);
    REQUIRE(touch_down.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(touch_down.operation_id));
    auto const touch_down_event = collector.terminal_for(touch_down.operation_id);
    REQUIRE(touch_down_event->id == UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(touch_down_event->details.at("payload").at("source_frame").at("frame_id")
            == second_frame);

    auto const key_while_pressed = UmaKeyEventAsync(handle, second_frame, "KEYCODE_BACK");
    REQUIRE(key_while_pressed.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(key_while_pressed.operation_id));
    REQUIRE(collector.terminal_for(key_while_pressed.operation_id)->id == UMA_MSG_INPUT_FAILED);
    REQUIRE(collector.terminal_for(key_while_pressed.operation_id)->details.at("payload").at("error_code")
            == UMA_ERROR_BUSY);

    auto const text_while_pressed = UmaInputTextAsync(handle, second_frame, "text");
    REQUIRE(text_while_pressed.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(text_while_pressed.operation_id));
    REQUIRE(collector.terminal_for(text_while_pressed.operation_id)->id == UMA_MSG_INPUT_FAILED);
    REQUIRE(collector.terminal_for(text_while_pressed.operation_id)->details.at("payload").at("error_code")
            == UMA_ERROR_BUSY);

    auto const stale_release = UmaTouchAsync(handle, first_frame, 0, 0, UMA_TOUCH_UP);
    REQUIRE(stale_release.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(stale_release.operation_id));
    auto const stale_release_event = collector.terminal_for(stale_release.operation_id);
    REQUIRE(stale_release_event->id == UMA_MSG_INPUT_FAILED);
    REQUIRE(stale_release_event->details.at("payload").at("error_code")
            == UMA_ERROR_INVALID_ARGUMENT);

    auto const capture_while_pressed = UmaCaptureAsync(handle);
    REQUIRE(capture_while_pressed.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(capture_while_pressed.operation_id));
    auto const held_capture = collector.terminal_for(capture_while_pressed.operation_id);
    REQUIRE(held_capture->id == UMA_MSG_FRAME_CAPTURED);
    auto const held_capture_frame = held_capture->details.at("payload").at("frame_id").get<std::uint64_t>();

    auto const tap_while_pressed = UmaTapAsync(handle, second_frame, 540, 960);
    REQUIRE(tap_while_pressed.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(tap_while_pressed.operation_id));
    auto const held_tap = collector.terminal_for(tap_while_pressed.operation_id);
    REQUIRE(held_tap->id == UMA_MSG_INPUT_FAILED);
    REQUIRE(held_tap->details.at("payload").at("error_code") == UMA_ERROR_BUSY);

    auto const touch_up = UmaTouchAsync(handle, second_frame, 0, 0, UMA_TOUCH_UP);
    REQUIRE(touch_up.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(touch_up.operation_id));
    auto const touch_up_event = collector.terminal_for(touch_up.operation_id);
    REQUIRE(touch_up_event->id == UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(touch_up_event->details.at("payload").at("source_frame").at("frame_id")
            == second_frame);

    auto const duplicate_press_frame = UmaTouchAsync(
        handle, second_frame, 540, 960, UMA_TOUCH_DOWN);
    REQUIRE(duplicate_press_frame.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(duplicate_press_frame.operation_id));
    auto const duplicate_press_event = collector.terminal_for(duplicate_press_frame.operation_id);
    REQUIRE(duplicate_press_event->id == UMA_MSG_INPUT_FAILED);
    REQUIRE(duplicate_press_event->details.at("payload").at("error_code")
            == UMA_ERROR_INVALID_ARGUMENT);

    prepare_held_touch(collector, handle, held_capture_frame);
    auto const touch_down_again = UmaTouchAsync(
        handle, held_capture_frame, 540, 960, UMA_TOUCH_DOWN);
    REQUIRE(touch_down_again.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(touch_down_again.operation_id));
    REQUIRE(collector.terminal_for(touch_down_again.operation_id)->id == UMA_MSG_INPUT_SUCCEEDED);
    auto const stale_cancel = UmaTouchAsync(handle, second_frame, 0, 0, UMA_TOUCH_CANCEL);
    REQUIRE(stale_cancel.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(stale_cancel.operation_id));
    auto const stale_cancel_event = collector.terminal_for(stale_cancel.operation_id);
    REQUIRE(stale_cancel_event->id == UMA_MSG_INPUT_FAILED);
    REQUIRE(stale_cancel_event->details.at("payload").at("error_code")
            == UMA_ERROR_INVALID_ARGUMENT);
    auto const touch_cancel = UmaTouchAsync(handle, held_capture_frame, 0, 0, UMA_TOUCH_CANCEL);
    REQUIRE(touch_cancel.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(touch_cancel.operation_id));
    REQUIRE(collector.terminal_for(touch_cancel.operation_id)->id == UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(UmaTouchAsync(handle, held_capture_frame, 540, 960, 99).error_code
            == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaReleaseFrame(handle, held_capture_frame) == UMA_SUCCESS);

    settings.set(L"UMA_FAKE_ADB_ORIENTATION", L"2");
    auto const stale_tap = UmaTapAsync(handle, second_frame, 540, 960);
    REQUIRE(stale_tap.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(stale_tap.operation_id));
    auto const stale = collector.terminal_for(stale_tap.operation_id);
    REQUIRE(stale->id == UMA_MSG_INPUT_FAILED);
    REQUIRE(stale->details.at("payload").at("requested").at("first_point").at("canonical_x") == 540);
    REQUIRE_FALSE(stale->details.at("payload").contains("actual"));

    auto const rotated_capture = UmaCaptureAsync(handle);
    REQUIRE(rotated_capture.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(rotated_capture.operation_id));
    auto const rotated = collector.terminal_for(rotated_capture.operation_id);
    REQUIRE(rotated->id == UMA_MSG_FRAME_CAPTURED);
    REQUIRE(rotated->details.at("payload").at("orientation") == 2);
    REQUIRE(rotated->details.at("payload").at("geometry_generation")
            > second_event->details.at("payload").at("geometry_generation"));
    auto const rotated_frame = rotated->details.at("payload").at("frame_id").get<std::uint64_t>();
    auto const rotated_tap = UmaTapAsync(handle, rotated_frame, 540, 960);
    REQUIRE(rotated_tap.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(rotated_tap.operation_id));
    auto const rotated_input = collector.terminal_for(rotated_tap.operation_id);
    REQUIRE(rotated_input->id == UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(rotated_input->details.at("payload").at("source_frame").at("orientation") == 2);
    REQUIRE(rotated_input->details.at("payload").at("actual").at("first_point").at("device_x") == 449);
    REQUIRE(rotated_input->details.at("payload").at("actual").at("first_point").at("device_y") == 799);
    REQUIRE(UmaReleaseFrame(handle, first_frame) == UMA_SUCCESS);
    REQUIRE(UmaReleaseFrame(handle, second_frame) == UMA_SUCCESS);
    REQUIRE(UmaReleaseFrame(handle, rotated_frame) == UMA_SUCCESS);

    REQUIRE(write_solid_png(landscape_path, 1600, 900));
    settings.set(L"UMA_FAKE_ADB_SCREENSHOT_PATH", landscape_path.wstring());
    settings.set(L"UMA_FAKE_ADB_ORIENTATION", L"1");
    auto const orientation_90_capture = UmaCaptureAsync(handle);
    REQUIRE(orientation_90_capture.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(orientation_90_capture.operation_id));
    auto const orientation_90 = collector.terminal_for(orientation_90_capture.operation_id);
    REQUIRE(orientation_90->id == UMA_MSG_FRAME_CAPTURED);
    REQUIRE(orientation_90->details.at("payload").at("width") == 1600);
    REQUIRE(orientation_90->details.at("payload").at("height") == 900);
    REQUIRE(orientation_90->details.at("payload").at("orientation") == 1);
    auto const frame_90 = orientation_90->details.at("payload").at("frame_id").get<std::uint64_t>();
    auto const tap_90 = UmaTapAsync(handle, frame_90, 540, 960);
    REQUIRE(tap_90.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(tap_90.operation_id));
    auto const input_90 = collector.terminal_for(tap_90.operation_id);
    REQUIRE(input_90->id == UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(input_90->details.at("payload").at("actual").at("first_point").at("device_x") == 799);
    REQUIRE(input_90->details.at("payload").at("actual").at("first_point").at("device_y") == 450);
    REQUIRE(UmaReleaseFrame(handle, frame_90) == UMA_SUCCESS);

    settings.set(L"UMA_FAKE_ADB_ORIENTATION", L"3");
    auto const orientation_270_capture = UmaCaptureAsync(handle);
    REQUIRE(orientation_270_capture.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(orientation_270_capture.operation_id));
    auto const orientation_270 = collector.terminal_for(orientation_270_capture.operation_id);
    REQUIRE(orientation_270->id == UMA_MSG_FRAME_CAPTURED);
    REQUIRE(orientation_270->details.at("payload").at("orientation") == 3);
    auto const frame_270 = orientation_270->details.at("payload").at("frame_id").get<std::uint64_t>();
    auto const tap_270 = UmaTapAsync(handle, frame_270, 540, 960);
    REQUIRE(tap_270.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(tap_270.operation_id));
    auto const input_270 = collector.terminal_for(tap_270.operation_id);
    REQUIRE(input_270->id == UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(input_270->details.at("payload").at("actual").at("first_point").at("device_x") == 800);
    REQUIRE(input_270->details.at("payload").at("actual").at("first_point").at("device_y") == 449);

    REQUIRE(write_solid_png(natural_landscape_path, 1080, 1920));
    settings.set(L"UMA_FAKE_ADB_RESOLUTION", L"1920x1080");
    settings.set(L"UMA_FAKE_ADB_SCREENSHOT_PATH", natural_landscape_path.wstring());
    settings.set(L"UMA_FAKE_ADB_ORIENTATION", L"1");
    auto const natural_landscape_capture = UmaCaptureAsync(handle);
    REQUIRE(natural_landscape_capture.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(natural_landscape_capture.operation_id));
    auto const natural_landscape = collector.terminal_for(natural_landscape_capture.operation_id);
    REQUIRE(natural_landscape->id == UMA_MSG_FRAME_CAPTURED);
    REQUIRE(natural_landscape->details.at("payload").at("width") == 1080);
    REQUIRE(natural_landscape->details.at("payload").at("height") == 1920);
    REQUIRE(natural_landscape->details.at("payload").at("orientation") == 1);
    auto const natural_landscape_frame = natural_landscape->details.at("payload").at("frame_id").get<std::uint64_t>();
    REQUIRE(UmaReleaseFrame(handle, natural_landscape_frame) == UMA_SUCCESS);

    REQUIRE(write_solid_png(wide_path, 2000, 900));
    settings.set(L"UMA_FAKE_ADB_RESOLUTION", L"900x2000");
    settings.set(L"UMA_FAKE_ADB_SCREENSHOT_PATH", wide_path.wstring());
    settings.set(L"UMA_FAKE_ADB_ORIENTATION", L"1");
    auto const letterbox_capture = UmaCaptureAsync(handle);
    REQUIRE(letterbox_capture.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(letterbox_capture.operation_id));
    auto const letterboxed = collector.terminal_for(letterbox_capture.operation_id);
    REQUIRE(letterboxed->id == UMA_MSG_FRAME_CAPTURED);
    REQUIRE(letterboxed->details.at("payload").at("transform").at("offset_x").get<double>() == 200.0);
    auto const letterbox_frame = letterboxed->details.at("payload").at("frame_id").get<std::uint64_t>();
    auto const letterbox_tap = UmaTapAsync(handle, letterbox_frame, 540, 960);
    REQUIRE(letterbox_tap.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(letterbox_tap.operation_id));
    auto const letterbox_input = collector.terminal_for(letterbox_tap.operation_id);
    REQUIRE(letterbox_input->id == UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(letterbox_input->details.at("payload").at("actual").at("first_point").at("device_x") == 999);
    REQUIRE(letterbox_input->details.at("payload").at("actual").at("first_point").at("device_y") == 450);

    for (auto const invalid_orientation : {L"4 (secondary 1)", L"12", L"90"})
    {
        settings.set(L"UMA_FAKE_ADB_ORIENTATION", invalid_orientation);
        auto const invalid_orientation_capture = UmaCaptureAsync(handle);
        REQUIRE(invalid_orientation_capture.error_code == UMA_SUCCESS);
        REQUIRE(collector.wait_for_operation_terminal(invalid_orientation_capture.operation_id));
        auto const invalid_orientation_event = collector.terminal_for(invalid_orientation_capture.operation_id);
        REQUIRE(invalid_orientation_event->id == UMA_MSG_OPERATION_FAILED);
        REQUIRE(invalid_orientation_event->details.at("payload").at("error_code")
                == UMA_ERROR_INVALID_DEVICE_RESPONSE);
    }

    auto const log_bytes = read_binary_file(log_path);
    std::string const command_log(log_bytes.begin(), log_bytes.end());
    REQUIRE(command_log.find("input\ttap\t450\t800") != std::string::npos);
    REQUIRE(command_log.find("input\tkeyevent\t'KEYCODE_BACK'") != std::string::npos);
    REQUIRE(command_log.find("input\ttext\thello%sworld\\&42%s赛马娘") != std::string::npos);
    REQUIRE(command_log.find("input\tswipe\t0\t0\t899\t1599\t3500") != std::string::npos);
    REQUIRE(command_log.find("input\tswipe\t0\t833\t899\t917\t3500") != std::string::npos);
    REQUIRE(command_log.find("input\tswipe\t899\t917\t899\t817\t500") != std::string::npos);
    REQUIRE(command_log.find("input\tswipe\t0\t833\t899\t917\t877") != std::string::npos);
    REQUIRE(command_log.find("input\tswipe\t899\t917\t899\t877\t1234") != std::string::npos);
    REQUIRE(command_log.find("sendevent /dev/input/event5 3 53 2048") != std::string::npos);
    REQUIRE(command_log.find("sendevent /dev/input/event5 3 54 2048") != std::string::npos);
    REQUIRE(command_log.find("sendevent /dev/input/event5 1 325 1") != std::string::npos);
    REQUIRE(command_log.find("sendevent /dev/input/event5 1 330 1") != std::string::npos);
    REQUIRE(command_log.find("sendevent /dev/input/event5 3 57 -1") != std::string::npos);
    REQUIRE(UmaGetFramePngSize(handle, first_frame, &retained_size) == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaReleaseFrame(handle, frame_270) == UMA_SUCCESS);
    REQUIRE(UmaReleaseFrame(handle, letterbox_frame) == UMA_SUCCESS);

    auto truncated = expected_png;
    truncated.resize(64);
    {
        std::ofstream output(truncated_path, std::ios::binary | std::ios::trunc);
        output.write(reinterpret_cast<char const*>(truncated.data()), static_cast<std::streamsize>(truncated.size()));
    }
    auto corrupt = expected_png;
    corrupt[100] = static_cast<std::uint8_t>(corrupt[100] ^ 0xff);
    {
        std::ofstream output(corrupt_path, std::ios::binary | std::ios::trunc);
        output.write(reinterpret_cast<char const*>(corrupt.data()), static_cast<std::streamsize>(corrupt.size()));
    }
    for (auto const& invalid_path : {truncated_path, corrupt_path})
    {
        settings.set(L"UMA_FAKE_ADB_SCREENSHOT_PATH", invalid_path.wstring());
        auto const invalid_capture = UmaCaptureAsync(handle);
        REQUIRE(invalid_capture.error_code == UMA_SUCCESS);
        REQUIRE(collector.wait_for_operation_terminal(invalid_capture.operation_id));
        auto const invalid = collector.terminal_for(invalid_capture.operation_id);
        REQUIRE(invalid->id == UMA_MSG_OPERATION_FAILED);
        REQUIRE(invalid->details.at("payload").at("error_code") == UMA_ERROR_INVALID_DEVICE_RESPONSE);
    }

    settings.set(L"UMA_FAKE_ADB_SCREENSHOT_PATH",
        (fs::path(source_root()) / "testdata/hachimi/ura/captures/career_skill_race_day_490_sample.png").wstring());
    settings.set(L"UMA_FAKE_ADB_RESOLUTION", L"900x1600");
    settings.set(L"UMA_FAKE_ADB_ORIENTATION", L"0");
    Collector destroy_collector;
    UmaHandleGuard destroy_guard{make_handle(destroy_collector)};
    auto const destroy_handle = destroy_guard.get();
    auto const destroy_connect = start_connect(destroy_handle);
    REQUIRE(destroy_connect.error_code == UMA_SUCCESS);
    REQUIRE(destroy_collector.wait_for_operation_terminal(destroy_connect.operation_id));
    REQUIRE(destroy_collector.terminal_for(destroy_connect.operation_id)->id
            == UMA_MSG_CONNECTION_SUCCEEDED);
    auto const destroy_capture = UmaCaptureAsync(destroy_handle);
    REQUIRE(destroy_capture.error_code == UMA_SUCCESS);
    REQUIRE(destroy_collector.wait_for_operation_terminal(destroy_capture.operation_id));
    auto const destroy_capture_event = destroy_collector.terminal_for(destroy_capture.operation_id);
    REQUIRE(destroy_capture_event->id == UMA_MSG_FRAME_CAPTURED);
    auto const destroy_frame = destroy_capture_event->details.at("payload").at("frame_id").get<std::uint64_t>();
    prepare_held_touch(destroy_collector, destroy_handle, destroy_frame);
    auto const destroy_down = UmaTouchAsync(
        destroy_handle, destroy_frame, 540, 960, UMA_TOUCH_DOWN);
    REQUIRE(destroy_down.error_code == UMA_SUCCESS);
    REQUIRE(destroy_collector.wait_for_operation_terminal(destroy_down.operation_id));
    REQUIRE(destroy_collector.terminal_for(destroy_down.operation_id)->id
            == UMA_MSG_INPUT_SUCCEEDED);
    UmaDestroy(destroy_guard.release());
    auto const after_destroy_bytes = read_binary_file(log_path);
    std::string const after_destroy_log(after_destroy_bytes.begin(), after_destroy_bytes.end());
    std::size_t release_occurrences = 0;
    std::size_t release_position = 0;
    constexpr std::string_view release_token{"sendevent /dev/input/event5 3 57 -1"};
    while ((release_position = after_destroy_log.find(release_token, release_position))
        != std::string::npos)
    {
        ++release_occurrences;
        release_position += release_token.size();
    }
    REQUIRE(release_occurrences >= 3);
    fs::remove(log_path, fs_error);
    fs::remove(truncated_path, fs_error);
    fs::remove(corrupt_path, fs_error);
    fs::remove(landscape_path, fs_error);
    fs::remove(natural_landscape_path, fs_error);
    fs::remove(wide_path, fs_error);
}

TEST_CASE("held touch cancellation sends an uncanceled release after partial down",
    "[UmaCaller][device][touch]")
{
    auto const suffix = std::to_wstring(GetCurrentProcessId());
    auto const log_path = test_artifact(L"uma-fake-adb-touch-cancel-" + suffix + L".tsv");
    auto const started_path = test_artifact(L"uma-fake-adb-touch-down-started-" + suffix + L".txt");
    std::error_code ignored;
    fs::remove(log_path, ignored);
    fs::remove(started_path, ignored);

    FakeAdbSettings settings;
    settings.set(L"UMA_FAKE_ADB_RESOLUTION", L"900x1600");
    settings.set(L"UMA_FAKE_ADB_ORIENTATION", L"0");
    settings.set(L"UMA_FAKE_ADB_COMMANDS_FILE", log_path.wstring());
    settings.set(L"UMA_FAKE_ADB_SCREENSHOT_PATH",
        (fs::path(source_root()) / "testdata/hachimi/ura/captures/career_skill_race_day_490_sample.png").wstring());

    Collector collector;
    UmaHandleGuard handle_guard{make_handle(collector)};
    auto const handle = handle_guard.get();
    auto const connection = start_connect(handle);
    REQUIRE(connection.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(connection.operation_id));
    REQUIRE(collector.terminal_for(connection.operation_id)->id == UMA_MSG_CONNECTION_SUCCEEDED);

    auto const capture = UmaCaptureAsync(handle);
    REQUIRE(capture.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(capture.operation_id));
    auto const captured = collector.terminal_for(capture.operation_id);
    REQUIRE(captured->id == UMA_MSG_FRAME_CAPTURED);
    auto const frame_id = captured->details.at("payload").at("frame_id").get<std::uint64_t>();

    settings.set(L"UMA_FAKE_ADB_TOUCH_DOWN_STARTED_FILE", started_path.wstring());
    settings.set(L"UMA_FAKE_ADB_TOUCH_DOWN_DELAY_MS", L"10000");
    prepare_held_touch(collector, handle, frame_id);
    auto const down = UmaTouchAsync(handle, frame_id, 540, 960, UMA_TOUCH_DOWN);
    REQUIRE(down.error_code == UMA_SUCCESS);
    auto const deadline = std::chrono::steady_clock::now() + 5s;
    while (!fs::exists(started_path) && std::chrono::steady_clock::now() < deadline)
        std::this_thread::sleep_for(10ms);
    bool const partial_down_started = fs::exists(started_path);
    if (!partial_down_started) (void)UmaCancelOperation(handle, down.operation_id);
    REQUIRE(partial_down_started);

    REQUIRE(UmaCancelOperation(handle, down.operation_id) == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(down.operation_id));
    auto const canceled = collector.terminal_for(down.operation_id);
    REQUIRE(canceled->id == UMA_MSG_INPUT_FAILED);
    REQUIRE(canceled->details.at("payload").at("error_code") == UMA_ERROR_CANCELED);

    auto const log_bytes = read_binary_file(log_path);
    std::string const command_log(log_bytes.begin(), log_bytes.end());
    REQUIRE(command_log.find("sendevent /dev/input/event5 3 57 123") != std::string::npos);
    REQUIRE(command_log.find("sendevent /dev/input/event5 3 57 -1") != std::string::npos);
    REQUIRE(UmaReleaseFrame(handle, frame_id) == UMA_SUCCESS);

    fs::remove(log_path, ignored);
    fs::remove(started_path, ignored);
}

TEST_CASE("held touch requires current preflight and failed or canceled reprepare clears readiness",
    "[UmaCaller][device][touch][prepare]")
{
    auto const suffix = std::to_wstring(GetCurrentProcessId());
    auto const log_path = test_artifact(L"uma-fake-touch-prepare-" + suffix + L".tsv");
    auto const started_path = test_artifact(L"uma-fake-touch-prepare-started-" + suffix + L".txt");
    std::error_code ignored;
    fs::remove(log_path, ignored);
    fs::remove(started_path, ignored);

    FakeAdbSettings settings;
    settings.set(L"UMA_FAKE_ADB_RESOLUTION", L"900x1600");
    settings.set(L"UMA_FAKE_ADB_ORIENTATION", L"0");
    settings.set(L"UMA_FAKE_ADB_COMMANDS_FILE", log_path.wstring());

    Collector collector;
    UmaHandleGuard handle_guard{make_handle(collector)};
    auto const handle = handle_guard.get();
    auto const connection = start_connect(handle);
    REQUIRE(connection.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(connection.operation_id));
    REQUIRE(collector.terminal_for(connection.operation_id)->id == UMA_MSG_CONNECTION_SUCCEEDED);

    auto capture_frame = [&]() {
        auto const capture = UmaCaptureAsync(handle);
        REQUIRE(capture.error_code == UMA_SUCCESS);
        REQUIRE(collector.wait_for_operation_terminal(capture.operation_id));
        auto const event = collector.terminal_for(capture.operation_id);
        REQUIRE(event->id == UMA_MSG_FRAME_CAPTURED);
        return event->details.at("payload").at("frame_id").get<std::uint64_t>();
    };
    auto release_frame = [&](std::uint64_t frame_id) {
        REQUIRE(UmaReleaseFrame(handle, frame_id) == UMA_SUCCESS);
    };
    auto command_text = [&]() {
        auto const bytes = read_binary_file(log_path);
        return std::string(bytes.begin(), bytes.end());
    };
    auto occurrences = [](std::string_view text, std::string_view needle) {
        std::size_t result = 0;
        for (std::size_t position = 0;
             (position = text.find(needle, position)) != std::string_view::npos;
             position += needle.size())
            ++result;
        return result;
    };
    auto wait_input = [&](UmaStartResult operation, std::int32_t expected_message) {
        REQUIRE(operation.error_code == UMA_SUCCESS);
        REQUIRE(collector.wait_for_operation_terminal(operation.operation_id));
        auto const event = collector.terminal_for(operation.operation_id);
        REQUIRE(event->id == expected_message);
        return event;
    };

    auto const preparation_frame = capture_frame();
    auto const before_unprepared_down = command_text();
    auto const unprepared_down = wait_input(
        UmaTouchAsync(handle, preparation_frame, 540, 960, UMA_TOUCH_DOWN),
        UMA_MSG_INPUT_FAILED);
    REQUIRE(unprepared_down->id == UMA_MSG_INPUT_FAILED);
    REQUIRE(unprepared_down->details.at("payload").at("error_code") == UMA_ERROR_DEVICE_NOT_READY);
    auto const after_unprepared_down = command_text();
    REQUIRE(occurrences(after_unprepared_down, "getevent\t-lp")
            == occurrences(before_unprepared_down, "getevent\t-lp"));
    REQUIRE(occurrences(after_unprepared_down, "\tpush\t")
            == occurrences(before_unprepared_down, "\tpush\t"));

    prepare_held_touch(collector, handle, preparation_frame);
    release_frame(preparation_frame);
    auto const first_ready_frame = capture_frame();
    auto const before_prepared_down = command_text();
    auto const down = wait_input(
        UmaTouchAsync(handle, first_ready_frame, 540, 960, UMA_TOUCH_DOWN),
        UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(down->id == UMA_MSG_INPUT_SUCCEEDED);
    auto const after_prepared_down = command_text();
    REQUIRE(occurrences(after_prepared_down, "getevent\t-lp")
            == occurrences(before_prepared_down, "getevent\t-lp"));
    REQUIRE(occurrences(after_prepared_down, "\tpush\t")
            == occurrences(before_prepared_down, "\tpush\t"));
    auto const up = wait_input(
        UmaTouchAsync(handle, first_ready_frame, 0, 0, UMA_TOUCH_UP),
        UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(up->id == UMA_MSG_INPUT_SUCCEEDED);
    release_frame(first_ready_frame);

    auto const option_preparation_frame = capture_frame();
    prepare_held_touch(collector, handle, option_preparation_frame);
    release_frame(option_preparation_frame);
    REQUIRE(UmaSetInstanceOptions(handle,
        R"({"version":1,"touch_event_id":"5"})") == UMA_SUCCESS);
    auto const option_changed_frame = capture_frame();
    auto const options_changed = wait_input(
        UmaTouchAsync(handle, option_changed_frame, 540, 960, UMA_TOUCH_DOWN),
        UMA_MSG_INPUT_FAILED);
    REQUIRE(options_changed->id == UMA_MSG_INPUT_FAILED);
    REQUIRE(options_changed->details.at("payload").at("error_code") == UMA_ERROR_DEVICE_NOT_READY);
    prepare_held_touch(collector, handle, option_changed_frame);
    auto const down_after_options = wait_input(
        UmaTouchAsync(handle, option_changed_frame, 540, 960, UMA_TOUCH_DOWN),
        UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(down_after_options->id == UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(wait_input(
        UmaTouchAsync(handle, option_changed_frame, 0, 0, UMA_TOUCH_CANCEL),
        UMA_MSG_INPUT_SUCCEEDED)->id == UMA_MSG_INPUT_SUCCEEDED);
    release_frame(option_changed_frame);

    auto const geometry_preparation_frame = capture_frame();
    prepare_held_touch(collector, handle, geometry_preparation_frame);
    release_frame(geometry_preparation_frame);
    settings.set(L"UMA_FAKE_ADB_ORIENTATION", L"2");
    auto const changed_geometry_frame = capture_frame();
    auto const geometry_changed = wait_input(
        UmaTouchAsync(handle, changed_geometry_frame, 540, 960, UMA_TOUCH_DOWN),
        UMA_MSG_INPUT_FAILED);
    REQUIRE(geometry_changed->id == UMA_MSG_INPUT_FAILED);
    REQUIRE(geometry_changed->details.at("payload").at("error_code") == UMA_ERROR_DEVICE_NOT_READY);
    release_frame(changed_geometry_frame);
    settings.set(L"UMA_FAKE_ADB_ORIENTATION", L"0");
    auto const recovered_geometry_frame = capture_frame();
    prepare_held_touch(collector, handle, recovered_geometry_frame);
    auto const down_after_geometry = wait_input(
        UmaTouchAsync(handle, recovered_geometry_frame, 540, 960, UMA_TOUCH_DOWN),
        UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(down_after_geometry->id == UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(wait_input(
        UmaTouchAsync(handle, recovered_geometry_frame, 0, 0, UMA_TOUCH_UP),
        UMA_MSG_INPUT_SUCCEEDED)->id == UMA_MSG_INPUT_SUCCEEDED);
    release_frame(recovered_geometry_frame);

    auto const failing_prepare_frame = capture_frame();
    prepare_held_touch(collector, handle, failing_prepare_frame);
    settings.set(L"UMA_FAKE_ADB_GETEVENT_FAIL", L"1");
    auto const failed_prepare = UmaPrepareTouchAsync(handle, failing_prepare_frame);
    REQUIRE(failed_prepare.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(failed_prepare.operation_id));
    auto const failed_prepare_event = collector.terminal_for(failed_prepare.operation_id);
    REQUIRE(failed_prepare_event->id == UMA_MSG_INPUT_FAILED);
    settings.set(L"UMA_FAKE_ADB_GETEVENT_FAIL", L"");
    release_frame(failing_prepare_frame);
    auto const after_failed_prepare_frame = capture_frame();
    auto const after_failed_prepare = wait_input(
        UmaTouchAsync(handle, after_failed_prepare_frame, 540, 960, UMA_TOUCH_DOWN),
        UMA_MSG_INPUT_FAILED);
    REQUIRE(after_failed_prepare->id == UMA_MSG_INPUT_FAILED);
    REQUIRE(after_failed_prepare->details.at("payload").at("error_code") == UMA_ERROR_DEVICE_NOT_READY);
    prepare_held_touch(collector, handle, after_failed_prepare_frame);
    auto const down_after_failure = wait_input(
        UmaTouchAsync(handle, after_failed_prepare_frame, 540, 960, UMA_TOUCH_DOWN),
        UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(down_after_failure->id == UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(wait_input(
        UmaTouchAsync(handle, after_failed_prepare_frame, 0, 0, UMA_TOUCH_UP),
        UMA_MSG_INPUT_SUCCEEDED)->id == UMA_MSG_INPUT_SUCCEEDED);

    prepare_held_touch(collector, handle, after_failed_prepare_frame);
    settings.set(L"UMA_FAKE_ADB_GETEVENT_STARTED_FILE", started_path.wstring());
    settings.set(L"UMA_FAKE_ADB_GETEVENT_DELAY_MS", L"10000");
    auto const canceled_prepare = UmaPrepareTouchAsync(handle, after_failed_prepare_frame);
    REQUIRE(canceled_prepare.error_code == UMA_SUCCESS);
    auto const deadline = std::chrono::steady_clock::now() + 5s;
    while (!fs::exists(started_path) && std::chrono::steady_clock::now() < deadline)
        std::this_thread::sleep_for(10ms);
    bool const discovery_started = fs::exists(started_path);
    auto const cancel_result = UmaCancelOperation(handle, canceled_prepare.operation_id);
    REQUIRE(cancel_result == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(canceled_prepare.operation_id));
    auto const canceled_prepare_event = collector.terminal_for(canceled_prepare.operation_id);
    REQUIRE(canceled_prepare_event->id == UMA_MSG_INPUT_FAILED);
    REQUIRE(canceled_prepare_event->details.at("payload").at("error_code") == UMA_ERROR_CANCELED);
    settings.set(L"UMA_FAKE_ADB_GETEVENT_DELAY_MS", L"");
    settings.set(L"UMA_FAKE_ADB_GETEVENT_STARTED_FILE", L"");
    REQUIRE(discovery_started);
    release_frame(after_failed_prepare_frame);

    auto const after_canceled_prepare_frame = capture_frame();
    auto const before_unready_down = command_text();
    auto const after_canceled_prepare = wait_input(
        UmaTouchAsync(handle, after_canceled_prepare_frame, 540, 960, UMA_TOUCH_DOWN),
        UMA_MSG_INPUT_FAILED);
    REQUIRE(after_canceled_prepare->id == UMA_MSG_INPUT_FAILED);
    REQUIRE(after_canceled_prepare->details.at("payload").at("error_code") == UMA_ERROR_DEVICE_NOT_READY);
    auto const after_unready_down = command_text();
    REQUIRE(occurrences(after_unready_down, "getevent\t-lp")
            == occurrences(before_unready_down, "getevent\t-lp"));

    prepare_held_touch(collector, handle, after_canceled_prepare_frame);
    auto const final_down = wait_input(
        UmaTouchAsync(handle, after_canceled_prepare_frame, 540, 960, UMA_TOUCH_DOWN),
        UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(final_down->id == UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(wait_input(
        UmaTouchAsync(handle, after_canceled_prepare_frame, 0, 0, UMA_TOUCH_CANCEL),
        UMA_MSG_INPUT_SUCCEEDED)->id == UMA_MSG_INPUT_SUCCEEDED);
    release_frame(after_canceled_prepare_frame);

    fs::remove(log_path, ignored);
    fs::remove(started_path, ignored);
}

TEST_CASE("interactive minitouch is started from stderr handshake and used without ADB input fallback",
    "[UmaCaller][device][touch][interactive]")
{
    auto const suffix = std::to_wstring(GetCurrentProcessId());
    auto const log_path = test_artifact(L"uma-fake-adb-minitouch-" + suffix + L".tsv");
    auto const input_path = test_artifact(L"uma-fake-minitouch-input-" + suffix + L".txt");
    auto const binary_path = test_artifact(L"fake-minitouch-" + suffix + L".bin");
    std::error_code ignored;
    fs::remove(log_path, ignored);
    fs::remove(input_path, ignored);
    {
        std::ofstream binary(binary_path, std::ios::binary | std::ios::trunc);
        binary << "fake minitouch binary fixture";
        REQUIRE(binary.good());
    }

    FakeAdbSettings settings;
    settings.set(L"UMA_FAKE_ADB_RESOLUTION", L"900x1600");
    settings.set(L"UMA_FAKE_ADB_COMMANDS_FILE", log_path.wstring());
    settings.set(L"UMA_FAKE_ADB_INTERACTIVE_TOUCH_MODE", L"split-stderr");
    settings.set(L"UMA_FAKE_ADB_INTERACTIVE_TOUCH_INPUT_FILE", input_path.wstring());

    Collector collector;
    UmaHandleGuard handle_guard{make_handle(collector)};
    auto const handle = handle_guard.get();
    auto const connection = start_connect(handle);
    REQUIRE(connection.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(connection.operation_id));
    REQUIRE(collector.terminal_for(connection.operation_id)->id == UMA_MSG_CONNECTION_SUCCEEDED);

    nlohmann::json options{
        {"version", 1}, {"touch_backend", "minitouch"},
        {"touch_binary_path", path_utf8(binary_path)},
        {"touch_remote_path", "/data/local/tmp/uma-minitouch"},
    };
    REQUIRE(UmaSetInstanceOptions(handle, options.dump().c_str()) == UMA_SUCCESS);
    REQUIRE(UmaSetInstanceOptions(handle,
        R"({"version":1,"display_id":7,"surface_display_id":"7","touch_backend":"maatouch","touch_binary_path":"touch.jar"})")
        == UMA_ERROR_INVALID_ARGUMENT);
    REQUIRE(UmaSetInstanceOptions(handle,
        R"({"version":1,"display_id":7,"surface_display_id":"7","touch_backend":"minitouch"})")
        == UMA_ERROR_INVALID_ARGUMENT);

    auto const capture = UmaCaptureAsync(handle);
    REQUIRE(capture.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(capture.operation_id));
    auto const captured = collector.terminal_for(capture.operation_id);
    REQUIRE(captured->id == UMA_MSG_FRAME_CAPTURED);
    auto const frame_id = captured->details.at("payload").at("frame_id").get<std::uint64_t>();
    auto const tap = UmaTapAsync(handle, frame_id, 540, 960);
    REQUIRE(tap.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(tap.operation_id));
    auto const tapped = collector.terminal_for(tap.operation_id);
    REQUIRE(tapped->id == UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(tapped->details.at("payload").at("actual").at("first_point").at("device_x") == 2000);
    REQUIRE(tapped->details.at("payload").at("actual").at("first_point").at("device_y") == 1500);

    auto const input_bytes = read_binary_file(input_path);
    std::string const protocol(input_bytes.begin(), input_bytes.end());
    REQUIRE(protocol.find("d 0 2000 1500 0\nc\nw 50\nu 0\nc\nw 50") != std::string::npos);
    auto const command_bytes = read_binary_file(log_path);
    std::string const commands(command_bytes.begin(), command_bytes.end());
    REQUIRE(commands.find("push\t") != std::string::npos);
    REQUIRE(commands.find("shell\tchmod\t700\t/data/local/tmp/uma-minitouch") != std::string::npos);
    REQUIRE(commands.find("/data/local/tmp/uma-minitouch -i") != std::string::npos);
    REQUIRE(commands.find("exec 3<&0;") != std::string::npos);
    REQUIRE(commands.find("'touch_pid=; cleanup_touch()") != std::string::npos);
    REQUIRE(commands.find("trap cleanup_touch HUP INT TERM; exec 3<&0;") != std::string::npos);
    REQUIRE(commands.find("shell\tinput\ttap") == std::string::npos);

    settings.set(L"UMA_FAKE_ADB_INTERACTIVE_TOUCH_MODE", L"maatouch-split");
    nlohmann::json maatouch_options{
        {"version", 1}, {"touch_backend", "maatouch"},
        {"touch_binary_path", path_utf8(binary_path)},
        {"touch_remote_path", "/data/local/tmp/uma-touch.jar"},
    };
    REQUIRE(UmaSetInstanceOptions(handle, maatouch_options.dump().c_str()) == UMA_SUCCESS);
    auto const maatouch_tap = UmaTapAsync(handle, frame_id, 540, 960);
    REQUIRE(maatouch_tap.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(maatouch_tap.operation_id));
    REQUIRE(collector.terminal_for(maatouch_tap.operation_id)->id == UMA_MSG_INPUT_SUCCEEDED);
    auto const maatouch_bytes = read_binary_file(input_path);
    std::string const maatouch_protocol(maatouch_bytes.begin(), maatouch_bytes.end());
    REQUIRE(maatouch_protocol.find("d 0 2000 1500 0") != std::string::npos);

    auto const landscape_path = test_artifact(L"uma-fake-maatouch-landscape-" + suffix + L".png");
    REQUIRE(write_solid_png(landscape_path, 1600, 900));
    settings.set(L"UMA_FAKE_ADB_SCREENSHOT_PATH", landscape_path.wstring());
    settings.set(L"UMA_FAKE_ADB_ORIENTATION", L"1");
    auto const capture_90 = UmaCaptureAsync(handle);
    REQUIRE(capture_90.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(capture_90.operation_id));
    auto const frame_90 = collector.terminal_for(capture_90.operation_id);
    REQUIRE(frame_90->id == UMA_MSG_FRAME_CAPTURED);
    auto const frame_90_id = frame_90->details.at("payload").at("frame_id").get<std::uint64_t>();
    auto const tap_90 = UmaTapAsync(handle, frame_90_id, 200, 300);
    REQUIRE(tap_90.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(tap_90.operation_id));
    auto const tap_90_event = collector.terminal_for(tap_90.operation_id);
    REQUIRE(tap_90_event->id == UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(tap_90_event->details.at("payload").at("actual").at("first_point").at("device_x") == 3372);
    REQUIRE(tap_90_event->details.at("payload").at("actual").at("first_point").at("device_y") == 557);
    REQUIRE(UmaReleaseFrame(handle, frame_90_id) == UMA_SUCCESS);

    settings.set(L"UMA_FAKE_ADB_ORIENTATION", L"3");
    auto const capture_270 = UmaCaptureAsync(handle);
    REQUIRE(capture_270.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(capture_270.operation_id));
    auto const frame_270 = collector.terminal_for(capture_270.operation_id);
    REQUIRE(frame_270->id == UMA_MSG_FRAME_CAPTURED);
    auto const frame_270_id = frame_270->details.at("payload").at("frame_id").get<std::uint64_t>();
    auto const tap_270 = UmaTapAsync(handle, frame_270_id, 200, 300);
    REQUIRE(tap_270.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(tap_270.operation_id));
    auto const tap_270_event = collector.terminal_for(tap_270.operation_id);
    REQUIRE(tap_270_event->id == UMA_MSG_INPUT_SUCCEEDED);
    REQUIRE(tap_270_event->details.at("payload").at("actual").at("first_point").at("device_x") == 625);
    REQUIRE(tap_270_event->details.at("payload").at("actual").at("first_point").at("device_y") == 2440);
    auto const startup_marker = test_artifact(L"uma-fake-touch-started-" + suffix + L".txt");
    fs::remove(startup_marker, ignored);
    settings.set(L"UMA_FAKE_ADB_TOUCH_PROCESS_STARTED_FILE", startup_marker.wstring());
    settings.set(L"UMA_FAKE_ADB_INTERACTIVE_TOUCH_MODE", L"delayed-owner");
    auto const portrait_path = test_artifact(L"uma-fake-minitouch-portrait-" + suffix + L".png");
    REQUIRE(write_solid_png(portrait_path, 900, 1600));
    auto count_owned_kill_requests = [](std::string_view text) {
        std::size_t count = 0;
        std::size_t position = 0;
        while ((position = text.find("kill -TERM 42424", position)) != std::string_view::npos)
        {
            ++count;
            position += 1;
        }
        return count;
    };
    nlohmann::json raw_touch_options{
        {"version", 1}, {"touch_backend", "minitouch"},
        {"touch_binary_path", path_utf8(binary_path)},
        {"touch_remote_path", "/data/local/tmp/uma-minitouch"},
    };
    REQUIRE(UmaSetInstanceOptions(handle, raw_touch_options.dump().c_str()) == UMA_SUCCESS);
    auto const prior_command_bytes = read_binary_file(log_path);
    std::string const prior_command_text(prior_command_bytes.begin(), prior_command_bytes.end());
    auto const prior_kill_count = count_owned_kill_requests(prior_command_text);
    auto const cancel_tap = UmaTapAsync(handle, frame_270_id, 200, 300);
    REQUIRE(cancel_tap.error_code == UMA_SUCCESS);
    auto const cancel_deadline = std::chrono::steady_clock::now() + 3s;
    while (!fs::exists(startup_marker) && std::chrono::steady_clock::now() < cancel_deadline)
        std::this_thread::sleep_for(5ms);
    REQUIRE(fs::exists(startup_marker));
    REQUIRE(UmaCancelOperation(handle, cancel_tap.operation_id) == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(cancel_tap.operation_id));
    auto const canceled_tap = collector.terminal_for(cancel_tap.operation_id);
    REQUIRE(canceled_tap->id == UMA_MSG_INPUT_FAILED);
    REQUIRE(canceled_tap->details.at("payload").at("error_code") == UMA_ERROR_CANCELED);
    auto const after_cancel_command_bytes = read_binary_file(log_path);
    std::string const after_cancel_command_text(
        after_cancel_command_bytes.begin(), after_cancel_command_bytes.end());
    REQUIRE(count_owned_kill_requests(after_cancel_command_text) > prior_kill_count);
    REQUIRE(UmaReleaseFrame(handle, frame_270_id) == UMA_SUCCESS);

    settings.set(L"UMA_FAKE_ADB_INTERACTIVE_TOUCH_MODE", L"split-stderr");
    struct RotationCase { wchar_t const* orientation; fs::path screenshot; int x; int y; };
    RotationCase const rotation_cases[]{
        {L"0", portrait_path, 742, 469},
        {L"1", landscape_path, 3258, 2529},
        {L"2", portrait_path, 747, 471},
        {L"3", landscape_path, 3253, 2531},
    };
    for (std::size_t index = 0; index < std::size(rotation_cases); ++index)
    {
        settings.set(L"UMA_FAKE_ADB_ORIENTATION", rotation_cases[index].orientation);
        settings.set(L"UMA_FAKE_ADB_SCREENSHOT_PATH", rotation_cases[index].screenshot.wstring());
        auto const rotated_capture = UmaCaptureAsync(handle);
        REQUIRE(rotated_capture.error_code == UMA_SUCCESS);
        REQUIRE(collector.wait_for_operation_terminal(rotated_capture.operation_id));
        auto const rotated_frame_event = collector.terminal_for(rotated_capture.operation_id);
        REQUIRE(rotated_frame_event->id == UMA_MSG_FRAME_CAPTURED);
        auto const rotated_frame_id = rotated_frame_event->details.at("payload").at("frame_id").get<std::uint64_t>();
        auto const raw_touch_tap = UmaTapAsync(handle, rotated_frame_id, 200, 300);
        REQUIRE(raw_touch_tap.error_code == UMA_SUCCESS);
        REQUIRE(collector.wait_for_operation_terminal(raw_touch_tap.operation_id));
        auto const raw_touch_event = collector.terminal_for(raw_touch_tap.operation_id);
        REQUIRE(raw_touch_event->id == UMA_MSG_INPUT_SUCCEEDED);
        REQUIRE(raw_touch_event->details.at("payload").at("actual").at("first_point").at("device_x")
                == rotation_cases[index].x);
        REQUIRE(raw_touch_event->details.at("payload").at("actual").at("first_point").at("device_y")
                == rotation_cases[index].y);
        auto const raw_protocol_bytes = read_binary_file(input_path);
        std::string const raw_protocol(raw_protocol_bytes.begin(), raw_protocol_bytes.end());
        REQUIRE(raw_protocol.find("d 0 " + std::to_string(rotation_cases[index].x) + " "
            + std::to_string(rotation_cases[index].y) + " 0") != std::string::npos);
        REQUIRE(UmaReleaseFrame(handle, rotated_frame_id) == UMA_SUCCESS);
    }

    auto const final_bytes = read_binary_file(log_path);
    std::string const final_commands(final_bytes.begin(), final_bytes.end());
    REQUIRE(final_commands.find("app_process /data/local/tmp com.shxyke.MaaTouch.App")
            != std::string::npos);
    REQUIRE(UmaReleaseFrame(handle, frame_id) == UMA_SUCCESS);
    fs::remove(log_path, ignored);
    fs::remove(input_path, ignored);
    fs::remove(binary_path, ignored);
    fs::remove(landscape_path, ignored);
    fs::remove(portrait_path, ignored);
    fs::remove(startup_marker, ignored);
}

TEST_CASE("interactive held touch survives capture, releases its disposed source frame, and retains failed owner cleanup",
    "[UmaCaller][device][touch][interactive][held]")
{
    auto const suffix = std::to_wstring(GetCurrentProcessId());
    auto const log_path = test_artifact(L"uma-fake-interactive-held-" + suffix + L".tsv");
    auto const input_path = test_artifact(L"uma-fake-interactive-held-input-" + suffix + L".txt");
    auto const binary_path = test_artifact(L"fake-maatouch-held-" + suffix + L".jar");
    auto const portrait_path = test_artifact(L"uma-fake-interactive-held-portrait-" + suffix + L".png");
    auto const landscape_path = test_artifact(L"uma-fake-interactive-held-landscape-" + suffix + L".png");
    std::error_code ignored;
    for (auto const& path : {log_path, input_path, binary_path, portrait_path, landscape_path})
        fs::remove(path, ignored);
    {
        std::ofstream binary(binary_path, std::ios::binary | std::ios::trunc);
        binary << "fake MaaTouch helper fixture";
        REQUIRE(binary.good());
    }
    REQUIRE(write_solid_png(portrait_path, 900, 1600));
    REQUIRE(write_solid_png(landscape_path, 1600, 900));

    FakeAdbSettings settings;
    settings.set(L"UMA_FAKE_ADB_RESOLUTION", L"900x1600");
    settings.set(L"UMA_FAKE_ADB_ORIENTATION", L"0");
    settings.set(L"UMA_FAKE_ADB_SCREENSHOT_PATH", portrait_path.wstring());
    settings.set(L"UMA_FAKE_ADB_COMMANDS_FILE", log_path.wstring());
    settings.set(L"UMA_FAKE_ADB_INTERACTIVE_TOUCH_MODE", L"maatouch-split");
    settings.set(L"UMA_FAKE_ADB_INTERACTIVE_TOUCH_INPUT_FILE", input_path.wstring());

    Collector collector;
    UmaHandleGuard handle_guard{make_handle(collector)};
    auto const handle = handle_guard.get();
    auto const connection = start_connect(handle);
    REQUIRE(connection.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(connection.operation_id));
    REQUIRE(collector.terminal_for(connection.operation_id)->id == UMA_MSG_CONNECTION_SUCCEEDED);

    nlohmann::json options{
        {"version", 1}, {"touch_backend", "maatouch"},
        {"touch_binary_path", path_utf8(binary_path)},
        {"touch_remote_path", "/data/local/tmp/uma-held-touch.jar"},
    };
    REQUIRE(UmaSetInstanceOptions(handle, options.dump().c_str()) == UMA_SUCCESS);

    auto const capture_one = UmaCaptureAsync(handle);
    REQUIRE(capture_one.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(capture_one.operation_id));
    auto const frame_one_event = collector.terminal_for(capture_one.operation_id);
    REQUIRE(frame_one_event->id == UMA_MSG_FRAME_CAPTURED);
    auto const frame_one = frame_one_event->details.at("payload").at("frame_id").get<std::uint64_t>();
    auto command_text = [&]() {
        auto const bytes = read_binary_file(log_path);
        return std::string(bytes.begin(), bytes.end());
    };
    auto occurrences = [](std::string_view text, std::string_view needle) {
        std::size_t count = 0;
        for (std::size_t position = 0;
             (position = text.find(needle, position)) != std::string_view::npos;
             position += needle.size())
            ++count;
        return count;
    };
    auto const before_prepare = command_text();
    prepare_held_touch(collector, handle, frame_one);
    auto const after_prepare = command_text();
    auto const push_count = occurrences(after_prepare, "\tpush\t");
    auto const helper_launch_count = occurrences(after_prepare, "app_process /data/local/tmp");
    REQUIRE(push_count > occurrences(before_prepare, "\tpush\t"));
    REQUIRE(helper_launch_count > occurrences(before_prepare, "app_process /data/local/tmp"));
    auto const down_one = UmaTouchAsync(handle, frame_one, 540, 960, UMA_TOUCH_DOWN);
    REQUIRE(down_one.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(down_one.operation_id));
    REQUIRE(collector.terminal_for(down_one.operation_id)->id == UMA_MSG_INPUT_SUCCEEDED);
    auto const after_down = command_text();
    REQUIRE(occurrences(after_down, "\tpush\t") == push_count);
    REQUIRE(occurrences(after_down, "app_process /data/local/tmp") == helper_launch_count);

    auto const capture_two = UmaCaptureAsync(handle);
    REQUIRE(capture_two.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(capture_two.operation_id));
    auto const frame_two_event = collector.terminal_for(capture_two.operation_id);
    REQUIRE(frame_two_event->id == UMA_MSG_FRAME_CAPTURED);
    auto const frame_two = frame_two_event->details.at("payload").at("frame_id").get<std::uint64_t>();
    REQUIRE(UmaReleaseFrame(handle, frame_one) == UMA_SUCCESS);
    auto const up_one = UmaTouchAsync(handle, frame_one, 0, 0, UMA_TOUCH_UP);
    REQUIRE(up_one.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(up_one.operation_id));
    REQUIRE(collector.terminal_for(up_one.operation_id)->id == UMA_MSG_INPUT_SUCCEEDED);

    prepare_held_touch(collector, handle, frame_two);
    auto const down_two = UmaTouchAsync(handle, frame_two, 540, 960, UMA_TOUCH_DOWN);
    REQUIRE(down_two.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(down_two.operation_id));
    REQUIRE(collector.terminal_for(down_two.operation_id)->id == UMA_MSG_INPUT_SUCCEEDED);
    settings.set(L"UMA_FAKE_ADB_ORIENTATION", L"1");
    settings.set(L"UMA_FAKE_ADB_SCREENSHOT_PATH", landscape_path.wstring());
    auto const capture_rotated = UmaCaptureAsync(handle);
    REQUIRE(capture_rotated.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(capture_rotated.operation_id));
    auto const rotated_event = collector.terminal_for(capture_rotated.operation_id);
    REQUIRE(rotated_event->id == UMA_MSG_FRAME_CAPTURED);
    auto const rotated_frame = rotated_event->details.at("payload").at("frame_id").get<std::uint64_t>();
    auto const input_bytes = read_binary_file(input_path);
    std::string const input_protocol(input_bytes.begin(), input_bytes.end());
    REQUIRE(input_protocol.find("d 0 2000 1500 0\nc") != std::string::npos);
    REQUIRE(input_protocol.find("u 0\nc\nr") != std::string::npos);
    auto const stale_up = UmaTouchAsync(handle, frame_two, 0, 0, UMA_TOUCH_UP);
    REQUIRE(stale_up.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(stale_up.operation_id));
    REQUIRE(collector.terminal_for(stale_up.operation_id)->id == UMA_MSG_INPUT_FAILED);
    prepare_held_touch(collector, handle, rotated_frame);
    auto const down_rotated = UmaTouchAsync(handle, rotated_frame, 540, 960, UMA_TOUCH_DOWN);
    REQUIRE(down_rotated.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(down_rotated.operation_id));
    REQUIRE(collector.terminal_for(down_rotated.operation_id)->id == UMA_MSG_INPUT_SUCCEEDED);
    auto const cancel_rotated = UmaTouchAsync(handle, rotated_frame, 0, 0, UMA_TOUCH_CANCEL);
    REQUIRE(cancel_rotated.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(cancel_rotated.operation_id));
    REQUIRE(collector.terminal_for(cancel_rotated.operation_id)->id == UMA_MSG_INPUT_SUCCEEDED);

    // A failed remote kill must keep the owner PID/token so a subsequent
    // configuration attempt can retry cleanup instead of forgetting it.
    auto const rotated_tap = UmaTapAsync(handle, rotated_frame, 540, 960);
    REQUIRE(rotated_tap.error_code == UMA_SUCCESS);
    REQUIRE(collector.wait_for_operation_terminal(rotated_tap.operation_id));
    REQUIRE(collector.terminal_for(rotated_tap.operation_id)->id == UMA_MSG_INPUT_SUCCEEDED);
    auto const log_before_cleanup = read_binary_file(log_path);
    std::string const commands_before_cleanup(log_before_cleanup.begin(), log_before_cleanup.end());
    auto count_kill = [](std::string_view text) {
        std::size_t count = 0;
        std::size_t position = 0;
        while ((position = text.find("kill -TERM 42424", position)) != std::string_view::npos)
        {
            ++count;
            position += 1;
        }
        return count;
    };
    auto const prior_kill_count = count_kill(commands_before_cleanup);
    settings.set(L"UMA_FAKE_ADB_TOUCH_STOP_FAIL", L"1");
    auto const first_switch = UmaSetInstanceOptions(handle, R"({"version":1})");
    REQUIRE(first_switch == UMA_ERROR_COMMAND_FAILED);
    settings.set(L"UMA_FAKE_ADB_TOUCH_STOP_FAIL", L"");
    REQUIRE(UmaSetInstanceOptions(handle, R"({"version":1})") == UMA_SUCCESS);
    auto const log_after_cleanup = read_binary_file(log_path);
    std::string const commands_after_cleanup(log_after_cleanup.begin(), log_after_cleanup.end());
    REQUIRE(count_kill(commands_after_cleanup) >= prior_kill_count + 2);

    REQUIRE(UmaReleaseFrame(handle, frame_two) == UMA_SUCCESS);
    REQUIRE(UmaReleaseFrame(handle, rotated_frame) == UMA_SUCCESS);
    fs::remove(log_path, ignored);
    fs::remove(input_path, ignored);
    fs::remove(binary_path, ignored);
    fs::remove(portrait_path, ignored);
    fs::remove(landscape_path, ignored);
}
