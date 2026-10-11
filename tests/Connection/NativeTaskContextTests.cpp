#include <catch2/catch_test_macros.hpp>

#include "TaskEngine/NativeTaskExecutionContext.hpp"
#include "TaskEngine/HachimiPipelineLoader.hpp"
#include "TaskEngine/TaskGraphRunner.hpp"
#include "Common/PathEncoding.hpp"

#include <windows.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <atomic>
#include <chrono>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <map>
#include <sstream>
#include <thread>
#include <vector>

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

bool WritePng(fs::path const& path, UINT width, UINT height)
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
    return SUCCEEDED(frame->WritePixels(height, stride, static_cast<UINT>(pixels.size()), pixels.data()))
        && SUCCEEDED(frame->Commit()) && SUCCEEDED(encoder->Commit());
}

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
