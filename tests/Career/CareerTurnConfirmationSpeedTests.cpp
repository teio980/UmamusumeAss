#include <catch2/catch_test_macros.hpp>
#include <catch2/generators/catch_generators.hpp>

#include "UmaAssistantCore/Career/CareerActionExecutor.hpp"
#include "UmaAssistantCore/Career/CareerFlowCoordinator.hpp"
#include "UmaAssistantCore/Career/CareerObservationReader.hpp"
#include "UmaAssistantCore/CareerDomain/Scenarios/Ura/UraScenarioModule.hpp"
#include "UmaAssistantCore/CareerDomain/TrainingStrategies.hpp"
#include "UmaAssistantCore/Common/PathEncoding.hpp"
#include "MockCareerComponents.hpp"
#include "tests/TaskEngine/MockTaskExecutionContext.hpp"

#include <algorithm>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <memory>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

namespace
{
using namespace UmaAssistant::Career;
using namespace UmaAssistant::CareerDomain;
using namespace UmaAssistant::TaskEngine;
using UmaAssistant::TaskEngine::Tests::MockTaskExecutionContext;

std::shared_ptr<CareerVisualResources> TurnResources()
{
    static const auto resources = [] {
        CareerRuntimeConfig config;
        config.resourceDirectory = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR) / "resource";
        return std::make_shared<CareerVisualResources>(config);
    }();
    return resources;
}

class TurnConfirmationContext final : public MockTaskExecutionContext
{
public:
    bool CaptureScreen() override
    {
        if (!MockTaskExecutionContext::CaptureScreen()) return false;
        auto next = std::make_shared<TaskFrameSnapshot>();
        next->width = 900;
        next->height = 1600;
        next->frameId = static_cast<std::uint64_t>(captureCount);
        next->sessionGeneration = 1;
        next->geometryGeneration = 1;
        if (changeGeometry && captureCount >= changedFrameStart) next->geometryGeneration = 2;
        if (changeSession && captureCount >= changedFrameStart) next->sessionGeneration = 2;
        if (changeOrientation && captureCount >= changedFrameStart) next->orientation = 1;
        if (repeatFrameId) next->frameId = 1;
        const auto current = screens.empty() ? std::string{} : screens[
            (std::min)(static_cast<std::size_t>(captureCount - 1), screens.size() - 1)];
        if (current == "event_choice")
        {
            next->rgbaPixels.resize(static_cast<std::size_t>(next->width) * next->height * 4);
            const auto pixel = (static_cast<std::size_t>(1040) * next->width + 580) * 4;
            next->rgbaPixels[pixel + 1] = next->rgbaPixels[pixel + 3] = 255;
        }
        frame = std::move(next);
        consumed = false;
        return true;
    }

    std::shared_ptr<const TaskFrameSnapshot> GetCurrentFrame() const override { return frame; }

    std::shared_ptr<const UmaAssistant::Vision::GrayImage> LoadTemplateImage(const std::string& path) override
    {
        if (std::filesystem::path(path).filename() != "event_effects_button.png") return {};
        return std::make_shared<UmaAssistant::Vision::GrayImage>(
            UmaAssistant::Vision::GrayImage{1, 1, {150}, {0, 255, 0, 255}});
    }

    void Sleep(int milliseconds) override
    {
        MockTaskExecutionContext::Sleep(milliseconds);
        if (++sleepCount == cancelAtSleep) cancelled = true;
    }

    TaskMatchResult MatchTemplateAdvanced(const std::string& path, double threshold,
        const std::optional<TaskRoi>&, const std::string&, int referenceWidth,
        int referenceHeight, const std::vector<double>&) override
    {
        const auto name = std::filesystem::path(path).filename().string();
        checkedTemplates.push_back(name);
        const auto current = screens.empty() ? std::string{} : screens[
            (std::min)(static_cast<std::size_t>(captureCount - 1), screens.size() - 1)];
        static const std::unordered_map<std::string, std::string> markers{
            {"career_main_header.png", "career_main"},
            {"career_main_action_training.png", "career_main"},
            {"career_main_action_rest.png", "career_main"},
            {"recreation_action.png", "career_main"},
            {"summer_rest_action.png", "career_main"},
            {"infirmary_available.png", "career_main"},
            {"training_selection_header.png", "training_selection"},
            {"wit.png", "training_selection"},
            {"event_effects_button.png", "event_choice"},
            {"career_final_confirmation_header.png", "career_final_confirmation"},
            {"fan_goal_race_list_header.png", "race_list"},
            {"ura_post_finish_loaded.png", "career_rank"},
            {"career_rank_career_next.png", "career_rank"},
            {"home_selected.png", "home"},
            {"home_home_career.png", "home"},
            {"ura_final_race_live.png", "race_live"},
            {"rest_confirmation_dialog.png", "rest_confirmation"},
            {"summer_rest_confirmation_title.png", "summer_rest_confirmation"},
            {"infirmary_confirmation_title.png", "infirmary_confirmation"},
            {"recreation_prompt.png", "recreation_confirmation"},
            {"recreation_selection_title.png", "recreation_selection"},
            {"recreation_trainee.png", "recreation_selection"},
            {"recreation_ok.png", "recreation_confirmation"},
            {"infirmary_confirmation_ok.png", "infirmary_confirmation"}};
        bool found = false;
        if (name == "rest_confirmation_rest_confirm.png")
            found = current == "rest_confirmation" || current == "summer_rest_confirmation";
        else if (const auto marker = markers.find(name); marker != markers.end())
            found = current == marker->second;
        const bool button = name == "rest_confirmation_rest_confirm.png"
            || name == "infirmary_confirmation_ok.png" || name == "recreation_ok.png";
        if (button && buttonNeedsFreshFrame && captureCount == 2) found = false;
        TaskMatchResult result{found, found ? (std::max)(threshold, .99) : 0.0, 500, 1000, 160, 80};
        result.referenceWidth = referenceWidth;
        result.referenceHeight = referenceHeight;
        if (frame)
        {
            result.frameId = frame->frameId;
            result.sessionGeneration = frame->sessionGeneration;
            result.geometryGeneration = frame->geometryGeneration;
        }
        return result;
    }

    bool TapMatch(int x, int y, const TaskMatchResult& match) override
    {
        if (!frame || consumed || !match.found || match.frameId != frame->frameId
            || match.sessionGeneration != frame->sessionGeneration
            || match.geometryGeneration != frame->geometryGeneration) return false;
        consumed = true;
        clickedFrames.push_back(frame->frameId);
        return MockTaskExecutionContext::Tap(x, y);
    }

    void EmitEvent(const TaskExecutionEvent& event) override
    {
        if (expireBeforeInput && event.type == TaskEventType::ActionExecuting)
        {
            expireBeforeInput = false;
            std::this_thread::sleep_for(std::chrono::milliseconds(1100));
        }
        MockTaskExecutionContext::EmitEvent(event);
    }

    std::vector<std::string> screens;
    std::shared_ptr<TaskFrameSnapshot> frame;
    std::vector<std::uint64_t> clickedFrames;
    std::vector<std::string> checkedTemplates;
    bool consumed{false};
    bool buttonNeedsFreshFrame{false};
    bool changeGeometry{false};
    bool changeSession{false};
    bool changeOrientation{false};
    bool repeatFrameId{false};
    bool expireBeforeInput{false};
    int changedFrameStart{2};
    int sleepCount{0};
    int cancelAtSleep{0};
};

ActionIntent TurnIntent(const std::string& screen)
{
    if (screen == "recreation_confirmation") return ActionIntent::MakeRecreation();
    if (screen == "infirmary_confirmation") return ActionIntent::MakeInfirmary();
    return ActionIntent::MakeRest();
}
}

TEST_CASE("Four turn confirmation dialogs rematch OK on their fresh observed frame",
    "[Career][TurnConfirmationSpeed]")
{
    const std::string screen = GENERATE("recreation_confirmation", "infirmary_confirmation",
        "rest_confirmation", "summer_rest_confirmation");
    CAPTURE(screen);
    const auto resources = TurnResources();
    REQUIRE(resources->IsReady());
    TurnConfirmationContext context;
    context.screens = {screen};
    CareerScreenObserver observer(resources);
    auto observation = observer.ObserveScreens(context, {screen});
    REQUIRE(observation.screenId == screen);
    REQUIRE(observation.stableFrameCount == 2);
    REQUIRE(observation.visualFrameEvidence);
    CHECK(context.captureCount == 2);
    std::uint64_t expectedFrame = 2;

    SECTION("valid observed frame avoids a third screenshot") {}
    SECTION("expired proof captures again")
    {
        observation.visualFrameEvidence->capturedAt -= std::chrono::seconds(2);
        expectedFrame = 3;
    }
    SECTION("replaced current frame captures again")
    {
        REQUIRE(context.CaptureScreen());
        expectedFrame = 4;
    }
    SECTION("single-frame proof does not authorize reuse")
    {
        observation.stableFrameCount = 1;
        expectedFrame = 3;
    }
    SECTION("missing OK on the observed frame retries a fresh capture")
    {
        context.buttonNeedsFreshFrame = true;
        expectedFrame = 3;
    }
    SECTION("changed geometry invalidates the proof")
    {
        context.frame->geometryGeneration = 2;
        expectedFrame = 3;
    }
    SECTION("changed session invalidates the proof")
    {
        context.frame->sessionGeneration = 2;
        expectedFrame = 3;
    }
    SECTION("changed orientation invalidates the proof")
    {
        context.frame->orientation = 1;
        expectedFrame = 3;
    }
    CareerActionExecutor executor(resources);
    REQUIRE(executor.ExecuteForObservation(TurnIntent(screen), observation, context)
        == ActionExecutionStatus::Success);
    CHECK(context.captureCount == static_cast<int>(expectedFrame));
    REQUIRE(context.clickedFrames.size() == 1);
    CHECK(context.clickedFrames.front() == expectedFrame);
    int const expectedSleep = context.buttonNeedsFreshFrame ? 670 : 420;
    CHECK(context.totalSleepMs == expectedSleep); // Independent proof gap plus 300 ms after OK (plus 250ms retry poll if fresh frame needed).
}

TEST_CASE("Turn dialogs retain the recognized second frame through opening animation",
    "[Career][TurnConfirmationSpeed]")
{
    const std::string screen = GENERATE("recreation_confirmation", "infirmary_confirmation",
        "rest_confirmation", "summer_rest_confirmation");
    TurnConfirmationContext context;
    context.screens = {"unknown", screen, screen};
    CareerScreenObserver observer(TurnResources());
    const auto observation = observer.ObserveScreens(context, {screen});
    REQUIRE(observation.screenId == screen);
    CHECK(observation.stableFrameCount == 2);
    REQUIRE(observation.visualFrameEvidence);
    CHECK(observation.visualFrameEvidence->frameId == 3);
    CHECK(context.captureCount == 3);
    CHECK(context.totalSleepMs == 240);
}

TEST_CASE("Settlement actions reuse confirmed frames with a 300 ms post-click delay",
    "[Career][CareerWait]")
{
    TurnConfirmationContext context;
    context.screens = {"career_rank"};
    CareerScreenObserver observer(TurnResources());
    auto observation = observer.ObserveScreens(context, {"career_rank"});
    REQUIRE(observation.screenId == "career_rank");
    REQUIRE(observation.visualFrameEvidence);
    REQUIRE(context.captureCount == 2);
    std::uint64_t expectedFrame = 2;
    SECTION("fresh confirmation requires no additional screenshot") {}
    SECTION("expired confirmation is captured again before input")
    {
        observation.visualFrameEvidence->capturedAt -= std::chrono::seconds(2);
        expectedFrame = 3;
    }
    SECTION("missing visual evidence requires a fresh capture")
    {
        observation.visualFrameEvidence.reset();
        expectedFrame = 3;
    }
    SECTION("geometry changed after confirmation")
    {
        context.frame->geometryGeneration = 2;
        expectedFrame = 3;
    }
    CareerActionExecutor executor(TurnResources());
    REQUIRE(executor.ExecuteForObservation(ActionIntent::MakeAdvance(), observation, context)
        == ActionExecutionStatus::Success);
    REQUIRE(context.clickedFrames.size() == 1);
    CHECK(context.clickedFrames.front() == expectedFrame);
    CHECK(context.captureCount == static_cast<int>(expectedFrame));
    CHECK(context.totalSleepMs == 420); // Independent proof gap plus 300 ms after the click.
}

TEST_CASE("Turn dialog confirmation rejects non-independent or changed frames",
    "[Career][TurnConfirmationSpeed]")
{
    TurnConfirmationContext context;
    context.screens = {"rest_confirmation"};
    SECTION("geometry changed") { context.changeGeometry = true; }
    SECTION("session changed") { context.changeSession = true; }
    SECTION("orientation changed") { context.changeOrientation = true; }
    SECTION("same frame ID was returned twice") { context.repeatFrameId = true; }
    CareerScreenObserver observer(TurnResources());
    const auto observation = observer.ObserveScreens(context, {"rest_confirmation"});
    CHECK(observation.screenId == "unknown");
    CHECK_FALSE(observation.visualFrameEvidence);
    CHECK(context.clickedFrames.empty());
}

TEST_CASE("Main return retains its first recognized frame for consecutive confirmation",
    "[Career][TurnConfirmationSpeed][MainReturnSpeed]")
{
    TurnConfirmationContext context;
    int expectedCaptures = 3;
    SECTION("newly visible Main") { context.screens = {"unknown", "career_main", "career_main"}; }
    SECTION("already stable Main")
    {
        context.screens = {"career_main"};
        expectedCaptures = 2;
    }
    CareerScreenObserver observer(TurnResources());
    const auto observation = observer.ObserveScreens(context, {"career_main"});
    REQUIRE(observation.screenId == "career_main");
    CHECK(observation.stableFrameCount == 2);
    REQUIRE(observation.visualFrameEvidence);
    CHECK(observation.visualFrameEvidence->frameId == static_cast<std::uint64_t>(expectedCaptures));
    CHECK(context.captureCount == expectedCaptures);
    CHECK(context.totalSleepMs == 120 * (expectedCaptures - 1));
    CHECK(std::none_of(context.logs.begin(), context.logs.end(), [](const auto& log) {
        return log.find("two-frame confirmation window") != std::string::npos;
    }));
    CareerActionExecutor executor(TurnResources());
    REQUIRE(executor.ExecuteForObservation(ActionIntent::MakeRest(), observation, context)
        == ActionExecutionStatus::Success);
    CHECK(context.captureCount == expectedCaptures);
    REQUIRE(context.clickedFrames.size() == 1);
    CHECK(context.clickedFrames.front() == static_cast<std::uint64_t>(expectedCaptures));
}

TEST_CASE("Transient Main followed by an event or dialog remains unconfirmed",
    "[Career][TurnConfirmationSpeed][MainReturnSpeed]")
{
    const std::string destination = GENERATE("unknown", "event_choice", "rest_confirmation");
    CAPTURE(destination);
    TurnConfirmationContext context;
    context.screens = {"unknown", "career_main", destination};
    CareerScreenObserver observer(TurnResources());
    const auto observation = observer.ObserveScreens(context, {"career_main", "event_choice", "rest_confirmation"});
    CHECK(observation.screenId == "unknown");
    CHECK_FALSE(observation.visualFrameEvidence);
    CHECK(context.captureCount == 3);
    CHECK(context.clickedFrames.empty());
}

TEST_CASE("Main return requires independent frames with unchanged geometry and session",
    "[Career][TurnConfirmationSpeed][MainReturnSpeed]")
{
    TurnConfirmationContext context;
    context.screens = {"unknown", "career_main", "career_main"};
    context.changedFrameStart = 3;
    SECTION("geometry changed") { context.changeGeometry = true; }
    SECTION("session changed") { context.changeSession = true; }
    SECTION("orientation changed") { context.changeOrientation = true; }
    SECTION("same frame ID was returned twice") { context.repeatFrameId = true; }
    CareerScreenObserver observer(TurnResources());
    const auto observation = observer.ObserveScreens(context, {"career_main"});
    CHECK(observation.screenId == "unknown");
    CHECK_FALSE(observation.visualFrameEvidence);
    CHECK(context.captureCount == 3);
    CHECK(context.clickedFrames.empty());
}

TEST_CASE("Main return stops when the extra confirmation capture fails or is cancelled",
    "[Career][TurnConfirmationSpeed][MainReturnSpeed]")
{
    TurnConfirmationContext context;
    context.screens = {"unknown", "career_main", "career_main"};
    std::string expectedScreen = "capture_failed";
    int expectedCaptures = 3;
    SECTION("extra capture fails") { context.captureResults = {true, true, false}; }
    SECTION("cancel during the extra proof gap")
    {
        context.cancelAtSleep = 2;
        expectedScreen = "capture_cancelled";
        expectedCaptures = 2;
    }
    CareerScreenObserver observer(TurnResources());
    const auto observation = observer.ObserveScreens(context, {"career_main"});
    CHECK(observation.screenId == expectedScreen);
    CHECK(context.captureCount == expectedCaptures);
    CHECK(context.clickedFrames.empty());
}

TEST_CASE("Every Career page confirms by retaining its first recognized frame",
    "[Career][RollingConfirmation]")
{
    const std::string screen = GENERATE("career_final_confirmation", "home", "career_main",
        "training_selection", "event_choice", "race_list", "race_live", "career_rank",
        "rest_confirmation", "summer_rest_confirmation", "recreation_selection",
        "recreation_confirmation", "infirmary_confirmation");
    CAPTURE(screen);
    TurnConfirmationContext context;
    context.screens = {"unknown", screen, screen};
    CareerScreenObserver observer(TurnResources());
    const auto observation = observer.ObserveScreens(context, {screen});
    REQUIRE(observation.screenId == screen);
    CHECK(observation.stableFrameCount == 2);
    REQUIRE(observation.visualFrameEvidence);
    CHECK(observation.visualFrameEvidence->frameId == 3);
    CHECK(context.captureCount == 3);
    CHECK(context.totalSleepMs == 240);
    CHECK(context.clickedFrames.empty());
}

TEST_CASE("Career confirmation carries its latest frame across observation retries",
    "[Career][RollingConfirmation]")
{
    TurnConfirmationContext context;
    context.screens = {"unknown", "event_choice", "race_list", "race_list"};
    CareerScreenObserver observer(TurnResources());
    const std::vector<std::string> candidates{"event_choice", "race_list"};
    const auto changing = observer.ObserveScreens(context, candidates);
    CHECK(changing.screenId == "unknown");
    REQUIRE(context.captureCount == 3);
    CHECK(context.clickedFrames.empty());
    const auto confirmed = observer.ObserveScreens(context, candidates);
    REQUIRE(confirmed.screenId == "race_list");
    REQUIRE(confirmed.visualFrameEvidence);
    CHECK(confirmed.visualFrameEvidence->frameId == 4);
    CHECK(context.captureCount == 4);
    CHECK(context.totalSleepMs == 360);
}

TEST_CASE("An unknown frame remains the next comparison's starting point",
    "[Career][RollingConfirmation]")
{
    TurnConfirmationContext context;
    context.screens = {"unknown", "training_selection", "unknown", "training_selection", "training_selection"};
    CareerScreenObserver observer(TurnResources());
    CHECK(observer.ObserveScreens(context, {"training_selection"}).screenId == "unknown");
    REQUIRE(context.captureCount == 3);
    const auto confirmed = observer.ObserveScreens(context, {"training_selection"});
    REQUIRE(confirmed.screenId == "training_selection");
    CHECK(context.captureCount == 5);
    CHECK(context.totalSleepMs == 480);
}

TEST_CASE("Pending Career frames cannot survive reset or replacement of the current frame",
    "[Career][RollingConfirmation]")
{
    TurnConfirmationContext context;
    context.screens = {"unknown", "event_choice", "race_list", "race_list"};
    CareerScreenObserver observer(TurnResources());
    const std::vector<std::string> candidates{"event_choice", "race_list"};
    CHECK(observer.ObserveScreens(context, candidates).screenId == "unknown");
    REQUIRE(context.captureCount == 3);
    int expectedCaptures = 5;
    SECTION("entry reset") { observer.ResetForEntry(); }
    SECTION("recovery reset") { observer.ResetForRecovery(); }
    SECTION("remembering a source before an action clears the pending frame")
    {
        CareerObservation source;
        source.screenId = "event_choice";
        observer.RememberObservation(source);
    }
    SECTION("a different frame became current")
    {
        REQUIRE(context.CaptureScreen());
        expectedCaptures = 6;
    }
    const auto confirmed = observer.ObserveScreens(context, candidates);
    REQUIRE(confirmed.screenId == "race_list");
    CHECK(context.captureCount == expectedCaptures);
}

TEST_CASE("A carried Career frame is recognized again under the current candidates",
    "[Career][RollingConfirmation]")
{
    TurnConfirmationContext context;
    context.screens = {"unknown", "event_choice", "race_list", "race_list"};
    CareerScreenObserver observer(TurnResources());
    CHECK(observer.ObserveScreens(context, {"event_choice", "race_list"}).screenId == "unknown");
    REQUIRE(context.captureCount == 3);
    CHECK(observer.ObserveScreens(context, {"event_choice"}).screenId == "unknown");
    CHECK(context.captureCount == 4);
    CHECK(context.clickedFrames.empty());
}

TEST_CASE("Turn dialog cancellation and input lease expiry do not permit stale taps",
    "[Career][TurnConfirmationSpeed]")
{
    TurnConfirmationContext context;
    context.screens = {"rest_confirmation"};
    CareerScreenObserver observer(TurnResources());
    const auto observation = observer.ObserveScreens(context, {"rest_confirmation"});
    REQUIRE(observation.visualFrameEvidence);
    CareerActionExecutor executor(TurnResources());
    SECTION("cancel after observation")
    {
        context.cancelled = true;
        CHECK(executor.ExecuteForObservation(ActionIntent::MakeRest(), observation, context)
            == ActionExecutionStatus::Cancelled);
        CHECK(context.clickedFrames.empty());
        CHECK(context.captureCount == 2);
    }
    SECTION("lease expires in the action callback")
    {
        context.expireBeforeInput = true;
        REQUIRE(executor.ExecuteForObservation(ActionIntent::MakeRest(), observation, context)
            == ActionExecutionStatus::Success);
        REQUIRE(context.clickedFrames.size() == 1);
        CHECK(context.clickedFrames.front() == 3);
        CHECK(context.captureCount == 3);
    }
}

TEST_CASE("Recreation selection uses the same guarded fast path before confirming the outing",
    "[Career][TurnConfirmationSpeed]")
{
    TurnConfirmationContext context;
    CareerScreenObserver observer(TurnResources());
    SECTION("an opening selection page retains its first recognized sample")
    {
        context.screens = {"unknown", "recreation_selection", "recreation_selection"};
        const auto observation = observer.ObserveScreens(context, {"recreation_selection"});
        CHECK(observation.screenId == "recreation_selection");
        CHECK(context.captureCount == 3);
        CHECK(observation.visualFrameEvidence);
    }
    SECTION("a stable selection page reuses its current frame to select the trainee")
    {
        context.screens = {"recreation_selection"};
        auto observation = observer.ObserveScreens(context, {"recreation_selection"});
        REQUIRE(observation.screenId == "recreation_selection");
        REQUIRE(observation.visualFrameEvidence);
        CareerActionExecutor executor(TurnResources());
        REQUIRE(executor.ExecuteForObservation(ActionIntent::MakeRecreation(), observation, context)
            == ActionExecutionStatus::Success);
        CHECK(context.captureCount == 2);
        REQUIRE(context.clickedFrames.size() == 1);
        CHECK(context.clickedFrames.front() == 2);
        CHECK(context.totalSleepMs == 120);
    }
}

TEST_CASE("Opening the four turn dialogs avoids duplicate Main capture and fixed opening delay",
    "[Career][TurnConfirmationSpeed]")
{
    const std::string screen = GENERATE("recreation_confirmation", "infirmary_confirmation",
        "rest_confirmation", "summer_rest_confirmation");
    TurnConfirmationContext context;
    context.screens = {"career_main", "career_main", screen, screen};
    CareerScreenObserver observer(TurnResources());
    auto main = observer.ObserveScreens(context, {"career_main"});
    REQUIRE(main.screenId == "career_main");
    REQUIRE(main.visualFrameEvidence);
    if (screen == "summer_rest_confirmation") main.turnPositionText = "Classic Year Early July";
    const auto intent = TurnIntent(screen);
    CareerActionExecutor executor(TurnResources());
    REQUIRE(executor.ExecuteForObservation(intent, main, context) == ActionExecutionStatus::Success);
    CHECK(context.captureCount == 2);
    CHECK(context.totalSleepMs == 120);
    REQUIRE(context.clickedFrames.size() == 1);
    CHECK(context.clickedFrames.front() == 2);
    const auto beforeDialog = context.checkedTemplates.size();
    REQUIRE(executor.ConfirmCompletionFromObservation(intent, main, context));
    CHECK(context.captureCount == 4);
    CHECK(context.totalSleepMs == 240);
    CHECK(std::find(context.checkedTemplates.begin() + static_cast<std::ptrdiff_t>(beforeDialog),
        context.checkedTemplates.end(), "career_main_header.png") == context.checkedTemplates.end());
    const auto dialog = executor.TakeConfirmedObservation();
    REQUIRE(dialog);
    REQUIRE(dialog->screenId == screen);
    REQUIRE(executor.ExecuteForObservation(intent, *dialog, context) == ActionExecutionStatus::Success);
    CHECK(context.captureCount == 4);
    REQUIRE(context.clickedFrames.size() == 2);
    CHECK(context.clickedFrames.back() == 4);
    CHECK(context.totalSleepMs == 540); // Two independent proof gaps plus 300 ms after OK.
}

TEST_CASE("Main source proof does not enable shortcuts for training",
    "[Career][TurnConfirmationSpeed]")
{
    TurnConfirmationContext context;
    context.screens = {"career_main", "career_main", "career_main", "training_selection"};
    CareerScreenObserver observer(TurnResources());
    const auto main = observer.ObserveScreens(context, {"career_main"});
    REQUIRE(main.visualFrameEvidence);
    CareerActionExecutor executor(TurnResources());
    REQUIRE(executor.ExecuteForObservation(ActionIntent::MakeTraining("speed"), main, context)
        == ActionExecutionStatus::Success);
    REQUIRE(context.clickedFrames.size() == 1);
    CHECK(context.clickedFrames.front() == 3);
    CHECK(std::none_of(context.logs.begin(), context.logs.end(), [](const auto& log) {
        return log.find("Turn action timing:") != std::string::npos;
    }));
}

TEST_CASE("Missing turn entry markers request a fresh page observation without claiming success",
    "[Career][TurnConfirmationSpeed][CareerActionReobserve]")
{
    const std::string action = GENERATE("action.rest", "action.summer_rest", "action.infirmary", "action.recreation");
    CareerRuntimeConfig config;
    config.resourceDirectory = UmaAssistant::Paths::FromUtf8(CMAKE_SOURCE_DIR) / "resource";
    nlohmann::json execution;
    std::ifstream(config.resourceDirectory / "hachimi/career/turn/execution.json") >> execution;
    const auto* entry = TurnResources()->FindAction("career_main", action);
    REQUIRE(entry != nullptr);
    auto& task = execution["tasks"][entry->taskName];
    task["timeoutMs"] = 0;
    task["retryTimes"] = 0;
    auto resources = std::make_shared<CareerVisualResources>(config, ActionRegistry{},
        std::vector<CareerJsonOverride>{{UmaAssistant::Paths::ToUtf8(
            config.resourceDirectory / "hachimi/career/turn/execution.json"), execution.dump()}});
    REQUIRE(resources->IsReady());
    TurnConfirmationContext context;
    context.screens = {"event_choice"};
    CareerActionExecutor executor(resources);

    REQUIRE(executor.ExecuteAction("career_main", action, context) == ActionExecutionStatus::Reobserve);

    CHECK(context.clickedFrames.empty());
    CHECK(context.captureCount == 1);
    CHECK_FALSE(executor.TakeActionHandoff());
    CHECK(std::none_of(context.emittedEvents.begin(), context.emittedEvents.end(), [](const auto& event) {
        return event.type == TaskEventType::ActionExecuted && event.succeeded;
    }));
    CareerScreenObserver observer(resources);
    CHECK(observer.ObserveCareer(context).screenId == "event_choice");
}

TEST_CASE("A rejected turn entry tap fails instead of requesting an input retry",
    "[Career][TurnConfirmationSpeed][CareerActionReobserve]")
{
    TurnConfirmationContext context;
    context.screens = {"career_main"};
    context.tapResult = false;
    CareerActionExecutor executor(TurnResources());
    CHECK(executor.ExecuteAction("career_main", "action.rest", context) == ActionExecutionStatus::Failed);
    CHECK(context.clickedFrames.size() == 1);
    CHECK_FALSE(executor.TakeActionHandoff());
}

TEST_CASE("A delayed turn dialog still falls back to the original transition polling",
    "[Career][TurnConfirmationSpeed]")
{
    TurnConfirmationContext context;
    context.screens = {"career_main", "career_main", "career_main", "career_main",
        "rest_confirmation", "rest_confirmation"};
    CareerScreenObserver observer(TurnResources());
    const auto main = observer.ObserveScreens(context, {"career_main"});
    REQUIRE(main.visualFrameEvidence);
    CareerActionExecutor executor(TurnResources());
    const auto intent = ActionIntent::MakeRest();
    REQUIRE(executor.ExecuteForObservation(intent, main, context) == ActionExecutionStatus::Success);
    REQUIRE(executor.ConfirmCompletionFromObservation(intent, main, context));
    CHECK(context.captureCount == 6);
    REQUIRE(context.clickedFrames.size() == 1);
    CHECK(context.totalSleepMs == 730); // Main gap, dialog probe, two rolling comparisons, and 250ms retry.
}

TEST_CASE("Only a fresh proven Career page skips the next coordinator scheduling wait",
    "[Career][TurnConfirmationSpeed]")
{
    class DestinationExecutor final : public UmaAssistant::Career::Tests::MockCareerActionExecutor
    {
    public:
        std::optional<CareerObservation> TakeConfirmedObservation() override { return destination; }
        CareerObservation destination;
    };
    TurnConfirmationContext context;
    REQUIRE(context.CaptureScreen());
    auto executor = std::make_shared<DestinationExecutor>();
    executor->destination.screenId = "recreation_confirmation";
    executor->destination.screenKind = CareerScreenKind::Recreation;
    executor->destination.stableFrameCount = 2;
    executor->destination.visualFrameEvidence = CareerObservation::VisualFrameEvidence{
        1, 1, 1, 900, 1600, 0, std::chrono::steady_clock::now()};
    int expectedWait = 0;
    SECTION("fresh confirmed dialog") {}
    SECTION("a fresh confirmed settlement page")
    {
        executor->destination.screenId = "career_rank";
        executor->destination.screenKind = CareerScreenKind::Settlement;
    }
    SECTION("changed frame geometry retains the scheduling wait")
    {
        context.frame->geometryGeneration = 2;
        expectedWait = 250;
    }
    SECTION("a single-frame observation retains the scheduling wait")
    {
        executor->destination.stableFrameCount = 1;
        expectedWait = 250;
    }
    SECTION("expired dialog retains the scheduling wait")
    {
        executor->destination.visualFrameEvidence->capturedAt -= std::chrono::seconds(2);
        expectedWait = 250;
    }
    SECTION("missing proof retains the scheduling wait")
    {
        executor->destination.visualFrameEvidence.reset();
        expectedWait = 250;
    }
    CareerFlowCoordinator coordinator(std::make_shared<UmaAssistant::Career::Tests::MockCareerObserver>(),
        executor, std::make_shared<UmaAssistant::CareerDomain::UraScenarioModule>(),
        std::make_shared<DefaultTrainingStrategy>());
    CareerObservation selection;
    selection.screenId = "recreation_selection";
    selection.screenKind = CareerScreenKind::Recreation;
    coordinator.SetConfirmedObservation(selection);
    static_cast<void>(coordinator.RunToCompletion(context, 1));
    CHECK(executor->executedIntents.size() == 1);
    CHECK(context.totalSleepMs == expectedWait);
}
