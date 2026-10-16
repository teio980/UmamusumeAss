#include "GameData/CareerSelectionCatalog.hpp"
#include "GameData/UmaGameDatabase.hpp"
#include "GameTasks/CareerEntryFlow.hpp"
#include "GameTasks/NativeIndependentTrainingFlow.hpp"
#include "Career/CareerVisualResources.hpp"
#include "Career/CareerObservationReader.hpp"
#include "Common/PathEncoding.hpp"
#include "Resources/WicImageDecoder.hpp"
#include "Vision/TemplateMatcher.hpp"
#include "TaskEngine/TaskContextInterruption.hpp"
#include "../TaskEngine/MockTaskExecutionContext.hpp"

#include <catch2/catch_test_macros.hpp>
#include <nlohmann/json.hpp>

#include <algorithm>
#include <array>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <functional>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <system_error>
#include <vector>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

namespace
{

using namespace UmaAssistant;
using namespace UmaAssistant::GameTasks;
using namespace UmaAssistant::TaskEngine;
using Json = nlohmann::json;

std::filesystem::path ResourceRoot()
{
    return std::filesystem::path(CMAKE_SOURCE_DIR) / "resource";
}

class TestDirectory final
{
public:
    TestDirectory()
    {
        auto const id = std::chrono::steady_clock::now().time_since_epoch().count();
        path = std::filesystem::temp_directory_path() / ("uma-career-replay-" + std::to_string(id));
        std::filesystem::create_directories(path);
    }
    ~TestDirectory()
    {
        std::error_code error;
        std::filesystem::remove_all(path, error);
    }
    std::filesystem::path path;
};

class ReplayObserver final : public Career::ICareerObserver
{
public:
    explicit ReplayObserver(std::string screen) : screenId(std::move(screen)) {}
    CareerDomain::CareerObservation Observe(ITaskExecutionContext&) override
    {
        ++observations;
        CareerDomain::CareerObservation value;
        value.screenId = nextScreen < sequence.size() ? sequence[nextScreen++] : screenId;
        value.score = 0.99;
          value.stableFrameCount = stableFrameCount;
        return value;
    }
    std::string screenId;
    int observations{0};
      int stableFrameCount{0};
    std::vector<std::string> sequence;
    std::size_t nextScreen{0};
};

class CareerReplayContext final : public Tests::MockTaskExecutionContext
{
public:
    std::string GetDeviceSerial() const override { return serial; }

    TaskMatchResult MatchTemplateAdvanced(
        const std::string& path,
        double,
        const std::optional<TaskRoi>& roi,
        const std::string&,
        int referenceWidth,
        int referenceHeight,
        const std::vector<double>&) override
    {
        templateRequests.push_back(path);
        if (legacyManualReplay && path.find("legacy_trainee_badge.png") != std::string::npos)
        {
            legacyBadgeRois.push_back(roi);
            return TaskMatchResult{};
        }
        if (path.find("skills_main_reset.png") != std::string::npos
            || path.find("lineup_open_down.png") != std::string::npos)
        {
            return TaskMatchResult{false, 0.0, 0, 0, 0, 0};
        }
        TaskMatchResult result{true, 0.99, 410, 420, 40, 30};
        result.referenceWidth = referenceWidth;
        result.referenceHeight = referenceHeight;
        return result;
    }

    TaskTemplateCandidatesResult MatchTemplateCandidates(
        const std::vector<std::string>& paths,
        double threshold,
        const std::optional<TaskRoi>& roi,
        const std::string& algorithm,
        int referenceWidth,
        int referenceHeight,
        const std::vector<double>& scales,
        double) override
    {
        TaskTemplateCandidatesResult result;
        if (paths.empty()) return result;
        auto match = MatchTemplateAdvanced(paths.front(), threshold, roi, algorithm,
            referenceWidth, referenceHeight, scales);
        if (match.found) result.candidates.push_back({paths.front(), std::move(match)});
        return result;
    }

    std::vector<TaskOcrDetection> RecognizeText(
        const std::string&, const std::optional<TaskRoi>&) override
    {
        std::vector<TaskOcrDetection> result;
        for (auto const& text : ocrTexts)
            result.push_back({text, 200, 300, 350, 36, 0.99});
        return result;
    }

    void EmitEvent(TaskExecutionEvent const& event) override
    {
        if (onEvent) onEvent(event);
        if (event.type == TaskEventType::TaskEntering)
        {
            enteredTasks.push_back(event.taskName);
            if (!interruptOnTask.empty() && event.taskName == interruptOnTask
                && ++interruptTaskOccurrences >= interruptAfterOccurrences)
            {
                interruptOnTask.clear();
                throw TaskContextInterruption("Replay interrupted by a date-change recovery", true);
            }
            if (!cancelOnTask.empty() && event.taskName == cancelOnTask)
                cancelled = true;
        }
        Tests::MockTaskExecutionContext::EmitEvent(event);
    }

    std::string serial{"replay-adb-serial"};
    std::string cancelOnTask;
    std::string interruptOnTask;
    int interruptAfterOccurrences{1};
    int interruptTaskOccurrences{0};
    std::vector<std::string> ocrTexts;
    std::vector<std::string> enteredTasks;
    std::vector<std::string> templateRequests;
    bool legacyManualReplay{false};
    std::vector<std::optional<TaskRoi>> legacyBadgeRois;
    std::function<void(TaskExecutionEvent const&)> onEvent;
};

class LegacyScreenshotReplayContext final : public Tests::MockTaskExecutionContext
{
public:
    bool CaptureScreen() override
    {
        const auto file = confirmation ? "legacy_auto_select_confirmation_mumu_20261012.png"
            : "legacy_select_mumu_20261012.png";
        image = Resources::WicImageDecoder::decode_file(
            std::filesystem::path(CMAKE_SOURCE_DIR) / "testdata/hachimi/ura/captures" / file);
        frame = std::make_shared<TaskFrameSnapshot>();
        frame->width = image.width;
        frame->height = image.height;
        frame->frameId = ++captureCount;
        frame->grayPixels = image.gray;
        frame->rgbaPixels = image.rgba;
        return true;
    }

    TaskMatchResult MatchTemplateAdvanced(const std::string& path, double threshold,
        const std::optional<TaskRoi>& roi, const std::string&, int referenceWidth,
        int referenceHeight, const std::vector<double>&) override
    {
        lastMarker = std::filesystem::path(path).filename().string();
        if (!roi || !frame) { missingRegions.push_back(lastMarker); return {}; }
        const auto marker = Resources::WicImageDecoder::decode_file_utf8(path);
        const Vision::Roi bounds = std::array<std::int32_t, 4>{roi->x, roi->y, roi->width, roi->height};
        const auto match = Vision::find(image, marker, bounds, threshold, referenceWidth, referenceHeight);
        TaskMatchResult result;
        result.found = match.found;
        result.score = match.score;
        result.x = match.x; result.y = match.y;
        result.width = match.width; result.height = match.height;
        result.frameId = frame->frameId;
        result.referenceWidth = referenceWidth; result.referenceHeight = referenceHeight;
        return result;
    }

    std::shared_ptr<const TaskFrameSnapshot> GetCurrentFrame() const override { return frame; }
    bool TapMatch(int x, int y, const TaskMatchResult& match) override
    {
        if (!frame || match.frameId != frame->frameId) return false;
        if (lastMarker == "legacy_auto_select.png") confirmation = true;
        if (lastMarker == "legacy_auto_select_ok.png") { confirmation = false; ++confirmed; }
        if (lastMarker == "legacy_next_enabled.png") ++nextTapped;
        if (lastMarker == "legacy_auto_select_include_guests.png") ++guestToggles;
        return Tap(x, y);
    }

    int confirmed{0};
    int nextTapped{0};
    int guestToggles{0};
    std::vector<std::string> missingRegions;
private:
    bool confirmation{false};
    std::string lastMarker;
    Vision::GrayImage image;
    std::shared_ptr<TaskFrameSnapshot> frame;
};

Career::CareerRuntimeConfig IndependentConfig(std::filesystem::path const& checkpointDirectory)
{
    Career::CareerRuntimeConfig config;
    config.resourceDirectory = ResourceRoot();
    config.manifestPath = Paths::ToUtf8(ResourceRoot() / "hachimi" / "ura" / "manifest.json");
    config.checkpointDirectory = checkpointDirectory;
    config.resourceVersion = "replay-resource-v1";
    config.traineeId = 1001;
    config.careerMode = "independent";
    config.continueExistingCareer = false;
    config.independentTrainingFocus = "balanced";
    config.independentLineupStrategy = "pace";
    return config;
}

std::filesystem::path CheckpointPath(Career::CareerRuntimeConfig const& config)
{
    return config.checkpointDirectory / ("independent-" + std::to_string(*config.traineeId) + ".json");
}

Json ReadJson(std::filesystem::path const& path)
{
    std::ifstream input(path, std::ios::binary);
    if (!input) throw std::runtime_error("could not read test checkpoint: " + Paths::ToUtf8(path));
    return Json::parse(input);
}

void WriteJson(std::filesystem::path const& path, Json const& json)
{
    std::ofstream output(path, std::ios::binary | std::ios::trunc);
    if (!output) throw std::runtime_error("could not write test checkpoint: " + Paths::ToUtf8(path));
    output << json.dump(2);
    output.flush();
    if (!output) throw std::runtime_error("could not flush test checkpoint: " + Paths::ToUtf8(path));
}

bool HasTask(CareerReplayContext const& context, std::string const& name)
{
    return std::find(context.enteredTasks.begin(), context.enteredTasks.end(), name) != context.enteredTasks.end();
}

bool HasTaskPrefix(CareerReplayContext const& context, std::string const& prefix)
{
    return std::any_of(context.enteredTasks.begin(), context.enteredTasks.end(), [&](auto const& name) {
        return name.rfind(prefix, 0) == 0;
    });
}

} // namespace

TEST_CASE("Legacy auto selection uses real catalog regions and allows owned auto results with guests enabled",
    "[GameTasks][Career][Replay][LegacyRegression]")
{
    for (bool const guests : {false, true})
    {
        CAPTURE(guests);
        Career::CareerRuntimeConfig config;
        config.resourceDirectory = ResourceRoot();
        config.useCachedLegacy = false;
        config.useLegacyGuest = guests;
        GameData::UmaGameDatabase trainees(ResourceRoot());
        GameData::CareerSelectionCatalog catalog(ResourceRoot());
        ActionRegistry registry;
        CareerEntryFlow::RegisterActions(config, trainees, catalog, registry);
        LegacyScreenshotReplayContext context;
        REQUIRE(context.CaptureScreen());
        HachimiPipelineTask task;
        task.action = "SelectUraLegacy";
        REQUIRE(registry.ExecuteAction(task, {}, context) == ActionResult::Success);
        CHECK(context.confirmed == 1);
        CHECK(context.nextTapped == 1);
        CHECK(context.guestToggles == 0);
        CHECK(context.missingRegions.empty());
    }
}

TEST_CASE("Manual Legacy selection reads the authored nested trainee badge regions",
    "[GameTasks][Career][Replay][LegacyRegression]")
{
    Career::CareerRuntimeConfig config;
    config.resourceDirectory = ResourceRoot();
    config.legacySelectionMode = "manual";
    config.useCachedLegacy = false;
    config.useLegacyGuest = false;
    config.legacyAttributeSparks = {"speed", "wit"};
    config.legacyAptitudeSparks = {"turf", "late"};
    GameData::UmaGameDatabase trainees(ResourceRoot());
    GameData::CareerSelectionCatalog catalog(ResourceRoot());
    ActionRegistry registry;
    CareerEntryFlow::RegisterActions(config, trainees, catalog, registry);
    CareerReplayContext context;
    context.legacyManualReplay = true;
    HachimiPipelineTask task;
    task.action = "SelectUraLegacy";
    REQUIRE(registry.ExecuteAction(task, {}, context) == ActionResult::Success);
    REQUIRE(context.legacyBadgeRois.size() == 2);
    for (auto const& roi : context.legacyBadgeRois)
    {
        REQUIRE(roi.has_value());
        CHECK(roi->width == 160);
        CHECK(roi->height == 75);
    }
}

TEST_CASE("Career entry requires registered screen evidence before handing off", "[GameTasks][Career][Replay]")
{
    Career::CareerRuntimeConfig config;
    config.resourceDirectory = ResourceRoot();
    config.manifestPath = Paths::ToUtf8(ResourceRoot() / "hachimi" / "ura" / "manifest.json");
    Career::CareerVisualResources resources(config);
    REQUIRE(resources.IsReady());

    SECTION("stable final confirmation is a valid setup boundary")
    {
        ReplayObserver observer("career_final_confirmation");
        CareerReplayContext context;
        auto const result = CareerEntryFlow::Run(config, resources, observer, context);
        CHECK(result.succeeded);
        CHECK(result.lastScreenId == "career_final_confirmation");
        CHECK(observer.observations >= 2);
        CHECK(context.taps.empty());
    }

    SECTION("an arbitrary screen ID cannot be reported as successful entry")
    {
        ReplayObserver observer("screen-that-is-not-in-the-pinned-package");
        CareerReplayContext context;
        auto const result = CareerEntryFlow::Run(config, resources, observer, context);
        CHECK_FALSE(result.succeeded);
        CHECK_FALSE(result.error.empty());
        CHECK(context.taps.empty());
    }
}

TEST_CASE("Career entry chains trainee Next once and configures Legacy before Support",
    "[GameTasks][Career][Replay][EntryRegression]")
{
    Career::CareerRuntimeConfig config;
    config.resourceDirectory = ResourceRoot();
    config.manifestPath = Paths::ToUtf8(ResourceRoot() / "hachimi/ura/manifest.json");
    ActionRegistry actions;
    int legacySelections = 0;
    actions.RegisterHandler("SelectUraTrainee", [](auto const&, auto const&, auto&) { return ActionResult::Success; });
    actions.RegisterHandler("SelectUraLegacy", [&](auto const&, auto const&, auto&) {
        ++legacySelections;
        return ActionResult::Success;
    });
    Career::CareerVisualResources resources(config, std::move(actions));
    REQUIRE(resources.IsReady());
    ReplayObserver observer("trainee_select");
    CareerReplayContext context;
    context.onEvent = [&](auto const& event) {
        if (event.type != TaskEventType::TaskCompleted) return;
        if (event.taskName == "trainee_select_trainee_next") observer.screenId = "legacy_select";
        else if (event.taskName == "legacy_select_legacy_choose") observer.screenId = "support_ready";
        else if (event.taskName == "support_ready_support_start") observer.screenId = "career_final_confirmation";
    };
    auto const result = CareerEntryFlow::Run(config, resources, observer, context);
    CAPTURE(result.error, result.lastScreenId);
    REQUIRE(result.succeeded);
    CHECK(legacySelections == 1);
    CHECK(std::count(context.enteredTasks.begin(), context.enteredTasks.end(), "trainee_select_trainee_next") == 1);
}

TEST_CASE("Entry accepts existing stable proof without a second observation", "[GameTasks][Career][CareerTransition]")
{
    Career::CareerRuntimeConfig config;
    config.resourceDirectory = ResourceRoot();
    Career::CareerVisualResources resources(config);
    REQUIRE(resources.IsReady());
    ReplayObserver observer("career_final_confirmation");
    observer.stableFrameCount = 2;
    CareerReplayContext context;
    const auto result = CareerEntryFlow::Run(config, resources, observer, context, {"career_final_confirmation"});
    REQUIRE(result.succeeded);
    CHECK(observer.observations == 1);
    REQUIRE(result.runtimeObservation);
    CHECK(result.runtimeObservation->stableFrameCount == 2);
    CHECK(context.taps.empty());
}

TEST_CASE("Resuming Career preserves confirmed runtime dialogs and events for startup",
    "[GameTasks][Career][Replay][ResumeHandoffRegression]")
{
    Career::CareerRuntimeConfig config;
    config.resourceDirectory = ResourceRoot();
    config.manifestPath = Paths::ToUtf8(ResourceRoot() / "hachimi/ura/manifest.json");
    config.continueExistingCareer = true;
    Career::CareerVisualResources resources(config);
    REQUIRE(resources.IsReady());
    for (auto const* screen : {"rest_confirmation", "scenario_event", "race_runner_result"})
    {
        CAPTURE(screen);
        ReplayObserver observer("home");
        observer.stableFrameCount = 2;
        CareerReplayContext context;
        context.onEvent = [&](auto const& event) {
            if (event.type != TaskEventType::TaskCompleted) return;
            if (event.taskName == "home_home_career") observer.screenId = "career_continue";
            if (event.taskName == "career_continue_resume") observer.screenId = screen;
        };
        auto const result = CareerEntryFlow::Run(config, resources, observer, context, {"home"});
        CAPTURE(result.error, result.lastScreenId);
        REQUIRE(result.succeeded);
        REQUIRE(result.runtimeObservation);
        CHECK(result.runtimeObservation->screenId == screen);
        CHECK(result.runtimeObservation->stableFrameCount == 2);
        CHECK(HasTask(context, "career_continue_resume"));
    }
}

TEST_CASE("Career entry waits through stale Support frames without repeating Start",
    "[GameTasks][Career][Replay][EntryRegression]")
{
    Career::CareerRuntimeConfig config;
    config.resourceDirectory = ResourceRoot();
    config.manifestPath = Paths::ToUtf8(ResourceRoot() / "hachimi/ura/manifest.json");
    Career::CareerVisualResources resources(config);
    ReplayObserver observer("support_ready");
    observer.sequence = {"capture_failed", "unknown"};
    CareerReplayContext context;
    context.onEvent = [&](auto const& event) {
        if (event.type == TaskEventType::TaskCompleted && event.taskName == "support_ready_support_start")
        {
            observer.sequence = {"support_ready", "support_ready", "unknown", "unknown"};
            observer.screenId = "career_final_confirmation";
        }
    };
    auto const result = CareerEntryFlow::Run(config, resources, observer, context);
    CAPTURE(result.error);
    REQUIRE(result.succeeded);
    CHECK(std::count(context.enteredTasks.begin(), context.enteredTasks.end(), "support_ready_support_start") == 1);
}

TEST_CASE("Career entry restores TP only when enabled and resumes without rebuilding Support",
    "[GameTasks][Career][Replay][EntryRegression][TpRestore]")
{
    Career::CareerRuntimeConfig config;
    config.resourceDirectory = ResourceRoot();
    config.autoRestoreTp = true;
    // A resumed TP prompt must preserve a manually configured formation too.
    config.supportDeckMode = "selected";
    ReplayObserver observer("career_tp_restore_prompt");
    CareerReplayContext context;
    bool returnToPrompt = false;
    SECTION("restores and starts") {}
    SECTION("disabled stops without consuming anything") { config.autoRestoreTp = false; }
    SECTION("cancellation before OK never consumes a potion") { context.cancelOnTask = "career_tp_restore_ok"; }
    SECTION("unresolved shortage cannot consume indefinitely") { returnToPrompt = true; }
    Career::CareerVisualResources resources(config);
    REQUIRE(resources.IsReady());
    context.onEvent = [&](auto const& event) {
        if (event.type != TaskEventType::TaskCompleted) return;
        if (event.taskName == "career_tp_restore_close") observer.screenId = "support_ready";
        if (event.taskName == "support_ready_support_start")
            observer.screenId = returnToPrompt ? "career_tp_restore_prompt" : "career_final_confirmation";
    };
    auto const result = CareerEntryFlow::Run(config, resources, observer, context);
    CAPTURE(result.error);
    if (!config.autoRestoreTp)
    {
        CHECK_FALSE(result.succeeded);
        CHECK(result.error.find("disabled") != std::string::npos);
        CHECK(context.taps.empty());
    }
    else if (!context.cancelOnTask.empty())
    {
        CHECK_FALSE(result.succeeded);
        CHECK(result.cancelled);
        CHECK(context.taps.size() == 4);
        CHECK_FALSE(HasTask(context, "career_tp_restore_close"));
    }
    else if (returnToPrompt)
    {
        CHECK_FALSE(result.succeeded);
        CHECK(result.error.find("two Toughness 30") != std::string::npos);
        CHECK(std::count(context.enteredTasks.begin(), context.enteredTasks.end(), "career_tp_restore_ok") == 2);
    }
    else
    {
        REQUIRE(result.succeeded);
        CHECK(std::count(context.enteredTasks.begin(), context.enteredTasks.end(), "support_ready_support_start") == 1);
        CHECK(std::count(context.enteredTasks.begin(), context.enteredTasks.end(), "career_tp_restore_ok") == 1);
        CHECK_FALSE(HasTask(context, "support_select_support_reset"));
    }
}

TEST_CASE("TP recovery matches real screenshots and selects the potion row",
    "[GameTasks][Career][Replay][EntryRegression][TpRestore]")
{
    class ScreenshotContext final : public Tests::MockTaskExecutionContext
    {
    public:
        bool CaptureScreen() override
        {
            static const std::array<const char*, 5> files{
                "prompt.png", "list.png", "confirm.png", "result.png", "support.png"};
            if (stage >= files.size()) return false;
            const auto file = stage == 2 && !quantitySelected ? "confirm_zero.png" : files[stage];
            image = Resources::WicImageDecoder::decode_file(
                std::filesystem::path(CMAKE_SOURCE_DIR) / "testdata/hachimi/ura/captures/tp_restore" / file);
            frame = std::make_shared<TaskFrameSnapshot>();
            frame->width = image.width; frame->height = image.height;
            frame->frameId = ++captureCount;
            frame->grayPixels = image.gray; frame->rgbaPixels = image.rgba;
            return true;
        }
        TaskMatchResult MatchTemplateAdvanced(const std::string& path, double threshold,
            const std::optional<TaskRoi>& roi, const std::string& algorithm, int rw, int rh,
            const std::vector<double>&) override
        {
            lastMarker = std::filesystem::path(path).filename().string();
            if (!roi || !frame) return {};
            const auto marker = Resources::WicImageDecoder::decode_file_utf8(path);
            const Vision::Roi bounds = std::array<std::int32_t, 4>{roi->x, roi->y, roi->width, roi->height};
            const auto match = algorithm == "MatchTemplateColor"
                ? Vision::find_color(image, marker, bounds, threshold, rw, rh)
                : Vision::find(image, marker, bounds, threshold, rw, rh);
            TaskMatchResult result;
            result.found = match.found; result.score = match.score;
            result.x = match.x; result.y = match.y; result.width = match.width; result.height = match.height;
            result.frameId = frame->frameId; result.referenceWidth = rw; result.referenceHeight = rh;
            return result;
        }
        std::shared_ptr<const TaskFrameSnapshot> GetCurrentFrame() const override { return frame; }
        bool TapMatch(int x, int y, const TaskMatchResult& match) override
        {
            if (!frame || frame->frameId != match.frameId) return false;
            if (lastMarker == "career_entry_recover_tp_min.png") quantitySelected = false;
            else if (lastMarker == "career_entry_recover_tp_plus.png") quantitySelected = !dropPlus;
            else if (lastMarker == "career_entry_recover_tp_result_close.png" && dropClose) {}
            else ++stage;
            return Tap(x, y);
        }
        std::size_t stage{0};
        bool quantitySelected{true};
        bool dropPlus{false};
        bool dropClose{false};
        std::string lastMarker;
        Vision::GrayImage image;
        std::shared_ptr<TaskFrameSnapshot> frame;
    } context;
    SECTION("Min clears the default selection and Plus selects one potion") {}
    SECTION("the dialog can initially have zero potions selected") { context.quantitySelected = false; }
    SECTION("a lost Plus tap cannot confirm the disabled OK button") { context.dropPlus = true; }
    SECTION("a lost result Close tap cannot report recovery complete") { context.dropClose = true; }
    Career::CareerRuntimeConfig config;
    config.resourceDirectory = ResourceRoot();
    auto resources = std::make_shared<Career::CareerVisualResources>(config);
    REQUIRE(resources->IsReady());
    Career::CareerScreenObserver observer(resources);
    auto const observation = observer.ObserveScreens(context, {"career_tp_restore_prompt"});
    REQUIRE(observation.screenId == "career_tp_restore_prompt");
    const auto result = [&] {
        if (!context.dropPlus && !context.dropClose)
            return resources->RunAction("career_tp_restore_prompt", "tp.restore", context);
        auto definition = resources->GetExecutionDefinition();
        definition.tasks.at("career_tp_restore_ok").timeoutMilliseconds = 0;
        definition.tasks.at("career_tp_restore_support_ready").timeoutMilliseconds = 0;
        return TaskGraphRunner{}.Run(definition, "career_tp_restore_open", context);
    }();
    CAPTURE(result.errorMessage);
    if (context.dropPlus)
    {
        CHECK(result.status == TaskGraphStatus::TimedOut);
        CHECK(result.errorMessage.find("career_tp_restore_ok") != std::string::npos);
        CHECK(context.taps.size() == 4);
        CHECK(context.stage == 2);
        return;
    }
    if (context.dropClose)
    {
        CHECK(result.status == TaskGraphStatus::TimedOut);
        CHECK(result.errorMessage.find("career_tp_restore_support_ready") != std::string::npos);
        CHECK(context.taps.size() == 6);
        CHECK(context.stage == 3);
        return;
    }
    REQUIRE((result.status == TaskGraphStatus::Completed || result.status == TaskGraphStatus::Success));
    REQUIRE(context.taps.size() == 6);
    // Restore, potion Use, Min, Plus, OK, result Close, then verify Support.
    CHECK(context.taps[0].first > 470);
    CHECK(context.taps[1].second > 355);
    CHECK(context.taps[1].second < 450);
    CHECK(context.taps[2].first < 190);
    CHECK(context.taps[3].first > 605);
    CHECK(context.taps[3].second > 790);
    CHECK(context.taps[3].second < 890);
    CHECK(context.taps[4].second > 1100);
    CHECK(context.taps[5].second > 990);
    CHECK(context.taps[5].second < 1105);
    CHECK(context.stage == 4);
}

TEST_CASE("Career entry refuses a match that never stabilizes", "[GameTasks][Career][Replay][EntryRegression]")
{
    Career::CareerRuntimeConfig config;
    config.resourceDirectory = ResourceRoot();
    config.manifestPath = Paths::ToUtf8(ResourceRoot() / "hachimi/ura/manifest.json");
    Career::CareerVisualResources resources(config);
    ReplayObserver observer("career_final_confirmation");
    for (int i = 0; i < 31; ++i)
        observer.sequence.push_back(i % 2 == 0 ? "career_final_confirmation" : "home");
    CareerReplayContext context;
    auto const result = CareerEntryFlow::Run(config, resources, observer, context);
    CHECK_FALSE(result.succeeded);
    CHECK(context.taps.empty());
}

TEST_CASE("Highest-star presets fill five owned slots and a borrowed slot",
    "[GameTasks][Career][Replay][EntryRegression]")
{
    for (auto const* preset : {"speed3-stamina3", "speed3-stamina2-wit1",
        "speed2-stamina2-power1-wit1", "speed2-stamina1-power1-wit1-friend1"})
    {
        CAPTURE(preset);
        Career::CareerRuntimeConfig config;
        config.resourceDirectory = ResourceRoot();
        config.manifestPath = Paths::ToUtf8(ResourceRoot() / "hachimi/ura/manifest.json");
        config.supportDeckMode = "highest-star";
        config.supportDeckPreset = preset;
        Career::CareerVisualResources resources(config);
        REQUIRE(resources.IsReady());
        // A previously populated formation still needs the requested preset.
        ReplayObserver observer("support_ready");
        CareerReplayContext context;
        context.onEvent = [&](auto const& event) {
            if (event.type == TaskEventType::TaskCompleted && event.taskName == "support_select_support_start")
                observer.screenId = "career_final_confirmation";
        };
        auto const result = CareerEntryFlow::Run(config, resources, observer, context);
        CAPTURE(result.error);
        REQUIRE(result.succeeded);
        CHECK(std::count(context.enteredTasks.begin(), context.enteredTasks.end(), "support_select_support_open") == 6);
        CHECK(std::count(context.enteredTasks.begin(), context.enteredTasks.end(), "support_select_support_top_card_ssr") == 5);
        CHECK(HasTask(context, "support_select_support_friend_sort_level"));
        CHECK(HasTask(context, "support_select_support_friend_top_card_ssr"));
        CHECK_FALSE(HasTask(context, "support_select_support_filter_friend"));
    }
}

TEST_CASE("Career selection catalogs retain exact Global IDs and verified metadata", "[GameTasks][Career][Replay]")
{
    GameData::CareerSelectionCatalog catalog(ResourceRoot());
    std::string error;
    REQUIRE(catalog.Load(error));
    CHECK(error.empty());
    CHECK(catalog.TryGetSupportCard(30028) != nullptr);
    CHECK(catalog.TryGetSupportCard(30028)->supportCardId == 30028);
    CHECK(catalog.TryGetSkill(100011) != nullptr);
    CHECK(catalog.TryGetSkill(100011)->skillId == 100011);
    CHECK(catalog.TryGetSkill(100011)->rarity == 3);
    CHECK(catalog.TryGetSkill(100011)->needSkillPoint == 0);
    CHECK(catalog.TryGetSkill(100011)->otherTiers.empty());
    auto const* skill = catalog.TryGetSkill(200014);
    REQUIRE(skill != nullptr);
    CHECK(skill->rarity == 2);
    CHECK(skill->needSkillPoint == 130);
    CHECK(skill->otherTiers == "Right-Handed ◎ / Right-Handed ○ / Right-Handed ×");
    CHECK(catalog.TryGetRace("First Year", "07_02", "Hakodate Junior Stakes") != nullptr);
    CHECK(catalog.TryGetRace("First Year", "07_02", "Hakodate Junior Stakes")->raceId == 3041);
}

TEST_CASE("Independent setup checkpoints a selected race and resumes without selecting it twice", "[GameTasks][Career][Replay]")
{
    TestDirectory directory;
    auto config = IndependentConfig(directory.path);
    config.continueExistingCareer = false;
    GameData::CareerSelectionCatalog catalog(ResourceRoot());
    std::string error;
    REQUIRE(catalog.Load(error));

    auto race = std::find_if(catalog.Races().begin(), catalog.Races().end(), [&](auto const& item) {
        if (!item.gameAvailable || item.pickerHeaderTarget.empty()) return false;
        auto const card = ResourceRoot() / "hachimi" / "career" / "modes" / "independent"
            / "templates" / "independent" / "race_cards" / (std::to_string(item.raceId) + ".png");
        return std::filesystem::is_regular_file(card)
            && std::count_if(catalog.Races().begin(), catalog.Races().end(), [&](auto const& other) {
                return other.gameAvailable && other.raceId != item.raceId
                    && other.year == item.year && other.turn == item.turn
                    && other.pickerHeaderTarget == item.pickerHeaderTarget;
            }) == 0;
    });
    REQUIRE(race != catalog.Races().end());
    auto skill = std::find_if(catalog.Skills().begin(), catalog.Skills().end(), [](auto const& item) {
        return item.singleModeEnabled && item.availableInGlobal && !item.skillName.empty();
    });
    REQUIRE(skill != catalog.Skills().end());
    config.independentAgendaSelections = {race->year + "|" + race->turn + "|" + race->raceName};
    config.independentSkillIds = {skill->skillId};

    Career::CareerVisualResources resources(config);
    REQUIRE(resources.IsReady());
    ReplayObserver observer("career_final_confirmation");
    CareerReplayContext first;
    first.ocrTexts = {race->pickerHeaderTarget, skill->skillName};
    first.cancelOnTask = "independent_agenda_save";
    auto const interrupted = NativeIndependentTrainingFlow::Run(config, resources, observer, first);
    CHECK(interrupted.cancelled);
    CHECK_FALSE(interrupted.succeeded);
    REQUIRE(std::filesystem::is_regular_file(CheckpointPath(config)));
    auto const saved = ReadJson(CheckpointPath(config));
    CHECK(saved.value("stage", std::string{}) == "ConfigureAgenda");
    CHECK(saved.value("lastConfirmedScreen", std::string{}).rfind("agenda-selected-", 0) == 0);

    config.continueExistingCareer = true;
    CareerReplayContext resumed;
    resumed.ocrTexts = {race->pickerHeaderTarget, skill->skillName};
    auto const complete = NativeIndependentTrainingFlow::Run(config, resources, observer, resumed);
    CAPTURE(complete.error);
    CHECK(complete.succeeded);
    CHECK(complete.lastScreenId == "home");
    CHECK_FALSE(HasTaskPrefix(resumed, "independent_agenda_year_"));
    CHECK(HasTask(resumed, "independent_skills_search_checkbox_ocr"));
    CHECK(HasTask(resumed, "independent_post_start_home_probe"));
    CHECK(std::count(resumed.enteredTasks.begin(), resumed.enteredTasks.end(), "independent_training_start") == 1);
    auto const completedState = ReadJson(CheckpointPath(config));
    CHECK(completedState.value("stage", std::string{}) == "Completed");
    CHECK(completedState.value("completionVerified", false));
}

TEST_CASE("Post-start checkpoint routes directly to home proof and never submits Start again", "[GameTasks][Career][Replay]")
{
    TestDirectory directory;
    auto config = IndependentConfig(directory.path);
    config.continueExistingCareer = false;
    Json checkpoint{
        {"Version", 2}, {"Stage", "HandlePostStartDialog"}, {"AgendaIndex", 2},
        {"SkillIndex", 3}, {"CurrentSkillId", nullptr},
        {"LastConfirmedScreen", "independent_start_issued"}, {"CompletionVerified", false}};
    {
        std::ofstream output(CheckpointPath(config));
        output << checkpoint.dump(2);
    }

    bool needsEntry = true;
    std::string error;
    CareerReplayContext context;
    REQUIRE(NativeIndependentTrainingFlow::NeedsEntry(config, context, needsEntry, error));
    CHECK(needsEntry); // Without recovery override, the original fresh-start preference applies.
    REQUIRE(NativeIndependentTrainingFlow::NeedsEntry(config, context, needsEntry, error, true));
    CHECK_FALSE(needsEntry);
    CHECK(error.empty());

    Career::CareerVisualResources resources(config);
    REQUIRE(resources.IsReady());
    ReplayObserver observer("home");
    auto const result = NativeIndependentTrainingFlow::Run(config, resources, observer, context, true);
    CHECK(result.succeeded);
    CHECK_FALSE(HasTask(context, "independent_training_start"));
    CHECK(HasTask(context, "independent_post_start_home_probe"));
}

TEST_CASE("Locked Independent checkpoint preserves the prior marker and resumes after the lock clears", "[GameTasks][Career][Replay]")
{
#ifdef _WIN32
    TestDirectory directory;
    auto config = IndependentConfig(directory.path);
    config.continueExistingCareer = true;
    auto const checkpointPath = CheckpointPath(config);
    Json const initial{
        {"version", 2}, {"stage", "HandlePostStartDialog"}, {"agendaIndex", 0},
        {"skillIndex", 0}, {"currentSkillId", nullptr},
        {"lastConfirmedScreen", "independent_start_issued"}, {"completionVerified", false}};
    WriteJson(checkpointPath, initial);

    HANDLE const lockedCheckpoint = CreateFileW(
        checkpointPath.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL, nullptr);
    REQUIRE(lockedCheckpoint != INVALID_HANDLE_VALUE);
    std::unique_ptr<void, decltype(&CloseHandle)> checkpointLock(lockedCheckpoint, &CloseHandle);
    {
        Career::CareerVisualResources resources(config);
        REQUIRE(resources.IsReady());
        ReplayObserver observer("home");
        CareerReplayContext blocked;
        auto const failed = NativeIndependentTrainingFlow::Run(config, resources, observer, blocked);
        CHECK_FALSE(failed.succeeded);
        CHECK(failed.error.find("Could not save Independent Training checkpoint") != std::string::npos);
        CHECK(ReadJson(checkpointPath) == initial);
    }
    REQUIRE(CloseHandle(checkpointLock.release()));

    Career::CareerVisualResources resources(config);
    REQUIRE(resources.IsReady());
    ReplayObserver observer("home");
    CareerReplayContext resumed;
    auto const complete = NativeIndependentTrainingFlow::Run(config, resources, observer, resumed);
    CAPTURE(complete.error);
    CHECK(complete.succeeded);
    CHECK_FALSE(HasTask(resumed, "independent_training_start"));
    CHECK(HasTask(resumed, "independent_post_start_home_probe"));
    auto const saved = ReadJson(checkpointPath);
    CHECK(saved.value("stage", std::string{}) == "Completed");
    CHECK(saved.value("completionVerified", false));
#else
    SUCCEED("Atomic replacement lock semantics are verified on Windows builds.");
#endif
}

TEST_CASE("Completed independent checkpoint is history and routes a new run through entry", "[GameTasks][Career][Replay]")
{
    TestDirectory directory;
    auto config = IndependentConfig(directory.path);
    config.continueExistingCareer = true;
    WriteJson(CheckpointPath(config), Json{
        {"version", 2}, {"stage", "Completed"}, {"agendaIndex", 0},
        {"skillIndex", 0}, {"currentSkillId", nullptr},
        {"lastConfirmedScreen", "home"}, {"completionVerified", true}});

    CareerReplayContext context;
    bool needsEntry = false;
    std::string error;
    REQUIRE(NativeIndependentTrainingFlow::NeedsEntry(config, context, needsEntry, error));
    CHECK(needsEntry);
    CHECK(error.empty());
}

TEST_CASE("Identity-bound checkpoint rejects a different connected device", "[GameTasks][Career][Replay]")
{
    TestDirectory directory;
    auto config = IndependentConfig(directory.path);
    config.continueExistingCareer = false;
    config.independentTrainingFocus = "unsupported-to-stop-before-action";
    Career::CareerVisualResources resources(config);
    REQUIRE(resources.IsReady());
    ReplayObserver observer("career_final_confirmation");
    CareerReplayContext context;
    auto const result = NativeIndependentTrainingFlow::Run(config, resources, observer, context);
    CHECK_FALSE(result.succeeded);
    REQUIRE(std::filesystem::is_regular_file(CheckpointPath(config)));

    config.continueExistingCareer = true;
    auto checkpoint = ReadJson(CheckpointPath(config));
    bool needsEntry = false;
    std::string error;
    checkpoint["adbSerial"] = "another-device";
    WriteJson(CheckpointPath(config), checkpoint);
    CHECK_FALSE(NativeIndependentTrainingFlow::NeedsEntry(config, context, needsEntry, error));
    CHECK_FALSE(error.empty());
    CHECK(error.find("different connected device") != std::string::npos);

    checkpoint["adbSerial"] = context.serial;
    WriteJson(CheckpointPath(config), checkpoint);
    config.independentTrainingFocus = "sprint";
    CHECK_FALSE(NativeIndependentTrainingFlow::NeedsEntry(config, context, needsEntry, error));
    CHECK(error.find("settings no longer match") != std::string::npos);

    config.independentTrainingFocus = "unsupported-to-stop-before-action";
    config.resourceVersion = "replay-resource-v2";
    CHECK_FALSE(NativeIndependentTrainingFlow::NeedsEntry(config, context, needsEntry, error));
    CHECK(error.find("different packaged resource version") != std::string::npos);

    config.resourceVersion = "replay-resource-v1";
    config.traineeId = 1002;
    WriteJson(directory.path / "independent-1002.json", checkpoint);
    CHECK_FALSE(NativeIndependentTrainingFlow::NeedsEntry(config, context, needsEntry, error));
    CHECK(error.find("different trainee") != std::string::npos);
}

TEST_CASE("Pending skill checkpoint refuses a blind checkbox toggle", "[GameTasks][Career][Replay]")
{
    TestDirectory directory;
    auto config = IndependentConfig(directory.path);
    config.continueExistingCareer = true;
    GameData::CareerSelectionCatalog catalog(ResourceRoot());
    std::string catalogError;
    REQUIRE(catalog.Load(catalogError));
    auto const skill = std::find_if(catalog.Skills().begin(), catalog.Skills().end(), [](auto const& item) {
        return item.singleModeEnabled && item.availableInGlobal && !item.skillName.empty();
    });
    REQUIRE(skill != catalog.Skills().end());
    config.independentSkillIds = {skill->skillId};
    WriteJson(CheckpointPath(config), Json{
        {"version", 2}, {"stage", "ConfigureSkills"}, {"agendaIndex", 0},
        {"skillIndex", 0}, {"currentSkillId", skill->skillId},
        {"lastConfirmedScreen", "skill-pending-" + std::to_string(skill->skillId)},
        {"completionVerified", false}});

    Career::CareerVisualResources resources(config);
    REQUIRE(resources.IsReady());
    ReplayObserver observer("career_final_confirmation");
    CareerReplayContext context;
    auto const result = NativeIndependentTrainingFlow::Run(config, resources, observer, context);
    CHECK_FALSE(result.succeeded);
    CHECK(result.error.find("avoid toggling") != std::string::npos);
    CHECK_FALSE(HasTask(context, "independent_skills_search_checkbox_ocr"));
    CHECK_FALSE(HasTask(context, "independent_skills_search_checkbox"));
}

TEST_CASE("Date recovery during Independent skill setup preserves earlier selection and does not repeat Start", "[GameTasks][Career][Replay]")
{
    TestDirectory directory;
    auto config = IndependentConfig(directory.path);
    config.continueExistingCareer = false;
    GameData::CareerSelectionCatalog catalog(ResourceRoot());
    std::string catalogError;
    REQUIRE(catalog.Load(catalogError));
    std::vector<GameData::UmaIndependentSkillRecord const*> skills;
    for (auto const& skill : catalog.Skills())
        if (skill.singleModeEnabled && skill.availableInGlobal && !skill.skillName.empty()) skills.push_back(&skill);
    REQUIRE(skills.size() >= 2);
    config.independentSkillIds = {skills[0]->skillId, skills[1]->skillId};

    Career::CareerVisualResources resources(config);
    REQUIRE(resources.IsReady());
    ReplayObserver observer("career_final_confirmation");
    CareerReplayContext interrupted;
    interrupted.ocrTexts = {skills[0]->skillName, skills[1]->skillName};
    interrupted.interruptOnTask = "independent_skills_open";
    interrupted.interruptAfterOccurrences = 2;
    CHECK_THROWS_AS(NativeIndependentTrainingFlow::Run(config, resources, observer, interrupted), TaskContextInterruption);
    auto const checkpoint = ReadJson(CheckpointPath(config));
    CHECK(checkpoint.value("stage", std::string{}) == "ConfigureSkills");
    CHECK(checkpoint.value("skillIndex", -1) == 1);
    CHECK(checkpoint.value("currentSkillId", 0) == skills[1]->skillId);
    CHECK(checkpoint.value("lastConfirmedScreen", std::string{}) == "skill-searching-" + std::to_string(skills[1]->skillId));
    CHECK(std::count(interrupted.inputs.begin(), interrupted.inputs.end(), skills[0]->searchText.empty()
        ? skills[0]->skillName : skills[0]->searchText) == 1);

    bool needsEntry = false;
    std::string error;
    REQUIRE(NativeIndependentTrainingFlow::NeedsEntry(config, interrupted, needsEntry, error, true));
    CHECK(needsEntry);
    auto entryConfig = config;
    entryConfig.continueExistingCareer = true;
    entryConfig.deleteExistingCareerData = false;
    auto const entered = CareerEntryFlow::Run(entryConfig, resources, observer, interrupted);
    CHECK(entered.succeeded);

    CareerReplayContext resumed;
    resumed.ocrTexts = {skills[0]->skillName, skills[1]->skillName};
    auto const complete = NativeIndependentTrainingFlow::Run(config, resources, observer, resumed, true);
    CHECK(complete.succeeded);
    CHECK(resumed.inputs.size() == 1);
    CHECK(resumed.inputs.front() == (skills[1]->searchText.empty() ? skills[1]->skillName : skills[1]->searchText));
    CHECK(std::count(resumed.enteredTasks.begin(), resumed.enteredTasks.end(), "independent_training_start") == 1);
    CHECK(std::count(resumed.enteredTasks.begin(), resumed.enteredTasks.end(), "independent_post_start_home_probe") == 1);
}

TEST_CASE("Legacy URA checkpoint flags migrate without deleting the original saved progress", "[GameTasks][Career][Replay]")
{
    TestDirectory directory;
    auto config = IndependentConfig(directory.path);
    config.continueExistingCareer = true;
    auto const legacyPath = config.checkpointDirectory / ("ura-" + std::to_string(*config.traineeId) + ".json");
    Json legacy{
        {"IndependentModeSelected", true}, {"IndependentLineupConfigured", true},
        {"IndependentTrainingFocusConfigured", true}, {"IndependentAgendaConfigured", true},
        {"IndependentSkillsConfigured", true}, {"IndependentLineupCollapsed", false},
        {"IndependentStrategyConfigured", false}, {"IndependentSetupCompleted", false}};
    {
        std::ofstream output(legacyPath);
        output << legacy.dump(2);
    }

    CareerReplayContext context;
    bool needsEntry = false;
    std::string error;
    REQUIRE(NativeIndependentTrainingFlow::NeedsEntry(config, context, needsEntry, error));
    CHECK(needsEntry);
    CHECK(error.empty());
    CHECK(std::filesystem::exists(legacyPath));
}
