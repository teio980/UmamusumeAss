#include <catch2/catch_test_macros.hpp>
#include "UmaAssistantCore/CareerDomain/TrainingStrategies.hpp"
#include "UmaAssistantCore/CareerDomain/Scenarios/Ura/UraSmartTrainingScorer.hpp"
#include "UmaAssistantCore/CareerDomain/Scenarios/Ura/UraScenarioModule.hpp"
#include "UmaAssistantCore/CareerDomain/Scenarios/Ura/UraSmartTrainingStrategy.hpp"
#include "UmaAssistantCore/Career/CareerObservationReader.hpp"
#include "UmaAssistantCore/Career/CareerActionExecutor.hpp"
#include "UmaAssistantCore/Career/CareerFlowCoordinator.hpp"
#include "tests/TaskEngine/MockTaskExecutionContext.hpp"
#include "MockCareerComponents.hpp"

using namespace UmaAssistant::CareerDomain;
using namespace UmaAssistant::CareerDomain::Ura;
using namespace UmaAssistant::Career;
using namespace UmaAssistant::Career::Tests;
using namespace UmaAssistant::TaskEngine::Tests;

TEST_CASE("TrainingRatio reduces via GCD and builds expected cycle", "[CareerDomain]")
{
    SECTION("2-1-0-0-0 ratio builds [speed, speed, stamina]")
    {
        TrainingRatio r{2, 1, 0, 0, 0};
        CHECK(r.IsValid());
        auto reduced = r.Reduce();
        CHECK(reduced.speed == 2);
        CHECK(reduced.stamina == 1);

        auto cycle = r.BuildCycle();
        REQUIRE(cycle.size() == 3);
        CHECK(cycle[0] == "speed");
        CHECK(cycle[1] == "speed");
        CHECK(cycle[2] == "stamina");
    }

    SECTION("4-2-0-0-0 ratio reduces to 2-1-0-0-0")
    {
        TrainingRatio r{4, 2, 0, 0, 0};
        CHECK(r.IsValid());
        auto reduced = r.Reduce();
        CHECK(reduced.speed == 2);
        CHECK(reduced.stamina == 1);
        CHECK(reduced.power == 0);
    }
}

TEST_CASE("DefaultTrainingStrategy makes correct intent based on energy and status", "[CareerDomain]")
{
    DefaultTrainingStrategy strategy("speed", 50);
    CareerState state;

    SECTION("High energy selects configured training")
    {
        state.energy = ObservedValue<int>::Make(80);
        auto intent = strategy.Choose(state, nullptr);
        CHECK(intent.kind == ActionIntentKind::Training);
        CHECK(intent.trainingType == "speed");
    }

    SECTION("Low energy selects rest")
    {
        state.energy = ObservedValue<int>::Make(30);
        auto intent = strategy.Choose(state, nullptr);
        CHECK(intent.kind == ActionIntentKind::Rest);
    }

    SECTION("Available infirmary takes precedence over training and rest")
    {
        state.energy = ObservedValue<int>::Make(20);
        state.infirmaryAvailable = true;
        auto intent = strategy.Choose(state, nullptr);
        CHECK(intent.kind == ActionIntentKind::Infirmary);
    }
}

TEST_CASE("SmartTrainingScorer evaluates gains, failure rate and bonuses", "[CareerDomain]")
{
    TrainingCandidate speedCand;
    speedCand.trainingType = "speed";
    speedCand.speedGain = 20;
    speedCand.staminaGain = 0;
    speedCand.powerGain = 10;
    speedCand.gutsGain = 0;
    speedCand.witGain = 0;
    speedCand.skillPointGain = 5;
    speedCand.failureRatePercent = 0;
    speedCand.unbondedSupportCount = 2; // In junior, 2 * 12 = 24 bonus
    speedCand.hintCount = 1;            // 1 * 4 = 4 bonus

    auto score = UraSmartTrainingScorer::Score(speedCand, RaceDistanceBand::Sprint, "junior", 70);
    CHECK_FALSE(score.IsExcluded());
    CHECK(score.bondBonus == 24.0);
    CHECK(score.hintBonus == 4.0);
    CHECK(score.totalScore > 50.0);

    SECTION("High failure rate candidate is excluded")
    {
        TrainingCandidate riskyCand = speedCand;
        riskyCand.failureRatePercent = 15; // > 5% max safe threshold
        auto riskyScore = UraSmartTrainingScorer::Score(riskyCand, RaceDistanceBand::Sprint, "junior", 70);
        CHECK(riskyScore.IsExcluded());
    }

    SECTION("SmartTrainingScorer::Choose picks higher scoring safe candidate")
    {
        TrainingCandidate staminaCand;
        staminaCand.trainingType = "stamina";
        staminaCand.speedGain = 0;
        staminaCand.staminaGain = 5;
        staminaCand.powerGain = 2;
        staminaCand.gutsGain = 2;
        staminaCand.witGain = 0;
        staminaCand.skillPointGain = 2;
        staminaCand.failureRatePercent = 0;

        auto decision = UraSmartTrainingScorer::Choose(
            {speedCand, staminaCand}, RaceDistanceBand::Sprint, "junior", 70);
        REQUIRE(decision.IsSafe());
        CHECK(decision.chosenCandidate->trainingType == "speed");
    }
}

TEST_CASE("UraSmartTrainingStrategy chooses best training or rests on high danger", "[CareerDomain]")
{
    UraSmartTrainingStrategy strategy(RaceDistanceBand::Sprint, 50);
    CareerState state;
    state.energy = ObservedValue<int>::Make(80);

    TrainingCandidate safeCand;
    safeCand.trainingType = "speed";
    safeCand.speedGain = 18;
    safeCand.staminaGain = 0;
    safeCand.powerGain = 8;
    safeCand.gutsGain = 0;
    safeCand.witGain = 0;
    safeCand.skillPointGain = 4;
    safeCand.failureRatePercent = 0;

    strategy.SetLatestCandidates({safeCand});
    auto intent = strategy.Choose(state, nullptr);
    CHECK(intent.kind == ActionIntentKind::Training);
    CHECK(intent.trainingType == "speed");

    SECTION("All candidates having dangerous failure rate results in Rest")
    {
        TrainingCandidate dangerousCand = safeCand;
        dangerousCand.failureRatePercent = 25; // High failure rate
        strategy.SetLatestCandidates({dangerousCand});

        auto dangerousIntent = strategy.Choose(state, nullptr);
        CHECK(dangerousIntent.kind == ActionIntentKind::Rest);
    }
}

TEST_CASE("CareerObservationReader measures synthetic energy bar and parses text", "[Career]")
{
    SECTION("ParseMood recognizes mood terms")
    {
        CHECK(CareerObservationReader::ParseMood("GREAT CONDITION") == "great");
        CHECK(CareerObservationReader::ParseMood("Normal") == "normal");
        CHECK(CareerObservationReader::ParseMood("Bad State") == "bad");
    }

    SECTION("ParseTurnPosition parses date and calculates turn index")
    {
        auto pos = CareerObservationReader::ParseTurnPosition("Classic Year Early July");
        REQUIRE(pos.has_value());
        CHECK(pos->year == "classic");
        CHECK(pos->phase == "early");
        CHECK(pos->month == 7);
        // turn = 1 + 24 (classic) + (7 - 1) * 2 + 0 = 37
        CHECK(pos->turnIndex == 37);
    }

    SECTION("MeasureEnergyBar computes full energy from saturated pixels")
    {
        const int width = 100;
        const int height = 20;
        std::vector<uint8_t> rgba(width * height * 4, 0);

        // Fill bar area with saturated green (R: 30, G: 200, B: 50, A: 255)
        for (int y = 5; y < 15; ++y)
        {
            for (int x = 10; x < 90; ++x)
            {
                int idx = (y * width + x) * 4;
                rgba[idx] = 30;
                rgba[idx + 1] = 200;
                rgba[idx + 2] = 50;
                rgba[idx + 3] = 255;
            }
        }

        std::vector<int> roi = {10, 5, 80, 10};
        auto measure = CareerObservationReader::MeasureEnergyBar(
            rgba.data(), width, height, roi, {}, width, height);

        REQUIRE(measure.has_value());
        CHECK(measure->percent >= 90);
        CHECK(measure->confidence > 0.5);
    }
}

TEST_CASE("CareerActionExecutor requires resource observations before sending input", "[Career]")
{
    CareerActionExecutor executor;
    MockTaskExecutionContext context;

    SECTION("Training without a recognized source screen is rejected")
    {
        auto intent = ActionIntent::MakeTraining("power", "Selected power");
        auto status = executor.Execute(intent, context);
        CHECK(status == ActionExecutionStatus::Failed);
        CHECK(context.taps.empty());
    }

    SECTION("Event choice without resource evidence is rejected")
    {
        auto intent = ActionIntent::MakeEventChoice(2, "Second option");
        auto status = executor.Execute(intent, context);
        CHECK(status == ActionExecutionStatus::Failed);
        CHECK(context.taps.empty());
    }
}

TEST_CASE("Career action failure stops the sequence and reports missing confirmation", "[Career]")
{
    CareerActionExecutor executor;
    MockTaskExecutionContext context;
    context.tapResult = false;
    auto intent = ActionIntent::MakeTraining("power", "Selected power");
    CHECK(executor.Execute(intent, context) == ActionExecutionStatus::Failed);
    CHECK(context.taps.empty());
    context.captureResult = false;
    CHECK_FALSE(executor.ConfirmCompletion(intent, context));
}

TEST_CASE("Career coordinator cannot complete without settlement or confirmed actions", "[Career]")
{
    auto observer = std::make_shared<MockCareerObserver>();
    auto executor = std::make_shared<MockCareerActionExecutor>();
    auto scenario = std::make_shared<UraScenarioModule>();
    auto strategy = std::make_shared<DefaultTrainingStrategy>();
    CareerFlowCoordinator coordinator(observer, executor, scenario, strategy);
    MockTaskExecutionContext context;

    SECTION("Step budget exhaustion is a failure")
    {
        CHECK(coordinator.RunToCompletion(context, 2).status == CareerRunStatus::Failed);
    }
    SECTION("Unconfirmed training cannot count as confirmed")
    {
        executor->confirmationResult = false;
        CHECK(coordinator.Step(context).status == CareerRunStatus::Failed);
    }
    SECTION("Failed event choices are propagated")
    {
        CareerObservation observation;
        observation.screenKind = CareerScreenKind::EventChoice;
        observer->observationSequence = {observation};
        executor->returnStatus = ActionExecutionStatus::Failed;
        CHECK(coordinator.Step(context).status == CareerRunStatus::Failed);
    }
    SECTION("Failed race progression is propagated")
    {
        CareerObservation observation;
        observation.screenKind = CareerScreenKind::RaceEntry;
        observer->observationSequence = {observation};
        executor->returnStatus = ActionExecutionStatus::Failed;
        CHECK(coordinator.Step(context).status == CareerRunStatus::Failed);
    }
}

TEST_CASE("CareerFlowCoordinator orchestrates main loop, events and settlement", "[Career]")
{
    auto observer = std::make_shared<MockCareerObserver>();
    auto actionExecutor = std::make_shared<MockCareerActionExecutor>();
    auto scenario = std::make_shared<UraScenarioModule>();
    auto strategy = std::make_shared<DefaultTrainingStrategy>("speed", 50);

    CareerObservation obs1;
    obs1.screenKind = CareerScreenKind::MainTurn;
    obs1.screenId = "career_main";
    obs1.energyPercent = ObservedValue<int>::Make(85);

    CareerObservation obs2;
    obs2.screenKind = CareerScreenKind::EventChoice;
    obs2.screenId = "event_choice";

    CareerObservation obs3;
    obs3.screenKind = CareerScreenKind::MainTurn;
    obs3.screenId = "career_main";
    obs3.energyPercent = ObservedValue<int>::Make(30);

    CareerObservation obs4;
    obs4.screenKind = CareerScreenKind::Settlement;
    obs4.screenId = "career_complete";

    CareerObservation obs5;
    obs5.screenId = "home";

    observer->observationSequence = {obs1, obs2, obs3, obs4, obs5};

    CareerFlowCoordinator coordinator(observer, actionExecutor, scenario, strategy);
    MockTaskExecutionContext context;

    auto result = coordinator.RunToCompletion(context, 10);

    CHECK(result.status == CareerRunStatus::Completed);
    REQUIRE(actionExecutor->executedIntents.size() == 4);
    CHECK(actionExecutor->executedIntents[0].kind == ActionIntentKind::Training);
    CHECK(actionExecutor->executedIntents[1].kind == ActionIntentKind::SelectEventOption);
    CHECK(actionExecutor->executedIntents[2].kind == ActionIntentKind::Rest);
}

TEST_CASE("Career goal completion is insufficient proof of whole-run completion", "[Career][Replay]")
{
    auto observer = std::make_shared<MockCareerObserver>();
    auto executor = std::make_shared<MockCareerActionExecutor>();
    CareerObservation goal;
    goal.screenId = "goal_complete";
    goal.screenKind = CareerScreenKind::GoalUpdate;
    goal.objectiveCompleted = ObservedValue<bool>::Make(true);
    CareerObservation home;
    home.screenId = "home";
    observer->observationSequence = {goal, home};
    CareerFlowCoordinator coordinator(observer, executor, std::make_shared<UraScenarioModule>(),
        std::make_shared<DefaultTrainingStrategy>());
    MockTaskExecutionContext context;
    auto const result = coordinator.RunToCompletion(context, 5);
    CHECK(result.status == CareerRunStatus::Failed);
    CHECK(result.failureReason.find("terminal settlement") != std::string::npos);
}

TEST_CASE("Opening training and recovery cannot advance the confirmed ratio cursor", "[Career][Replay]")
{
    auto observer = std::make_shared<MockCareerObserver>();
    auto executor = std::make_shared<MockCareerActionExecutor>();
    auto strategy = std::make_shared<RatioTrainingStrategy>(TrainingRatio{1, 1, 0, 0, 0});
    CareerFlowCoordinator coordinator(observer, executor, std::make_shared<UraScenarioModule>(), strategy);
    MockTaskExecutionContext context;
    CareerObservation first;
    first.screenId = "career_main";
    first.screenKind = CareerScreenKind::MainTurn;
    first.energyPercent = ObservedValue<int>::Make(80);
    first.turnIndexEvidence = ObservedValue<int>::Make(12);
    auto second = first;
    second.turnIndexEvidence = ObservedValue<int>::Make(13);
    observer->observationSequence = {first, second};
    REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
    REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
    REQUIRE(executor->executedIntents.size() == 2);
    CHECK(executor->executedIntents[0].trainingType == "speed");
    CHECK(executor->executedIntents[1].trainingType == "speed");

    CareerObservation drawer;
    drawer.screenId = "training_selection";
    drawer.screenKind = CareerScreenKind::TrainingSelect;
    observer->observationSequence = {drawer};
    REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
    coordinator.ResetAfterRecovery();
    observer->observationSequence = {second};
    REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
    CHECK(executor->executedIntents.back().trainingType == "speed");

    observer->observationSequence = {drawer};
    REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
    CareerObservation outcome;
    outcome.screenId = "training_result";
    outcome.screenKind = CareerScreenKind::TrainingResult;
    observer->observationSequence = {outcome};
    REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
    observer->observationSequence = {second};
    REQUIRE(coordinator.Step(context).status == CareerRunStatus::Running);
    CHECK(executor->executedIntents.back().trainingType == "stamina");
}
