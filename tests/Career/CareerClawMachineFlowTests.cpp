#include <catch2/catch_test_macros.hpp>
#include "UmaAssistantCore/Career/Flows/CareerClawMachineFlow.hpp"
#include "UmaAssistantCore/Common/PathEncoding.hpp"
#include "UmaAssistantCore/Resources/ResourceSnapshot.hpp"
#include "UmaAssistantCore/Resources/WicImageDecoder.hpp"
#include "tests/TaskEngine/MockTaskExecutionContext.hpp"
#include <fstream>
#include <stdexcept>

namespace
{
using namespace UmaAssistant;
using namespace Career;
using namespace TaskEngine;

std::filesystem::path Root() { return Paths::FromUtf8(CMAKE_SOURCE_DIR) / "resource"; }
std::filesystem::path Capture(const std::string& name)
{ return Root() / "hachimi" / "ura" / "screens" / "captures" / name; }

class ClawReplayContext : public Tests::MockTaskExecutionContext
{
public:
    explicit ClawReplayContext(int credits = 2) : initialCredits(credits), resources(Root()),
        ready(Resources::WicImageDecoder::decode_file(Capture("claw_machine_ready.png"))),
        result(Resources::WicImageDecoder::decode_file(Capture("claw_machine_result.png"))),
        home(Resources::WicImageDecoder::decode_file(Capture("recreation_main.png"))) {}
    bool CaptureScreen() override
    {
        ++captureCount;
        const auto& image = !taps.empty() ? home : upCount >= initialCredits ? result : ready;
        auto next = std::make_shared<TaskFrameSnapshot>();
        next->width = image.width; next->height = image.height;
        next->grayPixels = image.gray; next->rgbaPixels = image.rgba;
        next->frameId = static_cast<uint64_t>(captureCount);
        next->sessionGeneration = 1; next->geometryGeneration = 1;
        frame = next;
        return true;
    }
    std::shared_ptr<const TaskFrameSnapshot> GetCurrentFrame() const override { return frame; }
    std::shared_ptr<const Vision::GrayImage> LoadTemplateImage(const std::string& path) override
    { return resources.load_image(path); }
    std::vector<TaskOcrDetection> RecognizeImageText(const Vision::GrayImage& image, const std::string& language) override
    {
        CHECK(language == "en-US");
        CHECK_FALSE(image.rgba.empty());
        return {{"CREDIT " + std::to_string(initialCredits - upCount), 10, 10, 300, 40, 1.0}};
    }
    bool PrepareHeldTouch() override { ++prepareCount; return true; }
    bool TouchDownMatch(int x, int y, const TaskMatchResult& source) override
    {
        CHECK(source.frameId == frame->frameId);
        CHECK(source.referenceWidth == frame->width);
        CHECK(source.referenceHeight == frame->height);
        ++downCount; downPoint = {x, y}; active = true;
        return true;
    }
    bool TouchUp() override { ++upCount; active = false; return true; }
    bool TouchCancel() override { ++cancelCount; active = false; return true; }
    void Sleep(int duration) override
    {
        if (active)
        {
            holdDurations.push_back(duration);
            if (failHold) throw std::runtime_error("hold interrupted");
        }
        Tests::MockTaskExecutionContext::Sleep(duration);
    }
    int prepareCount{}, downCount{}, upCount{}, cancelCount{};
    bool active{}, failHold{};
    std::vector<int> holdDurations;
    std::pair<int, int> downPoint;
    int initialCredits;
private:
    Resources::ResourceSnapshot resources;
    Vision::GrayImage ready, result, home;
    std::shared_ptr<const TaskFrameSnapshot> frame;
};

std::shared_ptr<CareerVisualResources> LoadResources()
{
    CareerRuntimeConfig config;
    config.resourceDirectory = Root();
    config.manifestPath = Paths::ToUtf8(Root() / "hachimi" / "ura" / "manifest.json");
    auto resources = std::make_shared<CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    return resources;
}
}

TEST_CASE("Claw CREDIT parser uses exactly one label and aligned count", "[Career][Claw]")
{
    CHECK(CareerClawMachineFlow::ParseCredit({{"CREDIT 12", 10, 10, 300, 40, 1.0}}) == 12);
    const TaskOcrDetection label{"CREDIT", 10, 10, 100, 40, 1.0};
    const TaskOcrDetection count{"7", 200, 10, 50, 40, 1.0};
    const TaskOcrDetection prize{"PRIZES 2", 10, 100, 300, 40, 1.0};
    CHECK(CareerClawMachineFlow::ParseCredit({label, count, prize}) == 7);
    CHECK_FALSE(CareerClawMachineFlow::ParseCredit({prize}));
    CHECK_FALSE(CareerClawMachineFlow::ParseCredit({label, count, {"8", 260, 10, 50, 40, 1.0}}));
    CHECK_FALSE(CareerClawMachineFlow::ParseCredit({label, label, count}));
}

TEST_CASE("Claw vision locates controls and result buttons in packaged captures", "[Career][Claw][Replay]")
{
    auto resources = LoadResources();
    const auto settings = CareerClawMachineSettings::FromJson(resources->GetProfileOptions());
    Resources::ResourceSnapshot images(Root());
    const auto game = images.load_image(resources->FindScreen("claw_machine")->templates.front().path);
    const auto result = images.load_image(resources->FindScreen("claw_machine_result")->templates.front().path);
    REQUIRE(game); REQUIRE(result);
    const auto frame = Resources::WicImageDecoder::decode_file(Capture("claw_machine_ready.png"));
    const auto marker = CareerClawVision::MatchMarker(frame, *game, settings, .94);
    REQUIRE(marker.found);
    const auto control = CareerClawVision::FindControl(frame, marker, settings);
    REQUIRE(control);
    CHECK(control->x + control->width / 2 >= 440);
    CHECK(control->x + control->width / 2 <= 470);
    CHECK(control->y + control->height / 2 >= 1380);
    CHECK(control->y + control->height / 2 <= 1420);
    const auto resultFrame = Resources::WicImageDecoder::decode_file(Capture("claw_machine_result.png"));
    const auto resultMarker = CareerClawVision::MatchMarker(resultFrame, *result, settings, .92);
    REQUIRE(resultMarker.found);
    const auto buttons = CareerClawVision::FindResultButtons(resultFrame, resultMarker, settings);
    REQUIRE(buttons.size() == 1);
    CHECK(buttons.front().y + buttons.front().height / 2 >= 1460);
    CHECK(buttons.front().y + buttons.front().height / 2 <= 1490);
    const auto other = Resources::WicImageDecoder::decode_file(Capture("recreation_main.png"));
    CHECK_FALSE(CareerClawVision::MatchMarker(other, *game, settings, .94).found);
    CHECK_FALSE(CareerClawVision::MatchMarker(other, *result, settings, .92).found);
}

TEST_CASE("Claw flow finishes observed credits and confirms the result exit", "[Career][Claw][Replay]")
{
    ClawReplayContext context(2);
    const auto resources = LoadResources();
    const auto result = CareerClawMachineFlow(resources).Run(context);
    INFO(result.error);
    REQUIRE(result.status == ActionExecutionStatus::Success);
    CHECK(context.prepareCount == 1);
    CHECK(context.downCount == 2);
    CHECK(context.upCount == 2);
    CHECK(context.cancelCount == 0);
    CHECK(context.holdDurations == std::vector<int>{1000, 1000});
    CHECK(context.taps.size() == 1);
}

TEST_CASE("Claw interrupted hold releases touch on failure and cancellation", "[Career][Claw]")
{
    ClawReplayContext context;
    REQUIRE(context.CaptureScreen());
    const auto frame = context.GetCurrentFrame();
    TaskMatchResult match{true, 1, 400, 1300, 100, 200, frame->width, frame->height,
        frame->frameId, frame->sessionGeneration, frame->geometryGeneration};
    SECTION("hold failure") { context.failHold = true; }
    SECTION("cancel during hold") { context.cancelOnSleep = true; }
    const auto result = CareerClawMachineFlow::PressControl(context, match, 1000);
    CHECK(result.status != ActionExecutionStatus::Success);
    CHECK(context.downCount == 1);
    CHECK(context.upCount == 0);
    CHECK(context.cancelCount == 1);
    CHECK_FALSE(context.active);
}
