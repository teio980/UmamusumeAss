#include "CareerDomain/CareerCalendar.hpp"
#include "CareerDomain/CareerState.hpp"
#include "CareerDomain/IScenarioModule.hpp"
#include "CareerDomain/TrainingStrategies.hpp"
#include "CareerDomain/Scenarios/Ura/UraScenarioModule.hpp"
#include "GameData/CareerSelectionCatalog.hpp"
#include "GameData/UmaGameDatabase.hpp"
#include "GameTasks/GameTaskActionExtensions.hpp"
#include "GameTasks/DailyRaceRunnerSelector.hpp"
#include "GameTasks/NativeGameTaskRunner.hpp"
#include "GameTasks/ShopItemSelector.hpp"
#include "GameTasks/StartGameMonitor.hpp"
#include "Common/PathEncoding.hpp"
#include "Resources/ImageTransforms.hpp"
#include "Resources/ResourceSnapshot.hpp"
#include "Resources/WicImageDecoder.hpp"
#include "TaskEngine/ActionRegistry.hpp"
#include "TaskEngine/TaskFrameSnapshot.hpp"
#include "../TaskEngine/MockTaskExecutionContext.hpp"

#include <catch2/catch_test_macros.hpp>

#include <chrono>
#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <functional>
#include <memory>
#include <nlohmann/json.hpp>
#include <string>
#include <tuple>
#include <utility>
#include <vector>

namespace
{

using namespace UmaAssistant;
using namespace UmaAssistant::TaskEngine;

std::filesystem::path ResourceRoot()
{
    return std::filesystem::path(CMAKE_SOURCE_DIR) / "resource";
}

class MonitorContext final : public Tests::MockTaskExecutionContext
{
public:
    using Matcher = std::function<TaskMatchResult(std::string const&, std::uint64_t)>;

    bool CaptureScreen() override
    {
        ++captureCount;
        ++frameId;
        if (onCapture) onCapture();
        currentFrame = std::make_shared<TaskFrameSnapshot>();
        currentFrame->width = frameWidth;
        currentFrame->height = frameHeight;
        currentFrame->frameId = frameId;
        currentFrame->sessionGeneration = 1;
        currentFrame->geometryGeneration = 1;
        currentFrame->grayPixels = frameGrayPixels;
        currentFrame->rgbaPixels = frameRgbaPixels;
        return captureSucceeds;
    }

    std::shared_ptr<const TaskFrameSnapshot> GetCurrentFrame() const override
    {
        return currentFrame;
    }

    std::shared_ptr<const TaskFrameSnapshot> CaptureInspectionFrame() override
    {
        ++inspectionCaptureCount;
        return inspectionFrame ? inspectionFrame() : nullptr;
    }

    TaskMatchResult MatchTemplateAdvanced(
        const std::string& path,
        double,
        const std::optional<TaskRoi>&,
        const std::string&,
        int referenceWidth,
        int referenceHeight,
        const std::vector<double>&) override
    {
        if (!matcher) return {};
        auto match = matcher(path, frameId);
        matchRequests.push_back(path);
        if (match.found)
        {
            match.referenceWidth = referenceWidth;
            match.referenceHeight = referenceHeight;
            match.frameId = frameId;
            match.sessionGeneration = 1;
            match.geometryGeneration = 1;
        }
        return match;
    }

    bool TapMatch(int x, int y, const TaskMatchResult& match) override
    {
        matchTaps.push_back({x, y, match});
        if (onMatchTap) onMatchTap(x, y, match);
        return Tests::MockTaskExecutionContext::TapMatch(x, y, match);
    }

    bool ProbeHsv(
        int x, int y, int radius, double hMin, double hMax,
        double sMin, double sMax, double vMin, double vMax, double minRatio) override
    {
        if (probeMatcher) return probeMatcher(x, y);
        return Tests::MockTaskExecutionContext::ProbeHsv(
            x, y, radius, hMin, hMax, sMin, sMax, vMin, vMax, minRatio);
    }

    Matcher matcher;
    bool captureSucceeds{true};
    int frameWidth{900};
    int frameHeight{1600};
    std::uint64_t frameId{0};
    std::shared_ptr<TaskFrameSnapshot> currentFrame;
    std::vector<std::uint8_t> frameGrayPixels;
    std::vector<std::uint8_t> frameRgbaPixels;
    std::vector<std::string> matchRequests;
    struct MatchTap { int x; int y; TaskMatchResult match; };
    std::vector<MatchTap> matchTaps;
    std::function<void(int, int, const TaskMatchResult&)> onMatchTap;
    std::function<bool(int, int)> probeMatcher;
    std::function<void()> onCapture;
    std::function<std::shared_ptr<const TaskFrameSnapshot>()> inspectionFrame;
    int inspectionCaptureCount{0};
};

class FakePackageOperations final : public GameTasks::IGameTaskPackageOperations
{
public:
    GameTasks::GameTaskPackageResult Start(
        const std::string&, const std::string&) override
    {
        ++startCount;
        return {launchSucceeds, launchSucceeds, false, launchSucceeds ? 0 : 5,
            launchSucceeds ? "started" : "launcher failed"};
    }

    GameTasks::GameTaskPackageResult Stop(const std::string&) override
    {
        ++stopCount;
        return {true, false, false, 0, "stopped"};
    }

    GameTasks::GameTaskPackageResult Query(const std::string&) override
    {
        return {true, true, false, 0, "running"};
    }

    int startCount{0};
    int stopCount{0};
    bool launchSucceeds{true};
};

TaskMatchResult HitAt(int x)
{
    return TaskMatchResult{true, 0.99, x, 200, 30, 30};
}

GameTasks::StartGameMonitor::Clock AdvancingClock()
{
    auto elapsed = std::chrono::milliseconds{0};
    return [elapsed]() mutable {
        auto const point = std::chrono::steady_clock::time_point{} + elapsed;
        elapsed += std::chrono::seconds(200);
        return point;
    };
}

struct TemporaryCareerCatalogRoot final
{
    TemporaryCareerCatalogRoot()
    {
        const auto stamp = std::chrono::steady_clock::now().time_since_epoch().count();
        path = std::filesystem::temp_directory_path()
            / ("uma-career-catalog-" + std::to_string(stamp));
    }

    ~TemporaryCareerCatalogRoot()
    {
        std::error_code error;
        std::filesystem::remove_all(path, error);
    }

    std::filesystem::path path;
};

} // namespace

TEST_CASE("Career selection catalog preserves first-card and unknown game order", "[game-tasks]")
{
    TemporaryCareerCatalogRoot temporary;
    const auto database = temporary.path / "uma" / "database" / "global";
    const auto racesDirectory = temporary.path / "hachimi" / "ura"
        / "independent_training";
    std::filesystem::create_directories(database);
    std::filesystem::create_directories(racesDirectory);
    {
        std::ofstream output(database / "support_cards.json", std::ios::binary);
        REQUIRE(output.good());
        output << "[]";
    }
    {
        std::ofstream output(database / "skills.json", std::ios::binary);
        REQUIRE(output.good());
        output << R"({"skills":[]})";
    }
    {
        std::ofstream output(racesDirectory / "races.global.json", std::ios::binary);
        REQUIRE(output.good());
        output << R"({"races":[
            {"raceName":"First Card","year":"First Year","turn":"01_01","raceId":1,"grade":"G3","gameAvailable":true,"gameOrder":0},
            {"raceName":"Unknown Card Order","year":"First Year","turn":"01_02","raceId":2,"grade":"G2","gameAvailable":true}
        ]})";
    }

    GameData::CareerSelectionCatalog catalog(temporary.path);
    std::string error;
    REQUIRE(catalog.Load(error));
    REQUIRE(error.empty());
    REQUIRE(catalog.Races().size() == 2);
    REQUIRE(catalog.Races()[0].gameOrder == 0);
    REQUIRE(catalog.Races()[1].gameOrder == -1);
    REQUIRE(catalog.Races()[0].gameAvailable);
    REQUIRE(catalog.Races()[1].gameAvailable);
}

TEST_CASE("Career race calendar maps catalog slots to one-based turns", "[game-tasks]")
{
    using UmaAssistant::CareerDomain::CareerCalendar;
    CHECK(CareerCalendar::TryMapRaceSlot("First Year", "01_01") == 1);
    CHECK(CareerCalendar::TryMapRaceSlot("First Year", "07_02") == 14);
    CHECK(CareerCalendar::TryMapRaceSlot("Second Year", "01_01") == 25);
    CHECK(CareerCalendar::TryMapRaceSlot("Third Year", "12_02") == 72);
    CHECK_FALSE(CareerCalendar::TryMapRaceSlot("Fourth Year", "01_01").has_value());
}

TEST_CASE("URA scenario registry creates fresh per-run modules", "[game-tasks]")
{
    using namespace UmaAssistant::CareerDomain;
    UraScenarioModule::Register();
    auto first = ScenarioRegistry::Instance().CreateModule("ura");
    auto second = ScenarioRegistry::Instance().CreateModule("URA");
    REQUIRE(first != nullptr);
    REQUIRE(second != nullptr);
    REQUIRE(first.get() != second.get());

    CareerState firstState;
    CareerObservation finale;
    finale.screenId = "goal_complete";
    finale.screenKind = CareerScreenKind::GoalUpdate;
    first->OnObserve(firstState, finale);

    CareerState secondState;
    DefaultTrainingStrategy normal("speed", 45);
    CHECK(normal.Choose(firstState, first.get()).kind == ActionIntentKind::Race);
    CHECK(normal.Choose(secondState, second.get()).kind == ActionIntentKind::Training);
}

TEST_CASE("Game task validation rejects unknown or malformed tasks and accepts package fallback", "[game-tasks]")
{
    std::string error;
    CHECK_FALSE(GameTasks::NativeGameTaskRunner::ValidateSettings("not-a-task", nlohmann::json::object(), error));
    CHECK_FALSE(error.empty());
    CHECK_FALSE(GameTasks::NativeGameTaskRunner::ValidateSettings(
        "start-game", nlohmann::json{{"packageId", ""}}, error));
    CHECK(GameTasks::NativeGameTaskRunner::ValidateSettings(
        "start-game", nlohmann::json{{"packageId", "com.example.game"}, {"activityName", ""}}, error));
    CHECK(GameTasks::NativeGameTaskRunner::ValidateSettings("shop-purchase", nlohmann::json::object(), error));
    CHECK_FALSE(GameTasks::NativeGameTaskRunner::ValidateSettings(
        "shop-purchase", nlohmann::json{{"shop", "invalid"}}, error));
}

TEST_CASE("Unbound selectors fail closed and shop with no selected items fails", "[game-tasks]")
{
    Tests::MockTaskExecutionContext context;
    ActionRegistry registry;
    GameTasks::GameTaskActionExtensions::RegisterAll(registry);
    HachimiPipelineTask task;
    task.action = "SelectShopItems";
    CHECK(registry.ExecuteAction(task, HitAt(10), context) == ActionResult::Failed);
    task.action = "SelectDailyRaceRunner";
    CHECK(registry.ExecuteAction(task, HitAt(10), context) == ActionResult::Failed);
    CHECK(context.taps.empty());

    HachimiPipelineDefinition definition;
    GameTasks::ShopPurchaseOptions options;
    auto const selection = GameTasks::ShopItemSelector::Select(context, definition, options);
    CHECK_FALSE(selection.succeeded);
    CHECK_FALSE(selection.hasSelection);

    task.action = "TapToStart";
    CHECK(registry.ExecuteAction(task, HitAt(10), context) == ActionResult::Failed);
    CHECK(context.taps.empty());
    task.specificRect = std::vector<int>{100, 200, 60, 40};
    CHECK(registry.ExecuteAction(task, HitAt(10), context) == ActionResult::Success);
    REQUIRE(context.taps.size() == 1);
    CHECK((context.taps.back() == std::pair<int, int>{130, 220}));
}

TEST_CASE("Shop OCR selector verifies a row checkbox and fails closed when selection evidence is missing", "[game-tasks]")
{
    auto const root = ResourceRoot();
    Resources::ResourceSnapshot resources(root);
    HachimiPipelineDefinition definition;
    definition.baseDirectory = Paths::ToUtf8(root / "hachimi/pipelines");
    GameTasks::ShopPurchaseOptions options;
    options.enabled = true;
    options.buyStarPieces = true;

    auto prepareContext = [&](MonitorContext& context, bool hasOcrRow, bool checksAfterTap) {
        context.frameWidth = 450;
        context.frameHeight = 800;
        context.frameGrayPixels.assign(450U * 800U, 24);
        auto const checkboxPath = Paths::ToUtf8(
            root / "hachimi/pipelines/templates/shop/item_checkbox.png");
        auto const checkbox = resources.load_template(
            checkboxPath, Resources::TemplatePreparation::original);
        REQUIRE(checkbox != nullptr);
        auto const scaled = Resources::resize_nearest(checkbox->image, 45, 45);
        for (int y = 0; y < scaled.height; ++y)
            std::copy_n(scaled.gray.begin() + static_cast<std::ptrdiff_t>(y) * scaled.width,
                scaled.width,
                context.frameGrayPixels.begin() + static_cast<std::ptrdiff_t>(360 + y) * 450 + 360);

        context.matcher = [](std::string const& path, std::uint64_t) {
            if (path.find("confirm_disabled.png") != std::string::npos) return HitAt(300);
            return TaskMatchResult{};
        };
        if (hasOcrRow)
            context.mockOcrDetections = {TaskOcrDetection{"Star Piece", 200, 700, 120, 40, 0.99}};

        auto selected = std::make_shared<bool>(false);
        context.probeMatcher = [selected](int x, int y) {
            return *selected && x == 765 && y == 762;
        };
        context.onMatchTap = [selected, checksAfterTap](int, int, TaskMatchResult const&) {
            if (checksAfterTap) *selected = true;
        };
    };

    SECTION("selected OCR row is confirmed and the list is scanned to its bottom")
    {
        MonitorContext context;
        prepareContext(context, true, true);
        auto const result = GameTasks::ShopItemSelector::Select(context, definition, options, &resources);
        INFO(result.message);
        CHECK(result.succeeded);
        CHECK(result.hasSelection);
        CHECK(result.verifiedRows == 1);
        REQUIRE(context.matchTaps.size() == 1);
        CHECK(context.matchTaps.front().x >= 745);
        CHECK(context.matchTaps.front().x <= 785);
        CHECK(context.matchTaps.front().y >= 738);
        CHECK(context.matchTaps.front().y <= 786);
        REQUIRE(context.swipes.size() == 2);
        CHECK(context.swipes[0].y1 == 650);
        CHECK(context.swipes[0].y2 == 1050);
        CHECK(context.swipes[1].y1 == 1050);
        CHECK(context.swipes[1].y2 == 850);
    }

    SECTION("OCR miss completes the scan without claiming a selection")
    {
        MonitorContext context;
        prepareContext(context, false, true);
        auto const result = GameTasks::ShopItemSelector::Select(context, definition, options, &resources);
        CHECK(result.succeeded);
        CHECK_FALSE(result.hasSelection);
        CHECK(result.verifiedRows == 0);
        CHECK(context.matchTaps.empty());
        REQUIRE(context.swipes.size() == 2);
    }

    SECTION("a checkbox tap without checked-state evidence is never reported as selected")
    {
        MonitorContext context;
        prepareContext(context, true, false);
        auto const result = GameTasks::ShopItemSelector::Select(context, definition, options, &resources);
        CHECK(result.succeeded);
        CHECK_FALSE(result.hasSelection);
        CHECK(result.verifiedRows == 0);
        CHECK(context.matchTaps.size() == 1);
        REQUIRE(context.swipes.size() == 2);
    }
}

TEST_CASE("Packaged startup monitor prioritizes same-frame Home and confirms it twice", "[game-tasks]")
{
    MonitorContext context;
    context.matcher = [](std::string const& path, std::uint64_t) {
        if (path.find("game_home_selected.png") != std::string::npos)
            return HitAt(10);
        if (path.find("startnotice_skip.png") != std::string::npos)
            return HitAt(100);
        if (path.find("logo_skip.png") != std::string::npos)
            return HitAt(200);
        return TaskMatchResult{};
    };

    auto const result = GameTasks::StartGameMonitor::Run(context, ResourceRoot());
    CHECK(result.succeeded);
    CHECK(result.homeDetected);
    CHECK(context.captureCount == 2);
    CHECK(context.taps.empty());
}

TEST_CASE("Packaged startup monitor runs its ordered trigger chain before accepting Home", "[game-tasks]")
{
    MonitorContext context;
    context.matcher = [](std::string const& path, std::uint64_t frame) {
        if (path.find("startnotice_skip.png") != std::string::npos && frame == 1)
            return HitAt(100);
        if (path.find("logo_skip.png") != std::string::npos && frame == 2)
            return HitAt(200);
        if (path.find("game_home_selected.png") != std::string::npos && frame >= 3)
            return HitAt(300);
        return TaskMatchResult{};
    };

    auto const result = GameTasks::StartGameMonitor::Run(context, ResourceRoot());
    CHECK(result.succeeded);
    CHECK(result.homeDetected);
    REQUIRE(context.taps.size() == 2);
    CHECK(context.taps[0] == (std::pair<int, int>{115, 215}));
    CHECK(context.taps[1] == (std::pair<int, int>{215, 215}));
}

TEST_CASE("Startup timeout bounds continuous capture failure and repeated trigger hits", "[game-tasks]")
{
    SECTION("capture keeps failing")
    {
        MonitorContext context;
        context.captureSucceeds = false;
        auto const result = GameTasks::StartGameMonitor::Run(context, ResourceRoot(), AdvancingClock());
        CHECK_FALSE(result.succeeded);
        CHECK(context.captureCount == 1);
    }

    SECTION("the same trigger keeps matching")
    {
        MonitorContext context;
        context.matcher = [](std::string const& path, std::uint64_t) {
            if (path.find("startnotice_skip.png") != std::string::npos)
                return HitAt(100);
            return TaskMatchResult{};
        };
        auto const result = GameTasks::StartGameMonitor::Run(context, ResourceRoot(), AdvancingClock());
        CHECK_FALSE(result.succeeded);
        REQUIRE(context.taps.size() == 1);
    }
}

TEST_CASE("Game database loads packaged runner records", "[game-tasks]")
{
    GameData::UmaGameDatabase database(ResourceRoot());
    std::string error;
    REQUIRE(database.Load(error));
    auto const* trainee = database.TryGetTrainee(100101);
    REQUIRE(trainee != nullptr);
    CHECK(trainee->nameEn == "Special Week");
    CHECK(trainee->available);
    CHECK_FALSE(trainee->distanceAptitudes.empty());
    REQUIRE_FALSE(trainee->careerObjectives.empty());
    REQUIRE_FALSE(trainee->careerObjectives.front().raceIds.empty());
    auto const* objectiveRace = database.TryGetCareerRace(
        trainee->careerObjectives.front().raceIds.front());
    REQUIRE(objectiveRace != nullptr);
    CHECK(objectiveRace->raceId == trainee->careerObjectives.front().raceIds.front());
    CHECK(objectiveRace->distanceMeters > 0);
    CHECK_FALSE(objectiveRace->distanceBand.empty());
}

TEST_CASE("Native game runner replays each packaged task graph to its terminal", "[game-tasks]")
{
    auto const root = ResourceRoot();
    Resources::ResourceSnapshot resources(root);

    struct GraphFixture
    {
        std::string taskId;
        std::string terminalTask;
        nlohmann::json settings;
        bool startGame{false};
    };
    auto const package = nlohmann::json{{"packageId", "com.example.game"}, {"activityName", ""}};
    auto const shopDisabled = nlohmann::json{{"enabled", false}};
    std::vector<GraphFixture> fixtures{
        {"start-game", "", package, true},
        {"mail-collection", "homeVerify",
            nlohmann::json{{"definitionPath", Paths::ToUtf8(std::filesystem::absolute(
                root / "hachimi/pipelines/mail_collection.json"))}, {"shop", shopDisabled}}},
        {"mission-collection", "homeVerify",
            nlohmann::json{{"definitionPath", Paths::ToUtf8(std::filesystem::absolute(
                root / "hachimi/pipelines/mission_collection.json"))}, {"shop", shopDisabled}}},
        {"daily-race", "complete",
            nlohmann::json{{"definitionPath", Paths::ToUtf8(std::filesystem::absolute(
                root / "hachimi/pipelines/daily_race.json"))}, {"mode", "monies"},
                {"difficulty", "veryhard"}, {"raceCount", 1}, {"traineeId", nullptr}, {"shop", shopDisabled}}},
        {"team-race", "complete",
            nlohmann::json{{"definitionPath", Paths::ToUtf8(std::filesystem::absolute(
                root / "hachimi/pipelines/team_race.json"))}, {"raceCount", 1},
                {"stopWhenTicketsEmpty", false}, {"shop", shopDisabled}}},
        {"shop-purchase", "homeVerify",
            nlohmann::json{{"shop", nlohmann::json{{"enabled", true}, {"selectAll", true}}}}}
    };

    auto hasCompletedInOrder = [](std::vector<TaskExecutionEvent> const& events,
        std::vector<std::string> const& expectedNames) {
        auto next = events.begin();
        for (auto const& expected : expectedNames)
        {
            next = std::find_if(next, events.end(), [&expected](TaskExecutionEvent const& event) {
                return event.type == TaskEventType::TaskCompleted && event.taskName == expected;
            });
            if (next == events.end()) return false;
            ++next;
        }
        return true;
    };

    for (auto const& fixture : fixtures)
    {
        CAPTURE(fixture.taskId);
        MonitorContext context;
        context.matcher = [](std::string const& path, std::uint64_t) {
            if (path.find("date_changed/") != std::string::npos) return TaskMatchResult{};
            if (path.find("date_changed\\") != std::string::npos) return TaskMatchResult{};
            if (path.find("/err.png") != std::string::npos
                || path.find("/titlescreen.png") != std::string::npos)
                return TaskMatchResult{};
            if (path.find("runner_sort_asc.png") != std::string::npos) return TaskMatchResult{};
            if (path.find("runner_sort_desc.png") != std::string::npos) return HitAt(710);
            return HitAt(100);
        };
        auto settings = fixture.settings;
        settings.update(package);
        FakePackageOperations packageOperations;
        auto const result = GameTasks::NativeGameTaskRunner::Run(
            fixture.taskId, settings, context, root, &packageOperations, &resources);
        INFO(result.message);
        CHECK(result.succeeded);
        CHECK(result.taskId == fixture.taskId);
        CHECK(result.totalUnits == 1);
        CHECK(result.completedUnits <= result.totalUnits);
        CHECK(result.processDetected);
        if (fixture.startGame)
        {
            CHECK(packageOperations.startCount == 1);
            CHECK(context.taps.empty());
        }
        else
        {
            CHECK_FALSE(context.taps.empty());
            CHECK(std::any_of(context.emittedEvents.begin(), context.emittedEvents.end(),
                [](TaskExecutionEvent const& event) {
                    return event.type == TaskEventType::TaskCompleted;
                }));
            if (fixture.taskId != "daily-race" && fixture.taskId != "team-race")
                CHECK(result.completedUnits == 1);
        }
        if (!fixture.terminalTask.empty())
        {
            CHECK(std::any_of(context.emittedEvents.begin(), context.emittedEvents.end(),
                [&fixture](TaskExecutionEvent const& event) {
                    return event.type == TaskEventType::TaskCompleted
                        && event.taskName == fixture.terminalTask;
                }));
        }
        if (fixture.taskId == "mail-collection")
            CHECK(hasCompletedInOrder(context.emittedEvents, {"giftBox", "collectAll", "rewardClose", "homeVerify"}));
        else if (fixture.taskId == "mission-collection")
            CHECK(hasCompletedInOrder(context.emittedEvents,
                {"missionIcon", "dailyCollectAll", "mainCollectAll", "titlesCollectAll", "specialCollectAll", "homeVerify"}));
        else if (fixture.taskId == "daily-race")
            CHECK(hasCompletedInOrder(context.emittedEvents,
                {"raceStart", "runnerSelectHighest", "runnerConfirm", "itemsRace", "finalNext", "complete"}));
        else if (fixture.taskId == "team-race")
            CHECK(hasCompletedInOrder(context.emittedEvents, {"teamTrials", "opponent", "itemRace", "complete"}));
        else if (fixture.taskId == "shop-purchase")
            CHECK(hasCompletedInOrder(context.emittedEvents,
                {"specialShop", "dailySales", "shopSelectAll", "shopExchangeComplete", "homeVerify"}));
    }
}

TEST_CASE("Daily runner selector verifies the requested trainee on a 450 by 800 frame", "[game-tasks]")
{
    auto const root = ResourceRoot();
    Resources::ResourceSnapshot resources(root);
    GameData::UmaGameDatabase database(root);
    std::string error;
    REQUIRE(database.Load(error));
    auto const* trainee = database.TryGetTrainee(100101);
    REQUIRE(trainee != nullptr);
    auto const imagePaths = database.GetTraineeImagePaths(*trainee);
    REQUIRE_FALSE(imagePaths.empty());
    auto const prepared = resources.load_template(
        Paths::ToUtf8(imagePaths.front()), Resources::TemplatePreparation::original);
    REQUIRE(prepared != nullptr);

    auto makeScreen = [&prepared]() {
        std::vector<std::uint8_t> pixels(450U * 800U, 28);
        auto const card = Resources::resize_nearest(prepared->image, 79, 82);
        auto const detail = Resources::resize_nearest(prepared->image, 99, 103);
        auto paste = [&pixels](Vision::GrayImage const& image, int left, int top) {
            for (int y = 0; y < image.height; ++y)
                std::copy_n(image.gray.begin() + static_cast<std::ptrdiff_t>(y) * image.width,
                    image.width,
                    pixels.begin() + static_cast<std::ptrdiff_t>(top + y) * 450 + left);
        };
        paste(card, 10, 400);
        paste(detail, 10, 90);
        return pixels;
    };

    HachimiPipelineDefinition definition;
    definition.baseDirectory = Paths::ToUtf8(root / "hachimi/pipelines");
    MonitorContext context;
    context.frameWidth = 450;
    context.frameHeight = 800;
    context.frameGrayPixels = makeScreen();
    context.matcher = [](std::string const& path, std::uint64_t) {
        if (path.find("runner_sort_asc.png") != std::string::npos) return TaskMatchResult{};
        if (path.find("runner_sort_desc.png") != std::string::npos) return HitAt(710);
        return HitAt(100);
    };

    auto const selected = GameTasks::DailyRaceRunnerSelector::Select(
        context, definition, root, 100101, &resources);
    INFO(selected.message);
    CHECK(selected.succeeded);
    CHECK(selected.cardScore >= 0.70);
    CHECK(selected.detailScore >= 0.70);
    auto const cardTap = std::find_if(context.matchTaps.begin(), context.matchTaps.end(), [](auto const& tap) {
        return tap.match.width == 160 && tap.match.height == 190;
    });
    REQUIRE(cardTap != context.matchTaps.end());
    CHECK(cardTap->x == 100);
    CHECK(cardTap->y == 895);
    CHECK(cardTap->match.x == 20);
    CHECK(cardTap->match.y == 800);
    CHECK(cardTap->match.referenceWidth == 900);
    CHECK(cardTap->match.referenceHeight == 1600);
    CHECK(cardTap->match.frameId > 0);
    REQUIRE(context.currentFrame != nullptr);
    CHECK(context.currentFrame->width == 450);
    CHECK(context.currentFrame->height == 800);

    SECTION("missing card evidence never selects a neighboring runner")
    {
        MonitorContext emptyContext;
        emptyContext.frameWidth = 1;
        emptyContext.frameHeight = 1;
        emptyContext.frameGrayPixels.assign(1, 28);
        emptyContext.matcher = context.matcher;
        auto const missing = GameTasks::DailyRaceRunnerSelector::Select(
            emptyContext, definition, root, 100101, &resources);
        CHECK_FALSE(missing.succeeded);
        CHECK(std::none_of(emptyContext.matchTaps.begin(), emptyContext.matchTaps.end(), [](auto const& tap) {
            return tap.match.width == 160 && tap.match.height == 190;
        }));
        CHECK_FALSE(emptyContext.swipes.empty());
    }
}

TEST_CASE("Date-change recovery retries work interrupted before input but never repeats an uncertain purchase", "[game-tasks]")
{
    auto const root = ResourceRoot();
    auto const stamp = std::chrono::steady_clock::now().time_since_epoch().count();
    auto const directory = std::filesystem::temp_directory_path()
        / ("uma-game-task-recovery-" + std::to_string(stamp));
    std::filesystem::create_directories(directory);
    auto const definitionPath = directory / "recovery_fixture.json";
    auto const homeTemplate = Paths::ToUtf8(root / "hachimi/pipelines/templates/start_game/game_home_selected.png");
    auto const confirmTemplate = Paths::ToUtf8(root / "hachimi/pipelines/templates/shop/exchange_confirm.png");
    auto const completeTemplate = Paths::ToUtf8(root / "hachimi/pipelines/templates/shop/exchange_complete.png");
    nlohmann::json definition = {
        {"name", "recovery-fixture"},
        {"schemaVersion", 1},
        {"referenceWidth", 900},
        {"referenceHeight", 1600},
        {"tasks", {
            {"home", {{"algorithm", "MatchTemplate"}, {"action", "DoNothing"},
                {"template", homeTemplate}, {"timeoutMs", 0}, {"next", {"shopExchangeConfirm"}}}},
            {"shopExchangeConfirm", {{"algorithm", "MatchTemplate"}, {"action", "ClickSelf"},
                {"template", confirmTemplate}, {"timeoutMs", 0}, {"next", {"shopExchangeComplete"}}}},
            {"shopExchangeComplete", {{"algorithm", "MatchTemplate"}, {"action", "DoNothing"},
                {"template", completeTemplate}, {"timeoutMs", 0}, {"success", true}}}
        }}
    };
    {
        std::ofstream output(definitionPath, std::ios::binary);
        REQUIRE(output.good());
        output << definition.dump(2);
    }

    auto execute = [&](bool interruptBeforeInput, bool interruptDuringDelay = false,
                       bool transientDuringDelay = false, bool sessionError = false, int retainedMarker = -1) {
        definition["tasks"]["shopExchangeConfirm"]["preDelay"] = interruptDuringDelay ? 3500 : 0;
        {
            std::ofstream output(definitionPath, std::ios::binary);
            REQUIRE(output.good());
            output << definition.dump(2);
        }
        MonitorContext context;
        auto dialogVisible = [interruptBeforeInput, transientDuringDelay, sessionError](std::uint64_t frame) {
            if (transientDuringDelay) return false;
            if (sessionError) return frame == (interruptBeforeInput ? 3U : 4U);
            if (interruptBeforeInput) return frame == 3 || frame == 4;
            return frame == 4 || frame == 5;
        };
        auto modal = Resources::WicImageDecoder::decode_file(sessionError
            ? root.parent_path() / "testdata/hachimi/ura/captures/session_error_inactivity_mumu_20261014.png"
            : root / "hachimi/common/date_changed/sample.png");
        if (sessionError && retainedMarker >= 0)
        {
            constexpr std::array markerRois{std::array{320, 475, 260, 80},
                std::array{140, 730, 620, 100}, std::array{320, 1000, 260, 90}};
            for (int marker = 0; marker < 3; ++marker)
            {
                if (marker == retainedMarker) continue;
                auto const roi = markerRois[marker];
                for (int y = roi[1]; y < roi[1] + roi[3]; ++y)
                    for (int x = roi[0]; x < roi[0] + roi[2]; ++x)
                    {
                        auto const offset = static_cast<std::size_t>(y * modal.width + x);
                        modal.gray[offset] = 248;
                        for (std::size_t channel = 0; channel < 3; ++channel) modal.rgba[offset * 4 + channel] = 248;
                    }
            }
        }
        if (interruptDuringDelay)
        {
            context.inspectionFrame = [&]() -> std::shared_ptr<const TaskFrameSnapshot> {
                if (context.frameId != 2 || (transientDuringDelay && context.inspectionCaptureCount > 1)) return {};
                auto const selected = context.GetCurrentFrame();
                auto frame = std::make_shared<TaskFrameSnapshot>();
                frame->width = modal.width;
                frame->height = modal.height;
                frame->grayPixels = modal.gray;
                frame->rgbaPixels = modal.rgba;
                // Inspection does not replace the pending input's source.
                CHECK(context.GetCurrentFrame() == selected);
                return frame;
            };
        }
        context.onCapture = [&] {
            if (dialogVisible(context.frameId))
            {
                context.frameGrayPixels = modal.gray;
                context.frameRgbaPixels = modal.rgba;
            }
            else
            {
                context.frameGrayPixels.clear();
                context.frameRgbaPixels.clear();
            }
        };
        context.matcher = [](std::string const&, std::uint64_t) {
            return HitAt(100);
        };
        Resources::ResourceSnapshot resources(root);
        FakePackageOperations packageOperations;
        nlohmann::json settings = {
            {"definitionPath", Paths::ToUtf8(std::filesystem::absolute(definitionPath))},
            {"packageId", "com.example.game"}, {"activityName", ""}
        };
        auto const result = GameTasks::NativeGameTaskRunner::Run(
            "mail-collection", settings, context, root, &packageOperations, &resources);
        if (interruptDuringDelay) CHECK(context.inspectionCaptureCount > 0);
        std::size_t confirmationTaps = 0;
        for (auto const& [x, _] : context.taps)
            if (x == 115) ++confirmationTaps;
        if (sessionError)
        {
            CHECK(std::count_if(context.matchTaps.begin(), context.matchTaps.end(), [](auto const& tap) {
                return tap.x >= 441 && tap.x <= 459 && tap.y >= 1035 && tap.y <= 1055;
            }) == 1);
        }
        return std::tuple{result, confirmationTaps, packageOperations.stopCount, packageOperations.startCount};
    };

    auto const [preInput, preInputTaps, preStops, preStarts] = execute(true);
    CHECK(preInput.succeeded);
    CHECK(preInputTaps == 1);
    CHECK(preStops == 1);
    CHECK(preStarts == 1);

    auto const [postInput, postInputTaps, postStops, postStarts] = execute(false);
    CHECK_FALSE(postInput.succeeded);
    CHECK(postInput.message.find("could not be confirmed") != std::string::npos);
    CHECK(postInputTaps == 1);
    CHECK(postStops == 1);
    CHECK(postStarts == 1);

    auto const [duringDelay, delayTaps, delayStops, delayStarts] = execute(true, true);
    CHECK(duringDelay.succeeded);
    CHECK(delayTaps == 1);
    CHECK(delayStops == 1);
    CHECK(delayStarts == 1);
    auto const [transient, transientTaps, transientStops, transientStarts] = execute(true, true, true);
    CHECK(transient.succeeded);
    CHECK(transientTaps == 1);
    CHECK(transientStops == 0);
    CHECK(transientStarts == 0);
    auto const [session, sessionTaps, sessionStops, sessionStarts] = execute(true, false, false, true);
    CHECK(session.succeeded);
    CHECK(sessionTaps == 1);
    CHECK(sessionStops == 1);
    CHECK(sessionStarts == 1);
    auto const [uncertainSession, uncertainTaps, uncertainStops, uncertainStarts] = execute(false, false, false, true);
    CHECK_FALSE(uncertainSession.succeeded);
    CHECK(uncertainSession.message.find("could not be confirmed") != std::string::npos);
    CHECK(uncertainTaps == 1);
    CHECK(uncertainStops == 1);
    CHECK(uncertainStarts == 1);
    for (int marker = 0; marker < 3; ++marker)
    {
        CAPTURE(marker);
        auto const [single, singleTaps, singleStops, singleStarts] = execute(true, false, false, true, marker);
        CHECK(single.succeeded);
        CHECK(singleTaps == 1);
        CHECK(singleStops == 1);
        CHECK(singleStarts == 1);
    }
    auto const [sessionDelay, sessionDelayTaps, sessionDelayStops, sessionDelayStarts] = execute(true, true, false, true);
    CHECK(sessionDelay.succeeded);
    CHECK(sessionDelayTaps == 1);
    CHECK(sessionDelayStops == 1);
    CHECK(sessionDelayStarts == 1);
    std::filesystem::remove_all(directory);
}

TEST_CASE("Standalone startup dismisses repeated resets and restarts interrupted input recognition", "[game-tasks][GameRecovery]")
{
    auto const root = ResourceRoot();
    auto const modal = Resources::WicImageDecoder::decode_file(root / "hachimi/common/date_changed/sample.png");
    for (bool beforeInput : {false, true})
    {
        CAPTURE(beforeInput);
        MonitorContext context;
        context.onCapture = [&] {
            bool const visible = beforeInput
                ? context.frameId == 2 || context.frameId == 3
                : context.frameId == 1 || context.frameId == 2 || context.frameId == 4 || context.frameId == 5;
            context.frameGrayPixels = visible ? modal.gray : std::vector<std::uint8_t>{};
            context.frameRgbaPixels = visible ? modal.rgba : std::vector<std::uint8_t>{};
        };
        context.matcher = [beforeInput](std::string const& path, std::uint64_t frame) {
            if (beforeInput && frame == 1 && path.find("startnotice_skip.png") != std::string::npos)
                return HitAt(100);
            if (frame >= 3 && path.find("game_home_selected.png") != std::string::npos)
                return HitAt(300);
            return TaskMatchResult{};
        };
        Resources::ResourceSnapshot resources(root);
        FakePackageOperations packages;
        auto const result = GameTasks::NativeGameTaskRunner::Run("start-game",
            nlohmann::json{{"packageId", "com.example.game"}}, context, root, &packages, &resources);
        REQUIRE(result.succeeded);
        CHECK(packages.startCount == 1);
        CHECK(packages.stopCount == 0);
        CHECK(context.matchTaps.size() == (beforeInput ? 1U : 2U));
        for (auto const& tap : context.matchTaps)
        {
            CHECK(tap.x >= 441);
            CHECK(tap.x <= 459);
            CHECK(tap.y >= 1024);
            CHECK(tap.y <= 1072);
        }
    }
}

TEST_CASE("Failed preflight recovery and launcher failures return without replay or input", "[game-tasks]")
{
    SECTION("missing recovery references fail once before any input")
    {
        auto const stamp = std::chrono::steady_clock::now().time_since_epoch().count();
        auto const directory = std::filesystem::temp_directory_path()
            / ("uma-game-task-missing-recovery-" + std::to_string(stamp));
        std::filesystem::create_directories(directory);
        auto const definitionPath = directory / "fixture.json";
        auto const missingResourcesPath = directory / "resources";
        std::filesystem::create_directories(missingResourcesPath);
        auto const homeTemplate = Paths::ToUtf8(
            ResourceRoot() / "hachimi/pipelines/templates/start_game/game_home_selected.png");
        nlohmann::json definition = {
            {"name", "missing-recovery-fixture"}, {"schemaVersion", 1},
            {"referenceWidth", 900}, {"referenceHeight", 1600},
            {"tasks", {{"home", {
                {"algorithm", "MatchTemplate"}, {"action", "ClickSelf"},
                {"template", homeTemplate}, {"timeoutMs", 0}, {"success", true}
            }}}}
        };
        {
            std::ofstream output(definitionPath, std::ios::binary);
            REQUIRE(output.good());
            output << definition.dump(2);
        }

        MonitorContext context;
        context.matcher = [](std::string const&, std::uint64_t) { return HitAt(100); };
        Resources::ResourceSnapshot missingResources(missingResourcesPath);
        FakePackageOperations packageOperations;
        auto const result = GameTasks::NativeGameTaskRunner::Run(
            "mail-collection",
            nlohmann::json{
                {"definitionPath", Paths::ToUtf8(std::filesystem::absolute(definitionPath))},
                {"packageId", "com.example.game"}, {"activityName", ""}},
            context, missingResourcesPath, &packageOperations, &missingResources);

        CHECK_FALSE(result.succeeded);
        CHECK(result.message.find("references could not be loaded") != std::string::npos);
        CHECK(packageOperations.startCount == 0);
        CHECK(packageOperations.stopCount == 0);
        CHECK(context.taps.empty());
        auto const homeMatches = std::count_if(context.matchRequests.begin(), context.matchRequests.end(),
            [&homeTemplate](std::string const& path) { return path == homeTemplate; });
        CHECK(homeMatches == 0);
        CHECK(context.captureCount == 1);
        std::filesystem::remove_all(directory);
    }

    SECTION("career-training is supported and validated")
    {
        CHECK(GameTasks::NativeGameTaskRunner::SupportsTask("career-training"));
        std::string error;
        CHECK(GameTasks::NativeGameTaskRunner::ValidateSettings(
            "career-training", nlohmann::json{{"strategyId", "smart"}}, error));
        CHECK(error.empty());

        MonitorContext context;
        context.captureResult = true;
        // Run career-training with maxTurns=1
        auto const result = GameTasks::NativeGameTaskRunner::Run(
            "career-training", nlohmann::json{{"strategyId", "smart"}, {"maxTurns", 1}},
            context, ResourceRoot());
        CHECK(result.taskId == "career-training");
        CHECK(result.totalUnits == 1);
    }
}
