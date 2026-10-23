#include <catch2/catch_test_macros.hpp>

#include "TaskEngine/NativeTaskExecutionContext.hpp"
#include "TaskEngine/HachimiPipelineLoader.hpp"
#include "TaskEngine/TaskGraphRunner.hpp"
#include "GameTasks/NativeGameTaskRunner.hpp"
#include "Common/PathEncoding.hpp"
#include "Resources/WicImageDecoder.hpp"
#include "Resources/ImageTransforms.hpp"

#include <nlohmann/json.hpp>

#if defined(_WIN32)
#include <windows.h>
#include <wincodec.h>
#include <wrl/client.h>
inline uint32_t current_process_id() { return ::GetCurrentProcessId(); }
#else
#include <unistd.h>
#include <CoreFoundation/CoreFoundation.h>
#include <CoreGraphics/CoreGraphics.h>
#include <ImageIO/ImageIO.h>
inline uint32_t current_process_id() { return static_cast<uint32_t>(getpid()); }
#define GetCurrentProcessId current_process_id
using UINT = unsigned int;
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

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <map>
#include <sstream>
#include <thread>
#include <vector>

#if defined(__APPLE__)
TEST_CASE("Mac isolated and task image OCR share fixes and respect cancellation", "[NativeTaskContext][MacOcr]")
{
    using namespace UmaAssistant;
    auto image = Resources::WicImageDecoder::decode_file(std::filesystem::path(CMAKE_SOURCE_DIR)
        / "testdata/hachimi/ura/captures/career_skill_gourmand_selected_bluestacks_mac_20261023.png");
    image = Resources::crop_image(image, 625, 704, 220, 155);
    Recognition::WindowsOcrRecognizer reader;
    std::stop_source cancellation;
    TaskEngine::NativeTaskExecutionContext context(nullptr, nullptr, &reader, nullptr, 1,
        cancellation.get_token(), {});
    const auto isolated = context.CreateImageRecognitionContext();
    for (const auto& lines : {context.RecognizeImageText(image, "en-US"),
        isolated->RecognizeImageText(image, "en-US")})
    {
        int obtained = 0;
        for (const auto& line : lines)
        {
            INFO(line.text);
            if (line.text.find("Obtained") != std::string::npos
                || line.text.find("OBTAINED") != std::string::npos) ++obtained;
        }
        CHECK(obtained == 1);
    }
    cancellation.request_stop();
    CHECK(context.RecognizeImageText(image, "en-US").empty());
    CHECK(isolated->RecognizeImageText(image, "en-US").empty());
}
#endif

namespace
{

namespace fs = std::filesystem;
using namespace UmaAssistant;
using namespace UmaAssistant::TaskEngine;

class Environment final
{
public:
    void Set(std::wstring const& name, std::wstring const& value)
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

    ~Environment()
    {
        for (auto const& [name, value] : previous)
            SetEnvironmentVariableW(name.c_str(), value.empty() ? nullptr : value.c_str());
    }

private:
    std::map<std::wstring, std::wstring> previous;
};

#if defined(_WIN32)
bool WritePng(fs::path const& path, UINT width, UINT height,
    std::array<std::uint8_t, 4> bgra = {255, 255, 255, 255})
{
    auto const initialization = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(initialization) && initialization != RPC_E_CHANGED_MODE) return false;
    struct Cleanup
    {
        bool initialized;
        ~Cleanup() { if (initialized) CoUninitialize(); }
    } cleanup{SUCCEEDED(initialization)};
    using Microsoft::WRL::ComPtr;
    ComPtr<IWICImagingFactory> factory;
    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                IID_PPV_ARGS(&factory)))) return false;
    ComPtr<IWICStream> stream;
    if (FAILED(factory->CreateStream(&stream))
        || FAILED(stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE))) return false;
    ComPtr<IWICBitmapEncoder> encoder;
    if (FAILED(factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder))
        || FAILED(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache))) return false;
    ComPtr<IWICBitmapFrameEncode> frame;
    if (FAILED(encoder->CreateNewFrame(&frame, nullptr))
        || FAILED(frame->Initialize(nullptr)) || FAILED(frame->SetSize(width, height))) return false;
    auto format = GUID_WICPixelFormat32bppBGRA;
    if (FAILED(frame->SetPixelFormat(&format))
        || !IsEqualGUID(format, GUID_WICPixelFormat32bppBGRA)) return false;
    auto const stride = width * 4;
    std::vector<std::uint8_t> pixels(static_cast<std::size_t>(stride) * height, 255);
    for (std::size_t index = 0; index < pixels.size(); index += 4)
        std::copy(bgra.begin(), bgra.end(), pixels.begin() + static_cast<std::ptrdiff_t>(index));
    return SUCCEEDED(frame->WritePixels(height, stride, static_cast<UINT>(pixels.size()), pixels.data()))
        && SUCCEEDED(frame->Commit()) && SUCCEEDED(encoder->Commit());
}
#else
bool WritePng(fs::path const& path, unsigned int width, unsigned int height,
    std::array<std::uint8_t, 4> bgra = {255, 255, 255, 255})
{
    std::string const u8path = path.string();
    CFURLRef url = CFURLCreateFromFileSystemRepresentation(kCFAllocatorDefault,
        reinterpret_cast<UInt8 const*>(u8path.data()), static_cast<CFIndex>(u8path.size()), false);
    if (!url) return false;
    CGImageDestinationRef dest = CGImageDestinationCreateWithURL(url, CFSTR("public.png"), 1, nullptr);
    CFRelease(url);
    if (!dest) return false;

    auto const stride = width * 4;
    std::vector<std::uint8_t> pixels(static_cast<std::size_t>(stride) * height, 255);
    for (std::size_t index = 0; index < pixels.size(); index += 4)
        std::copy(bgra.begin(), bgra.end(), pixels.begin() + static_cast<std::ptrdiff_t>(index));

    CGColorSpaceRef colorSpace = CGColorSpaceCreateDeviceRGB();
    CGContextRef ctx = CGBitmapContextCreate(pixels.data(), width, height, 8, stride,
        colorSpace, static_cast<CGBitmapInfo>(static_cast<uint32_t>(kCGBitmapByteOrder32Little) | static_cast<uint32_t>(kCGImageAlphaPremultipliedFirst)));
    CGImageRef img = ctx ? CGBitmapContextCreateImage(ctx) : nullptr;
    if (ctx) CGContextRelease(ctx);
    CGColorSpaceRelease(colorSpace);

    if (!img) { CFRelease(dest); return false; }
    CGImageDestinationAddImage(dest, img, nullptr);
    bool const ok = CGImageDestinationFinalize(dest);
    CFRelease(img);
    CFRelease(dest);
    return ok;
}
#endif

class Fixture final
{
public:
    Fixture(int width = 450, int height = 800, int orientation = 0, std::stop_token cancellation = {})
    {
        static std::atomic<unsigned int> sequence{};
        auto const fixtureRoot = fs::path(CMAKE_SOURCE_DIR) / "build" / "phase3-context-fixtures";
        fs::create_directories(fixtureRoot);
        auto const nonce = std::chrono::steady_clock::now().time_since_epoch().count();
        directory = fixtureRoot / (std::to_string(GetCurrentProcessId()) + "-"
            + std::to_string(nonce) + "-" + std::to_string(++sequence));
        REQUIRE(fs::create_directory(directory));
        auto const screenshot = directory / "frame.png";
        REQUIRE(WritePng(screenshot, static_cast<UINT>(width), static_cast<UINT>(height)));
        commandLog = directory / "commands.txt";
        environment.Set(L"UMA_FAKE_ADB_DELAY_MS", L"0");
        environment.Set(L"UMA_FAKE_ADB_INPUT_FAIL", L"");
        environment.Set(L"UMA_FAKE_ADB_GETEVENT_FAIL", L"");
        environment.Set(L"UMA_FAKE_ADB_GETEVENT_DELAY_MS", L"");
        environment.Set(L"UMA_FAKE_ADB_TOUCH_DOWN_DELAY_MS", L"");
        environment.Set(L"UMA_FAKE_ADB_CAPTURE_MODE", L"");
        environment.Set(L"UMA_FAKE_ADB_GEOMETRY_BATCH_MODE", L"");
        environment.Set(L"UMA_FAKE_ADB_SCREENSHOT_PATH", screenshot.wstring());
        environment.Set(L"UMA_FAKE_ADB_RESOLUTION", orientation % 2 == 0
            ? std::to_wstring(width) + L"x" + std::to_wstring(height)
            : std::to_wstring(height) + L"x" + std::to_wstring(width));
        environment.Set(L"UMA_FAKE_ADB_ORIENTATION", std::to_wstring(orientation));
        environment.Set(L"UMA_FAKE_ADB_COMMANDS_FILE", commandLog.wstring());
        auto profile = std::make_shared<ConnectionProfile const>(ConnectionProfile::default_profile());
        session = std::make_shared<DeviceSession>(profile, fs::path(UMA_FAKE_ADB_PATH), "General",
            ConnectedDevice{"emulator-5554", "test", "14", width, height, width, height}, 77);
        resources = std::make_shared<Resources::ResourceSnapshot const>(directory);
        context = std::make_unique<NativeTaskExecutionContext>(session, resources, nullptr, nullptr,
            1, cancellation, directory, "mail-collection");
        context->SetCoordinateReference(900, 1600);
        REQUIRE(context->CaptureScreen());
        // Persistence identity comes from the connected session, independently
        // of template roots or editable task settings.
        CHECK(context->GetDeviceSerial() == "emulator-5554");
    }

    [[nodiscard]] TaskMatchResult MatchAt(int x, int y) const
    {
        auto const frame = context->GetCurrentFrame();
        REQUIRE(frame);
        TaskMatchResult match;
        match.found = true;
        match.score = 1.0;
        match.x = x;
        match.y = y;
        match.referenceWidth = 900;
        match.referenceHeight = 1600;
        match.frameId = frame->frameId;
        match.sessionGeneration = frame->sessionGeneration;
        match.geometryGeneration = frame->geometryGeneration;
        return match;
    }

    [[nodiscard]] std::vector<std::pair<int, int>> Taps() const
    {
        std::ifstream input(commandLog);
        std::vector<std::pair<int, int>> taps;
        for (std::string line; std::getline(input, line);)
        {
            auto const position = line.find("\tinput\ttap\t");
            if (position == std::string::npos) continue;
            std::istringstream coordinates(line.substr(position + 11));
            int x{}, y{};
            if (coordinates >> x >> y) taps.emplace_back(x, y);
        }
        return taps;
    }

    [[nodiscard]] std::string Commands() const
    {
        std::ifstream input(commandLog);
        return {std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>()};
    }

    Environment environment;
    fs::path directory;
    fs::path commandLog;
    std::shared_ptr<DeviceSession> session;
    std::shared_ptr<Resources::ResourceSnapshot const> resources;
    std::unique_ptr<NativeTaskExecutionContext> context;
};

} // namespace

TEST_CASE("Native HSV probes recognize shop checkbox green without RGB restrictions", "[context][hsv][shop]")
{
    for (int width : {450, 900})
    {
        CAPTURE(width);
        Fixture fixture(width, width * 16 / 9);
        auto& context = *fixture.context;
        // White is the unchecked state. Use the same range and reference
        // coordinates as ShopItemSelector, through the real capture path.
        CHECK_FALSE(context.ProbeHsv(765, 762, 22, 70, 160, 0.38, 1, 0.3, 1, 0.10));

        REQUIRE(WritePng(fixture.directory / "frame.png", static_cast<UINT>(width),
            static_cast<UINT>(width * 16 / 9), {96, 200, 64, 255}));
        REQUIRE(context.CaptureScreen());
        CHECK(context.ProbeHsv(765, 762, 22, 70, 160, 0.38, 1, 0.3, 1, 0.10));
        CHECK(context.ProbeHsv(765, 762, 22, 70, 160, 0.38, 1, 0.3, 1, 1.0));
        CHECK_FALSE(context.ProbeHsv(765, 762, 22, 200, 250, 0.38, 1, 0.3, 1, 0.10));
        CHECK_FALSE(context.ProbeHsv(765, 762, 22, 70, 160, 0.9, 1, 0.3, 1, 0.10));
        CHECK_FALSE(context.ProbeHsv(765, 762, 22, 70, 160, 0.38, 1, 0.9, 1, 0.10));

        // A colored unchecked-state miss must not be mistaken for green.
        REQUIRE(WritePng(fixture.directory / "frame.png", static_cast<UINT>(width),
            static_cast<UINT>(width * 16 / 9), {64, 96, 200, 255}));
        REQUIRE(context.CaptureScreen());
        CHECK_FALSE(context.ProbeHsv(765, 762, 22, 70, 160, 0.38, 1, 0.3, 1, 0.10));
    }
}

TEST_CASE("Native direct coordinate clicks need no screenshot", "[phase3][context][CoordinateClick]")
{
    Fixture fixture;
    auto definition = HachimiPipelineLoader::LoadFromJsonString(R"({
      "tasks":{"Burst":{"algorithm":"JustReturn","action":"ClickRect",
        "specificRect":[440,1290,20,20],"repeat":2,"repeat_delay":150,"success":true}}
    })");
    REQUIRE(definition);
    { std::ofstream clear(fixture.commandLog, std::ios::trunc); }
    TaskGraphRunner runner;
    const auto result = runner.Run(*definition, "Burst", *fixture.context);
    INFO(result.errorMessage);
    REQUIRE(result.status == TaskGraphStatus::Success);
    CHECK((fixture.Taps() == std::vector<std::pair<int, int>>{{225, 650}, {225, 650}}));
    CHECK_FALSE(fixture.context->HasRetainedMatch());
    const auto commands = fixture.Commands();
    const auto firstTap = commands.find("\tinput\ttap\t");
    REQUIRE(firstTap != std::string::npos);
    CHECK(commands.find("screencap") == std::string::npos);
}

TEST_CASE("Guarded direct clicks follow a matched click without a screenshot in between", "[phase3][context][CoordinateClick][GameRecovery]")
{
    const auto resourceRoot = fs::path(CMAKE_SOURCE_DIR) / "resource";
    Resources::ResourceSnapshot resources(resourceRoot);
    Fixture fixture;
    const auto definitionPath = fixture.directory / "immediate-click-chain.json";
    const nlohmann::json definition = {
        {"name", "immediate-click-chain"}, {"referenceWidth", 900}, {"referenceHeight", 1600},
        {"tasks", {
            {"home", {{"action", "DoNothing"}, {"next", {"Matched"}}}},
            {"Matched", {{"algorithm", "MatchTemplate"}, {"action", "ClickSelf"},
                {"template", Paths::ToUtf8(fixture.directory / "frame.png")},
                {"next", {"Blank"}}}},
            {"Blank", {{"algorithm", "JustReturn"}, {"action", "ClickRect"},
                {"specificRect", {440, 1290, 20, 20}}, {"repeat", 2},
                {"repeat_delay", 150}, {"success", true}}}
        }}
    };
    { std::ofstream output(definitionPath); output << definition; }
    const auto result = GameTasks::NativeGameTaskRunner::Run("mail-collection",
        nlohmann::json{{"definitionPath", Paths::ToUtf8(definitionPath)}},
        *fixture.context, resourceRoot, nullptr, &resources);
    INFO(result.message);
    REQUIRE(result.succeeded);
    REQUIRE(fixture.Taps().size() == 3);
    const auto commands = fixture.Commands();
    const auto firstTap = commands.find("\tinput\ttap\t");
    REQUIRE(firstTap != std::string::npos);
    CHECK(commands.find("screencap", firstTap) == std::string::npos);
}

TEST_CASE("Direct coordinate clicks scale to every display orientation without screenshots", "[phase3][context][CoordinateClick]")
{
    for (int orientation = 0; orientation < 4; ++orientation)
    {
        CAPTURE(orientation);
        const int width = orientation % 2 == 0 ? 450 : 800;
        const int height = orientation % 2 == 0 ? 800 : 450;
        Fixture fixture(width, height, orientation);
        { std::ofstream clear(fixture.commandLog, std::ios::trunc); }
        REQUIRE(fixture.context->TapCoordinate(180, 320));
        const auto taps = fixture.Taps();
        REQUIRE(taps.size() == 1);
        CHECK(std::abs(taps[0].first - width / 5) <= 1);
        CHECK(std::abs(taps[0].second - height / 5) <= 1);
        CHECK(fixture.Commands().find("screencap") == std::string::npos);
    }
}

TEST_CASE("Guarded coordinate clicks retain their source across the pre-input capture", "[phase3][context][CoordinateClick][GameRecovery]")
{
    auto const resourceRoot = fs::path(CMAKE_SOURCE_DIR) / "resource";
    Resources::ResourceSnapshot resources(resourceRoot);
    for (auto const* action : {"ClickRect", "ClickSpecificRect"})
    for (int repeat : {1, 2})
    {
        CAPTURE(action, repeat);
        Fixture fixture;
        auto const definitionPath = fixture.directory / "guarded-race-click.json";
        nlohmann::json const definition = {
            {"name", "guarded-race-click"}, {"referenceWidth", 900}, {"referenceHeight", 1600},
            {"tasks", {
                {"home", {{"action", "DoNothing"}, {"next", {"race_list_race_start"}}}},
                {"race_list_race_start", {{"algorithm", "ClickRect"}, {"action", action},
                    {"specificRect", {270, 1300, 360, 120}}, {"repeat", repeat}, {"success", true}}}
            }}
        };
        {
            std::ofstream output(definitionPath, std::ios::binary);
            REQUIRE(output.good());
            output << definition;
        }
        auto const result = GameTasks::NativeGameTaskRunner::Run("mail-collection",
            nlohmann::json{{"definitionPath", Paths::ToUtf8(definitionPath)}},
            *fixture.context, resourceRoot, nullptr, &resources);
        INFO(result.message);
        CHECK(result.succeeded);
        CHECK((fixture.Taps() == std::vector<std::pair<int, int>>(
            static_cast<std::size_t>(repeat), {225, 680})));
        CHECK_FALSE(fixture.context->HasRetainedMatch());
    }
}

TEST_CASE("Native captures reuse pixels identical to the public PNG decoder", "[phase3][context][capture]")
{
    Fixture fixture;
    DeviceSessionOptions options;
    SECTION("colored PNG")
    {
        options.screenshot_method = DeviceSessionOptions::ScreenshotMethod::png;
        fixture.environment.Set(L"UMA_FAKE_ADB_RESOLUTION", L"900x1600");
        fixture.environment.Set(L"UMA_FAKE_ADB_SCREENSHOT_PATH",
            (fs::path(CMAKE_SOURCE_DIR) / "testdata/hachimi/ura/captures/career_skill_race_day_490_sample.png").wstring());
    }
    SECTION("raw screenshot")
    {
        options.screenshot_method = DeviceSessionOptions::ScreenshotMethod::raw;
        fixture.environment.Set(L"UMA_FAKE_ADB_CAPTURE_MODE", L"raw");
        fixture.environment.Set(L"UMA_FAKE_ADB_RESOLUTION", L"10x16");
    }
    SECTION("raw screenshot with CRLF")
    {
        options.screenshot_method = DeviceSessionOptions::ScreenshotMethod::raw;
        fixture.environment.Set(L"UMA_FAKE_ADB_CAPTURE_MODE", L"raw-crlf");
        fixture.environment.Set(L"UMA_FAKE_ADB_RESOLUTION", L"10x16");
    }
    SECTION("gzip screenshot")
    {
        options.screenshot_method = DeviceSessionOptions::ScreenshotMethod::gzip;
        fixture.environment.Set(L"UMA_FAKE_ADB_CAPTURE_MODE", L"gzip");
        fixture.environment.Set(L"UMA_FAKE_ADB_RESOLUTION", L"10x16");
    }
    REQUIRE_FALSE(fixture.session->update_options(options));
    auto const previous = fixture.context->GetCurrentFrame();
    REQUIRE(fixture.context->CaptureScreen());
    auto const frame = fixture.context->GetCurrentFrame();
    REQUIRE(frame);
    auto const metadata = fixture.session->frame_metadata(frame->frameId);
    REQUIRE(metadata);
    auto const pixels = fixture.session->frame_pixels(frame->frameId);
    REQUIRE(pixels);
    if (options.screenshot_method != DeviceSessionOptions::ScreenshotMethod::png)
        CHECK(metadata->byte_count == 0); // Recognition did not encode a PNG.
    auto const pngSize = fixture.session->frame_png_size(frame->frameId);
    REQUIRE(pngSize);
    REQUIRE(*pngSize > 0);
    std::vector<std::uint8_t> png(static_cast<std::size_t>(*pngSize));
    REQUIRE(fixture.session->copy_frame_png(frame->frameId, png.data(), png.size()));
    auto const exported = fixture.session->frame_metadata(frame->frameId);
    REQUIRE(exported);
    CHECK(exported->byte_count == png.size());
    auto const expected = Resources::WicImageDecoder::decode_bytes(png);
    CHECK(pixels->width == expected.width);
    CHECK(pixels->height == expected.height);
    CHECK(pixels->rgba == expected.rgba);
    CHECK(pixels->gray == expected.gray);
    CHECK(frame->rgbaPixels == expected.rgba);
    CHECK(frame->grayPixels == expected.gray);
    CHECK(fixture.context->GetPreviousFrame() == previous);

    REQUIRE(fixture.session->release_frame(frame->frameId));
    CHECK_FALSE(fixture.session->frame_pixels(frame->frameId));
    CHECK_FALSE(fixture.session->frame_png_size(frame->frameId));
    CHECK_FALSE(fixture.session->copy_frame_png(frame->frameId, png.data(), png.size()));
    CHECK(pixels->rgba == expected.rgba); // Immutable ownership outlives release.
    auto const external = fixture.session->capture({});
    REQUIRE_FALSE(external.failure);
    REQUIRE(external.frame);
    CHECK(external.frame->byte_count > 0);
    CHECK_FALSE(fixture.session->frame_pixels(external.frame->id));
    std::vector<std::uint8_t> externalPng(static_cast<std::size_t>(external.frame->byte_count));
    REQUIRE(fixture.session->copy_frame_png(external.frame->id, externalPng.data(), externalPng.size()));
    auto const externalImage = Resources::WicImageDecoder::decode_bytes(externalPng);
    CHECK(externalImage.rgba == expected.rgba);
    CHECK(externalImage.gray == expected.gray);
    REQUIRE(fixture.session->release_frame(external.frame->id));
}

TEST_CASE("Pixel captures defer PNG export while retaining geometry checks and operation timings", "[context][capture][timing]")
{
    Fixture fixture;
    DeviceSessionOptions options;
    options.screenshot_method = DeviceSessionOptions::ScreenshotMethod::raw;
    REQUIRE_FALSE(fixture.session->update_options(options));
    fixture.environment.Set(L"UMA_FAKE_ADB_CAPTURE_MODE", L"raw");
    fixture.environment.Set(L"UMA_FAKE_ADB_RESOLUTION", L"10x16");
    fixture.environment.Set(L"UMA_FAKE_ADB_DELAY_MS", L"20");
    auto const capture = fixture.session->capture_pixels({});
    REQUIRE_FALSE(capture.failure);
    REQUIRE(capture.frame);
    REQUIRE(capture.timings);
    const auto& timing = *capture.timings;
    CHECK(timing.method == "exec_raw");
    CHECK(timing.png_deferred);
    CHECK(timing.encode_ms == 0);
    CHECK(timing.adb_calls == 3);
    CHECK(timing.capture_attempts == 1);
    CHECK(timing.geometry_ms > 0);
    CHECK(timing.command_ms > 0);
    CHECK(timing.total_ms >= timing.geometry_ms + timing.command_ms + timing.decode_ms);
    CHECK(capture.frame->byte_count == 0);
    auto const pixels = fixture.session->frame_pixels(capture.frame->id);
    REQUIRE(pixels);
    REQUIRE(pixels->rgba.size() == 10 * 16 * 4);
    // Verify channel order and grayscale independently of the PNG exporter.
    for (std::size_t index = 0; index < pixels->gray.size(); ++index)
    {
        auto const red = static_cast<std::uint8_t>((index * 31) & 0xff);
        auto const green = static_cast<std::uint8_t>(red ^ 0x5a);
        auto const blue = static_cast<std::uint8_t>(red ^ 0xa5);
        REQUIRE(pixels->rgba[index * 4] == red);
        REQUIRE(pixels->rgba[index * 4 + 1] == green);
        REQUIRE(pixels->rgba[index * 4 + 2] == blue);
        REQUIRE(pixels->rgba[index * 4 + 3] == 255);
        REQUIRE(pixels->gray[index] == (299u * red + 587u * green + 114u * blue) / 1000u);
    }
    auto const tap = fixture.session->tap(capture.frame->id, 540, 960, {});
    REQUIRE_FALSE(tap.failure);
    REQUIRE(tap.timings);
    CHECK(tap.timings->method == "adb");
    CHECK(tap.timings->adb_calls == 2);
    CHECK(tap.timings->geometry_ms > 0);
    CHECK(tap.timings->command_ms > 0);
    // Clicking must not trigger deferred PNG encoding.
    REQUIRE(fixture.session->frame_metadata(capture.frame->id));
    CHECK(fixture.session->frame_metadata(capture.frame->id)->byte_count == 0);

    auto const pngSize = fixture.session->frame_png_size(capture.frame->id);
    REQUIRE(pngSize);
    std::array<std::uint8_t, 1> tooSmall{42};
    CHECK_FALSE(fixture.session->copy_frame_png(capture.frame->id, tooSmall.data(), tooSmall.size()));
    CHECK(tooSmall[0] == 42);
    std::vector<std::uint8_t> png(static_cast<std::size_t>(*pngSize));
    REQUIRE(fixture.session->copy_frame_png(capture.frame->id, png.data(), png.size()));
    CHECK(Resources::WicImageDecoder::decode_bytes(png).rgba == pixels->rgba);
    CHECK(fixture.session->frame_png_size(capture.frame->id) == pngSize);
    REQUIRE(fixture.session->release_frame(capture.frame->id));
}

TEST_CASE("Pixel and PNG captures benchmark and cache their own output paths", "[context][capture][timing]")
{
    Fixture fixture;
    fixture.environment.Set(L"UMA_FAKE_ADB_CAPTURE_MODE", L"raw");
    // Clear method choices populated by the fixture's initial PNG capture.
    DeviceSessionOptions options;
    options.screenshot_method = DeviceSessionOptions::ScreenshotMethod::raw;
    REQUIRE_FALSE(fixture.session->update_options(options));
    options.screenshot_method = DeviceSessionOptions::ScreenshotMethod::automatic;
    REQUIRE_FALSE(fixture.session->update_options(options));
    for (bool png : {false, true})
    {
        CAPTURE(png);
        for (int pass = 0; pass < 2; ++pass)
        {
            auto const capture = png ? fixture.session->capture({}) : fixture.session->capture_pixels({});
            REQUIRE_FALSE(capture.failure);
            REQUIRE(capture.frame);
            REQUIRE(capture.timings);
            CHECK(capture.timings->method == "exec_raw");
            CHECK(capture.timings->capture_attempts == (pass == 0 ? 4u : 1u));
            CHECK(capture.timings->png_deferred == !png);
            CHECK((capture.frame->byte_count > 0) == png);
            REQUIRE(fixture.session->release_frame(capture.frame->id));
        }
    }
}

TEST_CASE("Pixel captures reject invalid raw payloads without producing a usable frame", "[context][capture]")
{
    Fixture fixture;
    DeviceSessionOptions options;
    std::wstring mode;
    SECTION("truncated raw") { options.screenshot_method = DeviceSessionOptions::ScreenshotMethod::raw; mode = L"raw-truncated"; }
    SECTION("truncated gzip") { options.screenshot_method = DeviceSessionOptions::ScreenshotMethod::gzip; mode = L"gzip-truncated"; }
    SECTION("corrupt gzip") { options.screenshot_method = DeviceSessionOptions::ScreenshotMethod::gzip; mode = L"gzip-corrupt"; }
    REQUIRE_FALSE(fixture.session->update_options(options));
    fixture.environment.Set(L"UMA_FAKE_ADB_CAPTURE_MODE", mode);
    auto const capture = fixture.session->capture_pixels({});
    REQUIRE(capture.failure);
    CHECK_FALSE(capture.frame);
    REQUIRE(capture.timings);
    CHECK(capture.timings->capture_attempts == 1);
}

TEST_CASE("Canceled pixel capture sends no device commands", "[context][capture][cancel]")
{
    Fixture fixture;
    std::stop_source cancellation;
    cancellation.request_stop();
    auto const capture = fixture.session->capture_pixels(cancellation.get_token());
    REQUIRE(capture.failure);
    CHECK(capture.failure->error_code == static_cast<int>(ConnectionErrorCode::Canceled));
    CHECK_FALSE(capture.frame);
    REQUIRE(capture.timings);
    CHECK(capture.timings->adb_calls == 0);
}

TEST_CASE("Native geometry batching retains both checks and falls back to original queries", "[phase3][context][capture]")
{
    Fixture fixture;
    DeviceSessionOptions options;
    options.screenshot_method = DeviceSessionOptions::ScreenshotMethod::png;
    REQUIRE_FALSE(fixture.session->update_options(options));
    std::string mode;
    SECTION("standard queries") { mode = ""; }
    SECTION("unsupported shell") { mode = "unsupported"; }
    SECTION("malformed batch output") { mode = "malformed"; }
    fixture.environment.Set(L"UMA_FAKE_ADB_GEOMETRY_BATCH_MODE", std::wstring(mode.begin(), mode.end()));
    { std::ofstream clear(fixture.commandLog, std::ios::trunc); }
    REQUIRE(fixture.context->CaptureScreen());
    REQUIRE(fixture.context->CaptureScreen());
    std::ifstream log(fixture.commandLog);
    int commands = 0;
    int batches = 0;
    for (std::string line; std::getline(log, line);)
    {
        ++commands;
        if (line.find("__UMA_GEOMETRY_ORIENTATION__") != std::string::npos) ++batches;
    }
    CHECK(commands == (mode.empty() ? 6 : 11));
    CHECK(batches == (mode.empty() ? 4 : 1));
    auto const match = fixture.MatchAt(180, 320);
    fixture.environment.Set(L"UMA_FAKE_ADB_ORIENTATION", L"2");
    CHECK_FALSE(fixture.context->TapMatch(match.CenterX(), match.CenterY(), match));
    CHECK(fixture.Taps().empty());
}

TEST_CASE("Native custom geometry queries keep their authored arguments", "[phase3][context][capture]")
{
    Fixture fixture;
    auto const profilePath = fixture.directory / "custom-connection.json";
    {
        std::ofstream profile(profilePath);
        profile << R"({"connection":[{"configName":"Custom","commands":{"get_size":["-s","[AdbSerial]","shell","wm","size","--custom-size"]}}]})";
    }
    auto profile = std::make_shared<ConnectionProfile const>(ConnectionProfile::load(profilePath));
    DeviceSessionOptions options;
    options.screenshot_method = DeviceSessionOptions::ScreenshotMethod::png;
    DeviceSession session(profile, fs::path(UMA_FAKE_ADB_PATH), "Custom",
        ConnectedDevice{"emulator-5554", "test", "14", 450, 800, 450, 800}, 78, options);
    { std::ofstream clear(fixture.commandLog, std::ios::trunc); }
    auto const capture = session.capture({});
    REQUIRE_FALSE(capture.failure);
    REQUIRE(capture.frame);
    std::ifstream input(fixture.commandLog);
    std::stringstream contents;
    contents << input.rdbuf();
    CHECK(contents.str().find("__UMA_GEOMETRY_ORIENTATION__") == std::string::npos);
    CHECK(contents.str().find("\twm\tsize\t--custom-size") != std::string::npos);
}

TEST_CASE("Native task recognition taps keep the source pixel point at every orientation", "[phase3][context]")
{
    for (int orientation = 0; orientation < 4; ++orientation)
    {
        CAPTURE(orientation);
        auto const width = orientation % 2 == 0 ? 450 : 800;
        auto const height = orientation % 2 == 0 ? 800 : 450;
        Fixture fixture(width, height, orientation);
        auto const match = fixture.MatchAt(180, 320);
        REQUIRE(fixture.context->TapMatch(match.CenterX(), match.CenterY(), match));
        auto const taps = fixture.Taps();
        REQUIRE(taps.size() == 1);
        CHECK(std::abs(taps[0].first - width / 5) <= 1);
        CHECK(std::abs(taps[0].second - height / 5) <= 1);
    }
}

TEST_CASE("Native inspection captures preserve selected evidence and cannot authorize input", "[phase3][context][inspection]")
{
    Fixture fixture;
    auto const selected = fixture.context->GetCurrentFrame();
    auto const original = fixture.MatchAt(180, 320);
    auto const inspected = fixture.context->CaptureInspectionFrame();
    REQUIRE(inspected);
    CHECK(inspected->frameId != selected->frameId);
    CHECK(inspected->grayPixels == selected->grayPixels);
    CHECK(fixture.context->GetCurrentFrame() == selected);
    CHECK_FALSE(fixture.session->frame_metadata(inspected->frameId));
    auto rejected = original;
    rejected.frameId = inspected->frameId;
    CHECK_FALSE(fixture.context->TapMatch(rejected.CenterX(), rejected.CenterY(), rejected));
    REQUIRE(fixture.context->TapMatch(original.CenterX(), original.CenterY(), original));
    CHECK(fixture.Taps().size() == 1);

    REQUIRE(fixture.context->CaptureScreen());
    auto const retained = fixture.MatchAt(180, 320);
    REQUIRE(fixture.context->RetainMatchForRetry(retained, 1, 30000));
    REQUIRE(fixture.context->CaptureInspectionFrame());
    CHECK(fixture.context->IsMatchRetained(retained));
    REQUIRE(fixture.context->TapMatch(retained.CenterX(), retained.CenterY(), retained));
    CHECK(fixture.Taps().size() == 2);
}

TEST_CASE("Native task input consumes ordinary screenshot evidence", "[phase3][context]")
{
    Fixture fixture;
    auto const match = fixture.MatchAt(180, 320);
    REQUIRE(fixture.context->TapMatch(match.CenterX(), match.CenterY(), match));
    CHECK_FALSE(fixture.context->TapMatch(match.CenterX(), match.CenterY(), match));
    REQUIRE(fixture.context->CaptureScreen());
    CHECK_FALSE(fixture.context->TapMatch(match.CenterX(), match.CenterY(), match));
    CHECK(fixture.Taps().size() == 1);
}

TEST_CASE("Native held input rejects stale evidence and releases after cancellation", "[phase4][context][touch]")
{
    std::stop_source cancellation;
    Fixture fixture(450, 800, 0, cancellation.get_token());
    auto const old = fixture.MatchAt(180, 320);
    REQUIRE(fixture.context->CaptureScreen());
    CHECK_FALSE(fixture.context->TouchDownMatch(old.CenterX(), old.CenterY(), old));
    auto current = fixture.MatchAt(180, 320);
    auto invalid = current;
    ++invalid.sessionGeneration;
    CHECK_FALSE(fixture.context->TouchDownMatch(invalid.CenterX(), invalid.CenterY(), invalid));
    ++invalid.geometryGeneration;
    invalid.sessionGeneration = current.sessionGeneration;
    CHECK_FALSE(fixture.context->TouchDownMatch(invalid.CenterX(), invalid.CenterY(), invalid));
    CHECK(fixture.Commands().find("3 57 123") == std::string::npos);
    REQUIRE(fixture.context->PrepareHeldTouch());
    REQUIRE(fixture.context->TouchDownMatch(current.CenterX(), current.CenterY(), current));
    CHECK_FALSE(fixture.context->Tap(90, 160));
    CHECK_FALSE(fixture.context->TouchDownMatch(current.CenterX(), current.CenterY(), current));
    cancellation.request_stop();
    REQUIRE(fixture.context->TouchCancel());
    CHECK_FALSE(fixture.context->TouchUp());
    auto const commands = fixture.Commands();
    CHECK(commands.find("3 57 123") != std::string::npos);
    CHECK(commands.find("3 57 -1") != std::string::npos);
}

TEST_CASE("Native held input keeps its frame until explicit or destructor release", "[phase4][context][touch]")
{
    SECTION("capture while held and explicit release")
    {
        Fixture fixture;
        auto const source = fixture.MatchAt(180, 320);
        REQUIRE(fixture.context->PrepareHeldTouch());
        REQUIRE(fixture.context->TouchDownMatch(source.CenterX(), source.CenterY(), source));
        REQUIRE(fixture.context->CaptureScreen());
        REQUIRE(fixture.context->TouchUp());
        CHECK(fixture.Commands().find("3 57 -1") != std::string::npos);
    }
    SECTION("destructor cancels the press")
    {
        Fixture fixture;
        auto const source = fixture.MatchAt(180, 320);
        REQUIRE(fixture.context->PrepareHeldTouch());
        REQUIRE(fixture.context->TouchDownMatch(source.CenterX(), source.CenterY(), source));
        fixture.context.reset();
        CHECK(fixture.Commands().find("3 57 -1") != std::string::npos);
    }
}

TEST_CASE("Native task retained retries bind one point and enforce their tap budget", "[phase3][context]")
{
    Fixture fixture;
    auto const match = fixture.MatchAt(180, 320);
    REQUIRE(fixture.context->RetainMatchForRetry(match, 2, 30000));
    CHECK_FALSE(fixture.context->TapMatch(match.CenterX() + 5, match.CenterY(), match));
    REQUIRE(fixture.context->TapMatch(match.CenterX(), match.CenterY(), match));
    REQUIRE(fixture.context->CaptureScreen());
    REQUIRE(fixture.context->TapMatch(match.CenterX(), match.CenterY(), match));
    CHECK_FALSE(fixture.context->TapMatch(match.CenterX(), match.CenterY(), match));
    CHECK(fixture.Taps().size() == 2);
}

TEST_CASE("Native task retained retry expiry and geometry changes reject input", "[phase3][context]")
{
    SECTION("finite expiry")
    {
        Fixture fixture;
        auto const match = fixture.MatchAt(180, 320);
        REQUIRE(fixture.context->RetainMatchForRetry(match, 2, 1));
        std::this_thread::sleep_for(std::chrono::milliseconds(3));
        CHECK_FALSE(fixture.context->RetainMatchForRetry(match, 2, 30000));
        CHECK_FALSE(fixture.context->TapMatch(match.CenterX(), match.CenterY(), match));
        CHECK(fixture.Taps().empty());
    }
    SECTION("source geometry changed")
    {
        Fixture fixture;
        auto const match = fixture.MatchAt(180, 320);
        REQUIRE(fixture.context->RetainMatchForRetry(match, 2, 30000));
        fixture.environment.Set(L"UMA_FAKE_ADB_ORIENTATION", L"2");
        CHECK_FALSE(fixture.context->TapMatch(match.CenterX(), match.CenterY(), match));
        CHECK(fixture.Taps().empty());
    }
}

TEST_CASE("Native task retry evidence cannot renew its budget or bind another point", "[phase3][context]")
{
    Fixture fixture;
    auto const match = fixture.MatchAt(180, 320);
    REQUIRE(fixture.context->RetainMatchForRetry(match, 2, 30000));
    CHECK_FALSE(fixture.context->RetainMatchForRetry(
        match, 2, 30000, match.CenterX() + 5, match.CenterY()));
    REQUIRE(fixture.context->TapMatch(match.CenterX(), match.CenterY(), match));
    REQUIRE(fixture.context->CaptureScreen());
    REQUIRE(fixture.context->RetainMatchForRetry(match, 2, 30000));
    REQUIRE(fixture.context->TapMatch(match.CenterX(), match.CenterY(), match));
    CHECK_FALSE(fixture.context->RetainMatchForRetry(match, 2, 30000));
    CHECK_FALSE(fixture.context->TapMatch(match.CenterX(), match.CenterY(), match));
    CHECK(fixture.Taps().size() == 2);
}

TEST_CASE("Native task failed input still consumes its retained attempt", "[phase3][context]")
{
    Fixture fixture;
    auto const match = fixture.MatchAt(180, 320);
    REQUIRE(fixture.context->RetainMatchForRetry(match, 1, 30000));
    fixture.environment.Set(L"UMA_FAKE_ADB_INPUT_FAIL", L"1");
    CHECK_FALSE(fixture.context->TapMatch(match.CenterX(), match.CenterY(), match));
    CHECK(fixture.context->GetLastErrorCode() == static_cast<int>(ConnectionErrorCode::CommandFailed));
    fixture.environment.Set(L"UMA_FAKE_ADB_INPUT_FAIL", L"");
    CHECK_FALSE(fixture.context->TapMatch(match.CenterX(), match.CenterY(), match));
    CHECK(fixture.Taps().size() == 1);
}

TEST_CASE("Native HSV retries create a fresh intent after reidentifying their target", "[phase3][context]")
{
    Fixture fixture;
    auto definition = HachimiPipelineLoader::LoadFromJsonString(R"({
        "schemaVersion":1,"start":"home","tasks":{"home":{
            "algorithm":"MatchTemplate","action":"ClickSelf","template":"frame.png",
            "templThreshold":0.99,"required":true,"timeoutMs":0,
            "clickVerification":{"preCheck":false,"settleDelayMs":0,"verifyTimeoutMs":0,
                "retryDelayMs":0,"maxRetries":1,"saturationMin":0.9,"minimumMatchRatio":0.5}
        }}
    })", Paths::ToUtf8(fixture.directory));
    REQUIRE(definition);

    auto const result = TaskGraphRunner{}.Run(*definition, "home", *fixture.context);

    CHECK(result.status == TaskGraphStatus::Failed);
    CHECK(result.errorMessage.find("HSV") != std::string::npos);
    CHECK(fixture.Taps().size() == 2);
    CHECK_FALSE(fixture.context->HasRetainedMatch());
}

TEST_CASE("Native task relative templates resolve UTF-8 definition directories", "[phase3][context]")
{
    Fixture fixture;
    auto const definitionDirectory = fixture.directory / fs::path(u8"自定义流程");
    fs::create_directories(definitionDirectory);
    auto const templatePath = definitionDirectory / fs::path(u8"识别.png");
    REQUIRE(WritePng(templatePath, 450, 800));
    fixture.context->SetTemplateBaseDirectory(Paths::ToUtf8(definitionDirectory));

    auto const match = fixture.context->MatchTemplate(Paths::ToUtf8(templatePath.filename()), 0.99);

    REQUIRE(match.found);
    CHECK(match.frameId == fixture.context->GetCurrentFrame()->frameId);
    CHECK(match.referenceWidth == 900);
    CHECK(match.referenceHeight == 1600);
}

TEST_CASE("Native fast scaled matching identifies only the configured card within its slot",
    "[phase3][context][SupportSelect]")
{
    Fixture fixture(900, 1600);
    auto const root = fs::path(CMAKE_SOURCE_DIR);
    fixture.environment.Set(L"UMA_FAKE_ADB_SCREENSHOT_PATH",
        (root / "testdata/hachimi/ura/captures/support_ready_mumu_20261012.png").wstring());
    REQUIRE(fixture.context->CaptureScreen());
    auto const cards = root / "resource/uma/assets/templates/global/support_cards";
    TaskRoi const firstSlot{55, 385, 255, 245};
    auto const correct = fixture.context->MatchTemplateAdvanced(
        Paths::ToUtf8(cards / "30028/card.png"), 0.80, firstSlot,
        "MatchTemplateScaledFast", 900, 1600, {1.775});
    REQUIRE(correct.found);
    CHECK(correct.frameId == fixture.context->GetCurrentFrame()->frameId);
    CHECK(correct.x >= firstSlot.x);
    CHECK(correct.x + correct.width <= firstSlot.x + firstSlot.width);
    CHECK_FALSE(fixture.context->MatchTemplateAdvanced(
        Paths::ToUtf8(cards / "20013/card.png"), 0.80, firstSlot,
        "MatchTemplateScaledFast", 900, 1600, {1.775}).found);
    CHECK_FALSE(fixture.context->MatchTemplateAdvanced(
        Paths::ToUtf8(cards / "30028/card.png"), 0.80, TaskRoi{325, 385, 255, 245},
        "MatchTemplateScaledFast", 900, 1600, {1.775}).found);
    CHECK(fixture.Taps().empty());
}
