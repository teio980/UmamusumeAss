#include <catch2/catch_test_macros.hpp>
#include "UmaAssistantCore/Career/CareerFlowCoordinator.hpp"
#include "UmaAssistantCore/Career/CareerPersistenceStore.hpp"
#include "UmaAssistantCore/Career/CareerObservationReader.hpp"
#include "UmaAssistantCore/CareerDomain/Scenarios/Ura/UraScenarioModule.hpp"
#include "UmaAssistantCore/CareerDomain/Scenarios/Ura/UraSmartTrainingStrategy.hpp"
#include "UmaAssistantCore/CareerDomain/TrainingStrategies.hpp"
#include "MockCareerComponents.hpp"
#include "tests/TaskEngine/MockTaskExecutionContext.hpp"
#include <chrono>
#include <fstream>
#include <functional>

using namespace UmaAssistant::Career;
using namespace UmaAssistant::Career::Tests;
using namespace UmaAssistant::CareerDomain;

namespace
{
struct TempStore
{
    std::filesystem::path path = std::filesystem::temp_directory_path()
        / ("uma-behavior-test-" + std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()));
    TempStore() { std::filesystem::create_directories(path); }
    ~TempStore() { std::error_code error; std::filesystem::remove_all(path, error); }
};
class Context : public UmaAssistant::TaskEngine::Tests::MockTaskExecutionContext
{
public:
    std::string GetDeviceSerial() const override { return "behavior-test-device"; }
};
class Observer : public MockCareerObserver
{
public:
    void SetGoalCompletionProbeArmed(bool value) noexcept override { armed = value; }
    void SetRaceReplayFlowCompleted(bool value) noexcept override { replayCompleted = value; }
    void SetRaceRunnerResultExpected(bool value) noexcept override { runnerResultExpected = value; }
    bool armed{false};
    bool replayCompleted{false};
    bool runnerResultExpected{false};
};
class Executor : public MockCareerActionExecutor
{
public:
    ActionExecutionStatus Execute(const ActionIntent& intent, UmaAssistant::TaskEngine::ITaskExecutionContext& context) override
    {
        if (beforeExecute) beforeExecute();
        return MockCareerActionExecutor::Execute(intent, context);
    }
    bool ConfirmCompletion(const ActionIntent& intent, UmaAssistant::TaskEngine::ITaskExecutionContext& context) override
    {
        if (beforeConfirm) beforeConfirm();
        return MockCareerActionExecutor::ConfirmCompletion(intent, context);
    }
    std::optional<std::string> TakeLastActionOutcome() override { return std::exchange(outcome, {}); }
    void SetGoalObservationPolicy(bool armed, bool completed) noexcept override
    {
        policyArmed = armed;
        policyReplayCompleted = completed;
    }
    void SetRaceRunnerResultExpected(bool value) noexcept override { runnerResultExpected = value; }
    std::function<void()> beforeConfirm;
    std::function<void()> beforeExecute;
    std::optional<std::string> outcome;
    bool policyArmed{false};
    bool policyReplayCompleted{false};
    bool runnerResultExpected{false};
};
class Flow : public ICareerTrainingFlow
{
public:
    bool HasCandidatesForCurrentTurn(const CareerState&) const noexcept override { return false; }
    CareerTrainingSelectionResult Select(UmaAssistant::TaskEngine::ITaskExecutionContext&,
        const CareerState&, const IScenarioModule&, ICareerStrategy&) override
    {
        CareerTrainingSelectionResult result;
        result.status = CareerTrainingSelectionStatus::Ready;
        result.intent = ActionIntent::MakeTraining("speed");
        return result;
    }
    bool HasPendingConfirmedTap() const noexcept override { return !pending; }
    bool HasPendingOutcome() const noexcept override { return pending; }
    bool MarkTrainingTapSent() noexcept override { pending = true; return true; }
    std::optional<std::string> ObserveOutcome(const CareerObservation&) override { return {}; }
    void Reset() noexcept override { pending = false; }
    bool pending{false};
};
CareerObservation Screen(const std::string& id, CareerScreenKind kind)
{
    CareerObservation observation;
    observation.screenId = id;
    observation.screenKind = kind;
    observation.score = 0.96;
    return observation;
}
CareerObservation Main(int countdown = 3)
{
    auto observation = Screen("career_main", CareerScreenKind::MainTurn);
    observation.energyPercent = ObservedValue<int>::Make(90);
    observation.moodEvidence = ObservedValue<std::string>::Make("Great");
    observation.turnIndexEvidence = ObservedValue<int>::Make(0);
    observation.turnPositionText = "Finale Underway";
    observation.turnsToGoalEvidence = ObservedValue<int>::Make(countdown);
    return observation;
}
}

TEST_CASE("Career persistence replaces existing records and rejects ambiguous pending guards", "[CareerBehavior]")
{
    TempStore temporary;
    SECTION("Skills can be saved more than once on Windows")
    {
        NormalCareerSkillCache::Save(temporary.path, 1001, "device", {1});
        NormalCareerSkillCache::Save(temporary.path, 1001, "device", {1, 2});
        CHECK(NormalCareerSkillCache::Load(temporary.path, 1001, "device") == std::vector<int>{1, 2});
        CHECK(NormalCareerSkillCache::Load(temporary.path, 1001, "another-device").empty());
    }
    SECTION("Pending guard can be replaced")
    {
        SmartTrainingConfirmationStore::Save(temporary.path, 1001, "device", "speed", 1);
        SmartTrainingConfirmationStore::Save(temporary.path, 1001, "device", "stamina", 2);
        const auto record = SmartTrainingConfirmationStore::Load(temporary.path, 1001, "device");
        REQUIRE(record);
        CHECK(record->trainingType == "stamina");
        CHECK(record->turnIndex == 2);
    }
    SECTION("Unreadable guard cannot mean no pending input")
    {
        std::ofstream(SmartTrainingConfirmationStore::GetPath(temporary.path, 1001, "device")) << "{broken";
        REQUIRE_THROWS(SmartTrainingConfirmationStore::Load(temporary.path, 1001, "device"));
    }
}

TEST_CASE("Career loads pending smart input before startup hands off a runtime observation", "[CareerBehavior]")
{
    TempStore temporary;
    Context context;
    CareerRuntimeConfig config;
    config.traineeId = 1001;
    config.checkpointDirectory = temporary.path;
    config.continueExistingCareer = true;
    auto observer = std::make_shared<Observer>();
    auto executor = std::make_shared<Executor>();
    SmartTrainingConfirmationStore::Save(temporary.path, 1001, context.GetDeviceSerial(), "speed", 0);
    CareerFlowCoordinator coordinator(observer, executor, std::make_shared<UraScenarioModule>(),
        std::make_shared<UraSmartTrainingStrategy>(), config);
    coordinator.SetConfirmedObservation(Screen("training_selection", CareerScreenKind::TrainingSelect));
    const auto result = coordinator.Step(context);
    CHECK(result.status == CareerRunStatus::Failed);
    CHECK(result.failureReason.find("confirmation guard") != std::string::npos);
    CHECK(executor->executedIntents.empty());
}

TEST_CASE("Finale skipped training results commit the ratio once from countdown evidence", "[CareerBehavior]")
{
    auto observer = std::make_shared<Observer>();
    auto executor = std::make_shared<Executor>();
    auto strategy = std::make_shared<RatioTrainingStrategy>(TrainingRatio{1, 1, 0, 0, 0});
    observer->observationSequence = {Main(), Screen("training_selection", CareerScreenKind::TrainingSelect), Main(2)};
    CareerFlowCoordinator coordinator(observer, executor, std::make_shared<UraScenarioModule>(), strategy);
    Context context;
    for (int step = 0; step < 3; ++step) REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
    REQUIRE(executor->executedIntents.size() == 3);
    CHECK(executor->executedIntents[1].trainingType == "speed");
    CHECK(executor->executedIntents[2].trainingType == "stamina");
}

TEST_CASE("Goal eligibility is armed before observing a submitted turn destination", "[CareerBehavior]")
{
    auto observer = std::make_shared<Observer>();
    auto executor = std::make_shared<Executor>();
    observer->observationSequence = {Screen("rest_confirmation", CareerScreenKind::Rest)};
    executor->beforeConfirm = [&] { CHECK(observer->armed); CHECK(executor->policyArmed); };
    CareerFlowCoordinator coordinator(observer, executor, std::make_shared<UraScenarioModule>(),
        std::make_shared<DefaultTrainingStrategy>());
    Context context;
    CHECK(coordinator.Step(context).status == CareerRunStatus::Running);
}

TEST_CASE("Race replay outcome enables goal recognition before destination polling", "[CareerBehavior]")
{
    auto observer = std::make_shared<Observer>();
    auto executor = std::make_shared<Executor>();
    observer->observationSequence = {Screen("race_day", CareerScreenKind::RaceEntry),
        Screen("race_runner", CareerScreenKind::RaceExecution)};
    CareerFlowCoordinator coordinator(observer, executor, std::make_shared<UraScenarioModule>(),
        std::make_shared<DefaultTrainingStrategy>());
    Context context;
    REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
    executor->outcome = "race.replay.completed";
    executor->beforeConfirm = [&] {
        CHECK(observer->replayCompleted);
        CHECK(observer->armed);
        CHECK(executor->policyReplayCompleted);
        CHECK(executor->policyArmed);
    };
    CHECK(coordinator.Step(context).status == CareerRunStatus::Running);
}

TEST_CASE("Runner result policy reaches both observers only during a configured pending race", "[CareerBehavior][CareerRaceTransition]")
{
    auto observer = std::make_shared<Observer>();
    auto executor = std::make_shared<Executor>();
    observer->observationSequence = {Screen("race_day", CareerScreenKind::RaceEntry),
        Screen("race_runner", CareerScreenKind::RaceExecution),
        Screen("race_runner_result", CareerScreenKind::RaceResult)};
    CareerFlowCoordinator coordinator(observer, executor, std::make_shared<UraScenarioModule>(),
        std::make_shared<DefaultTrainingStrategy>());
    Context context;
    executor->beforeConfirm = [&] {
        CHECK_FALSE(observer->runnerResultExpected);
        CHECK_FALSE(executor->runnerResultExpected);
    };
    REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
    executor->beforeConfirm = [&] {
        CHECK(observer->runnerResultExpected);
        CHECK(executor->runnerResultExpected);
    };
    REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
    executor->beforeConfirm = [&] {
        CHECK_FALSE(observer->runnerResultExpected);
        CHECK_FALSE(executor->runnerResultExpected);
    };
    REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
}

TEST_CASE("Restored story settlement completes only after Home", "[CareerBehavior]")
{
    auto observer = std::make_shared<Observer>();
    auto executor = std::make_shared<Executor>();
    observer->observationSequence = {Screen("career_story_unlocked_to_home", CareerScreenKind::Settlement),
        Screen("home", CareerScreenKind::Unknown)};
    CareerFlowCoordinator coordinator(observer, executor, std::make_shared<UraScenarioModule>(),
        std::make_shared<DefaultTrainingStrategy>());
    Context context;
    REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
    CHECK(coordinator.Step(context).status == CareerRunStatus::Completed);
}

TEST_CASE("Settlement selects the unselected Home tab only after a confirmed terminal page", "[CareerBehavior][CareerSettlementAudit]")
{
    auto observer = std::make_shared<Observer>();
    auto executor = std::make_shared<Executor>();
    CareerFlowCoordinator coordinator(observer, executor, std::make_shared<UraScenarioModule>(),
        std::make_shared<DefaultTrainingStrategy>());
    Context context;
    SECTION("A terminal page followed by an unselected Home tab completes after selection")
    {
        observer->observationSequence = {Screen("career_complete", CareerScreenKind::Settlement),
            Screen("home_unselected", CareerScreenKind::Unknown), Screen("home", CareerScreenKind::Unknown)};
        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
        REQUIRE(executor->executedIntents.size() == 2);
        CHECK(executor->executedIntents.back().customActionName == "Advance");
        CHECK(coordinator.Step(context).status == CareerRunStatus::Completed);
    }
    SECTION("An unselected Home tab alone cannot prove Career completion")
    {
        observer->observationSequence = {Screen("home_unselected", CareerScreenKind::Unknown)};
        CHECK(coordinator.Step(context).status == CareerRunStatus::Failed);
        CHECK(executor->executedIntents.empty());
    }
}

TEST_CASE("A readable Finale goal releases post-race waiting without a calendar change", "[CareerBehavior]")
{
    auto observer = std::make_shared<Observer>();
    auto executor = std::make_shared<Executor>();
    auto next = Main(0);
    next.turnIndexEvidence = ObservedValue<int>::Make(72);
    next.goalTextEvidence = ObservedValue<std::string>::Make("Participate in the URA Finale");
    observer->observationSequence = {Screen("goal_complete", CareerScreenKind::GoalUpdate),
        Screen("race_day", CareerScreenKind::RaceEntry), Screen("race_runner", CareerScreenKind::RaceExecution),
        Screen("reward_support", CareerScreenKind::RaceResult), next};
    CareerFlowCoordinator coordinator(observer, executor, std::make_shared<UraScenarioModule>(),
        std::make_shared<DefaultTrainingStrategy>());
    Context context;
    REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
    REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
    executor->outcome = "race.replay.completed";
    REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
    REQUIRE(observer->armed);
    REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
    REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
    REQUIRE(executor->executedIntents.size() == 5);
    CHECK(executor->executedIntents.back().kind == ActionIntentKind::Race);
    CHECK(executor->executedIntents.back().raceTarget == "finale");
}

TEST_CASE("Main text cache follows reference coordinates and color evidence", "[CareerBehavior]")
{
    CareerMainTextCache cache;
    UmaAssistant::TaskEngine::TaskFrameSnapshot frame;
    frame.width = 450;
    frame.height = 800;
    frame.grayPixels.assign(450 * 800, 20);
    frame.rgbaPixels.assign(450 * 800 * 4, 30);
    const UmaAssistant::TaskEngine::TaskRoi roi{600, 100, 100, 100};
    cache.Remember("goal", frame, roi, "Run in a race");
    std::string text;
    REQUIRE(cache.TryGet("goal", frame, roi, text));
    frame.grayPixels[75 * 450 + 325] = 40;
    CHECK_FALSE(cache.TryGet("goal", frame, roi, text));
    frame.grayPixels[75 * 450 + 325] = 20;
    cache.Remember("goal", frame, roi, "Run in a race", 0.63);
    double confidence = 0.0;
    REQUIRE(cache.TryGet("goal", frame, roi, text, &confidence));
    CHECK(confidence == 0.63);
    frame.rgbaPixels[(75 * 450 + 325) * 4] = 80;
    CHECK_FALSE(cache.TryGet("goal", frame, roi, text));
    cache.Remember("goal", frame, roi, "Run in a race");
    ++frame.sessionGeneration;
    CHECK_FALSE(cache.TryGet("goal", frame, roi, text));
}

TEST_CASE("Smart training guard exists before input and blocks input when storage fails", "[CareerBehavior]")
{
    TempStore temporary;
    Context context;
    CareerRuntimeConfig config;
    config.traineeId = 1001;
    config.checkpointDirectory = temporary.path;
    config.continueExistingCareer = false;
    auto observer = std::make_shared<Observer>();
    auto executor = std::make_shared<Executor>();
    observer->observationSequence = {Screen("training_selection", CareerScreenKind::TrainingSelect)};
    SECTION("Cancellation preserves the guard that was written before input")
    {
        executor->beforeExecute = [&] {
            const auto record = SmartTrainingConfirmationStore::Load(temporary.path, 1001, context.GetDeviceSerial());
            REQUIRE(record);
            CHECK(record->trainingType == "speed");
        };
        executor->returnStatus = ActionExecutionStatus::Cancelled;
        CareerFlowCoordinator coordinator(observer, executor, std::make_shared<UraScenarioModule>(),
            std::make_shared<UraSmartTrainingStrategy>(), config, std::make_shared<Flow>());
        CHECK(coordinator.Step(context).status == CareerRunStatus::Cancelled);
        CHECK(SmartTrainingConfirmationStore::Load(temporary.path, 1001, context.GetDeviceSerial()).has_value());
    }
    SECTION("A failed guard write sends no input")
    {
        config.checkpointDirectory = temporary.path / "blocked";
        std::ofstream(config.checkpointDirectory) << "not a directory";
        CareerFlowCoordinator coordinator(observer, executor, std::make_shared<UraScenarioModule>(),
            std::make_shared<UraSmartTrainingStrategy>(), config, std::make_shared<Flow>());
        const auto result = coordinator.Step(context);
        CHECK(result.status == CareerRunStatus::Failed);
        CHECK(result.failureReason.find("saved before input") != std::string::npos);
        CHECK(executor->executedIntents.empty());
    }
}

TEST_CASE("Main goal classification is refreshed and infirmary wins across configured strategies", "[CareerBehavior]")
{
    UraScenarioModule scenario;
    CareerState state;
    scenario.OnRunStart(state);
    auto observation = Main();
    observation.goalTextEvidence = ObservedValue<std::string>::Make("Earn 5,000 fans to go");
    observation.fansToGoalEvidence = ObservedValue<int>::Make(5000);
    observation.infirmaryEvidence = ObservedValue<bool>::Make(true);
    scenario.OnObserve(state, observation);
    REQUIRE(state.observedGoalKind == "fans");
    DefaultTrainingStrategy normal;
    RatioTrainingStrategy ratio({1, 1, 0, 0, 0});
    WitRatioTrainingStrategy wit({1, 1, 0, 0, 0});
    UraSmartTrainingStrategy smart;
    CHECK(normal.Choose(state, &scenario).kind == ActionIntentKind::Infirmary);
    CHECK(ratio.Choose(state, &scenario).kind == ActionIntentKind::Infirmary);
    CHECK(wit.Choose(state, &scenario).kind == ActionIntentKind::Infirmary);
    CHECK(smart.Choose(state, &scenario).kind == ActionIntentKind::Infirmary);
    observation.goalTextEvidence = ObservedValue<std::string>::Unknown();
    observation.fansToGoalEvidence = ObservedValue<int>::Unknown();
    scenario.OnObserve(state, observation);
    CHECK(state.observedGoalKind.empty());
}

TEST_CASE("Resumed optional races remain guarded while Race Day resumes a required race", "[CareerBehavior]")
{
    auto observer = std::make_shared<Observer>();
    auto executor = std::make_shared<Executor>();
    CareerRuntimeConfig config;
    config.continueExistingCareer = true;
    CareerFlowCoordinator coordinator(observer, executor, std::make_shared<UraScenarioModule>(),
        std::make_shared<DefaultTrainingStrategy>(), config);
    Context context;
    SECTION("Direct Race List resume cannot assume a clean race streak")
    {
        observer->observationSequence = {Screen("race_list", CareerScreenKind::RaceEntry)};
        CHECK(coordinator.Step(context).status == CareerRunStatus::Failed);
        CHECK(executor->executedIntents.empty());
    }
    SECTION("Required Race Day resumes through Race List with a target")
    {
        observer->observationSequence = {Screen("race_day", CareerScreenKind::RaceEntry),
            Screen("race_list", CareerScreenKind::RaceEntry)};
        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
        CHECK(executor->executedIntents.back().raceTarget == "race");
    }
}

TEST_CASE("The mandatory Race List commits the final training once and resets its non-race streak",
    "[CareerBehavior][CareerTrainingRaceHandoff]")
{
    class TrackingStrategy : public DefaultTrainingStrategy
    {
    public:
        TrackingStrategy() : DefaultTrainingStrategy("guts") {}
        bool RequiresTrainingCandidateScan() const noexcept override { return scan; }
        void ConfirmTraining(CareerState&, const std::string& type) override { committed.push_back(type); }
        bool scan{false};
        std::vector<std::string> committed;
    };
    TempStore temporary;
    Context context;
    auto observer = std::make_shared<Observer>();
    auto executor = std::make_shared<Executor>();
    auto strategy = std::make_shared<TrackingStrategy>();
    auto scenario = std::make_shared<UraScenarioModule>();
    CareerRuntimeConfig config;
    config.continueExistingCareer = false;
    auto raceList = Screen("race_list", CareerScreenKind::RaceEntry);
    raceList.goalTextEvidence = ObservedValue<std::string>::Make("Run in Junior Make Debut");
    raceList.turnsToGoalEvidence = ObservedValue<int>::Make(0);
    SECTION("Live final training skips directly into Race List")
    {
        auto main = Main(1);
        main.turnPositionText = "Junior Year Pre-Debut";
        main.goalTextEvidence = raceList.goalTextEvidence;
        observer->observationSequence = {main, Screen("training_selection", CareerScreenKind::TrainingSelect),
            raceList, raceList};
        CareerFlowCoordinator coordinator(observer, executor, scenario, strategy, config);
        for (int step = 0; step < 4; ++step) REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
        CHECK(strategy->committed == std::vector<std::string>{"guts"});
        CHECK(coordinator.GetState().confirmedTurnActions == 1);
        CHECK(coordinator.GetState().consecutiveRaceTurns == 0);
        CHECK(executor->executedIntents.back().kind == ActionIntentKind::Race);
    }
    SECTION("A resumed pending training is reconciled at the mandatory Race List")
    {
        config.continueExistingCareer = true;
        config.traineeId = 1001;
        config.checkpointDirectory = temporary.path;
        strategy->scan = true;
        REQUIRE(SmartTrainingConfirmationStore::Save(temporary.path, 1001, context.GetDeviceSerial(), "guts", 0));
        observer->observationSequence = {raceList, raceList};
        CareerFlowCoordinator coordinator(observer, executor, scenario, strategy, config);
        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
        REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
        CHECK(strategy->committed == std::vector<std::string>{"guts"});
        CHECK(coordinator.GetState().confirmedTurnActions == 1);
        CHECK(coordinator.GetState().consecutiveRaceTurns == 0);
        CHECK_FALSE(SmartTrainingConfirmationStore::Load(temporary.path, 1001, context.GetDeviceSerial()).has_value());
    }
    SECTION("A generic optional Race List cannot prove that the pending training finished")
    {
        config.continueExistingCareer = true;
        config.traineeId = 1001;
        config.checkpointDirectory = temporary.path;
        strategy->scan = true;
        raceList.turnsToGoalEvidence = ObservedValue<int>::Unknown();
        raceList.goalTextEvidence = ObservedValue<std::string>::Make("Earn 3000 fans");
        REQUIRE(SmartTrainingConfirmationStore::Save(temporary.path, 1001, context.GetDeviceSerial(), "guts", 0));
        observer->observationSequence = {raceList};
        CareerFlowCoordinator coordinator(observer, executor, scenario, strategy, config);
        CHECK(coordinator.Step(context).status == CareerRunStatus::Failed);
        CHECK(strategy->committed.empty());
        CHECK(coordinator.GetState().confirmedTurnActions == 0);
        CHECK(SmartTrainingConfirmationStore::Load(temporary.path, 1001, context.GetDeviceSerial()).has_value());
    }
}
