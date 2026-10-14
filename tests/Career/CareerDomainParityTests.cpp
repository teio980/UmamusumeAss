#include <catch2/catch_test_macros.hpp>

#include "UmaAssistantCore/CareerDomain/CareerCalendar.hpp"
#include "UmaAssistantCore/CareerDomain/CareerGoalTextParser.hpp"
#include "UmaAssistantCore/CareerDomain/Scenarios/Ura/UraScenarioModule.hpp"
#include "UmaAssistantCore/CareerDomain/Scenarios/Ura/UraSmartTrainingStrategy.hpp"
#include "UmaAssistantCore/CareerDomain/Scenarios/Ura/UraStrategyRegistry.hpp"
#include "UmaAssistantCore/CareerDomain/Scenarios/Ura/UraSmartTrainingScorer.hpp"
#include "UmaAssistantCore/CareerDomain/TrainingStrategies.hpp"

#include <cmath>
#include <limits>
#include <memory>
#include <string>
#include <utility>
#include <vector>

using namespace UmaAssistant::CareerDomain;

namespace
{

CareerState StateWithEnergy(int energy)
{
    CareerState state;
    state.energy = ObservedValue<int>::Make(energy, 0.96, ObservationSource::Ocr);
    return state;
}

Ura::TrainingCandidate Candidate(std::string type, int speed, int failure = 0)
{
    Ura::TrainingCandidate candidate;
    candidate.trainingType = std::move(type);
    candidate.speedGain = speed;
    candidate.staminaGain = 3;
    candidate.powerGain = 2;
    candidate.gutsGain = 1;
    candidate.witGain = 1;
    candidate.skillPointGain = 4;
    candidate.failureRatePercent = failure;
    candidate.coreConfidence = 0.95;
    candidate.failureRateConfidence = 0.95;
    return candidate;
}

TEST_CASE("Career parity preserves unknown evidence and strategy-specific fallbacks", "[CareerDomainParity]")
{
    CareerObservation observation;
    observation.screenId = "career_main";
    observation.screenKind = CareerScreenKind::MainTurn;
    observation.turnIndexEvidence = ObservedValue<int>::Unknown();
    CareerState state;
    state.UpdateFromObservation(observation);
    REQUIRE(!state.turnIndexEvidence.HasValue());
    REQUIRE(!state.energy.HasValue());

    DefaultTrainingStrategy normal("speed", 45);
    auto normalIntent = normal.Choose(state, nullptr);
    REQUIRE(normalIntent.kind == ActionIntentKind::Training);
    REQUIRE(normalIntent.trainingType == "speed");

    UraSmartTrainingStrategy smart(Ura::RaceDistanceBand::Middle, 45);
    auto cautiousIntent = smart.Choose(state, nullptr);
    REQUIRE(cautiousIntent.kind == ActionIntentKind::Rest);
    REQUIRE(cautiousIntent.source == ObservationSource::DefaultFallback);

    // A missing candidate set cannot be replaced by an invented speed card.
    state.energy = ObservedValue<int>::Make(80, 0.95, ObservationSource::Ocr);
    REQUIRE(smart.Choose(state, nullptr).kind == ActionIntentKind::Rest);
}

TEST_CASE("URA turn starts require distinct reliable calendar evidence", "[CareerDomainParity]")
{
    UraScenarioModule scenario;
    CareerState state;
    state.turnIndex = 0;
    scenario.OnTurnStart(state);
    scenario.OnTurnStart(state);
    REQUIRE(state.careerStarted);
    REQUIRE(state.turnIndex == 0);
    REQUIRE(!state.lastStartedTurnIndex.has_value());

    CareerObservation first;
    first.screenKind = CareerScreenKind::MainTurn;
    first.turnIndexEvidence = ObservedValue<int>::Make(4, 0.93, ObservationSource::Ocr);
    scenario.OnObserve(state, first);
    scenario.OnTurnStart(state);
    scenario.OnTurnStart(state);
    REQUIRE(state.turnIndex == 4);
    REQUIRE(state.lastStartedTurnIndex == 4);

    // An unknown intermediate frame cannot erase the last trusted turn.
    CareerObservation unknown;
    unknown.screenKind = CareerScreenKind::Unknown;
    unknown.turnIndexEvidence = ObservedValue<int>::Unknown();
    scenario.OnObserve(state, unknown);
    scenario.OnTurnStart(state);
    REQUIRE(state.turnIndex == 4);

    CareerObservation next;
    next.screenKind = CareerScreenKind::MainTurn;
    next.turnIndexEvidence = ObservedValue<int>::Make(5, 0.90, ObservationSource::Ocr);
    scenario.OnObserve(state, next);
    scenario.OnTurnStart(state);
    REQUIRE(state.turnIndex == 5);
    REQUIRE(state.lastStartedTurnIndex == 5);
}

TEST_CASE("unreadable career main clears stale evidence but keeps the last numeric turn", "[CareerDomainParity]")
{
    CareerState state;
    CareerObservation observedMain;
    observedMain.screenId = "career_main";
    observedMain.screenKind = CareerScreenKind::MainTurn;
    observedMain.turnIndexEvidence = ObservedValue<int>::Make(7, 0.95, ObservationSource::Ocr);
    observedMain.energyPercent = ObservedValue<int>::Make(72, 0.94, ObservationSource::Ocr);
    observedMain.moodEvidence = ObservedValue<std::string>::Make(
        "good", 0.93, ObservationSource::Ocr);
    observedMain.turnsToGoalEvidence = ObservedValue<int>::Make(3, 0.92, ObservationSource::Ocr);
    observedMain.goalTextEvidence = ObservedValue<std::string>::Make(
        "Run in the Junior Stakes", 0.91, ObservationSource::Ocr);
    state.UpdateFromObservation(observedMain);
    REQUIRE(state.turnIndex == 7);
    REQUIRE(state.turnIndexEvidence.IsReliable());
    UraScenarioModule scenario;
    scenario.OnTurnStart(state);
    REQUIRE(state.lastStartedTurnIndex == 7);

    // The observer can recognize the resource-defined Main ID even when
    // field-level evidence is missing and the broader screen kind is unknown.
    CareerObservation unreadableMain;
    unreadableMain.screenId = "CAREER_MAIN";
    unreadableMain.screenKind = CareerScreenKind::Unknown;
    state.UpdateFromObservation(unreadableMain);

    REQUIRE(!state.turnIndexEvidence.IsReliable());
    REQUIRE(state.turnIndex == 7);
    REQUIRE(!state.energy.IsReliable());
    REQUIRE(!state.mood.IsReliable());
    REQUIRE(!state.turnsToGoal.IsReliable());
    REQUIRE(!state.fansToGoal.IsReliable());
    REQUIRE(state.goalText.empty());
    REQUIRE_FALSE(state.turnPositionText.has_value());

    scenario.OnTurnStart(state);
    REQUIRE(state.lastStartedTurnIndex == 7);
}

TEST_CASE("Ratio cycle advances only after matching confirmed training", "[CareerDomainParity]")
{
    RatioTrainingStrategy strategy(TrainingRatio{2, 1, 0, 0, 0}, 50);
    CareerState state = StateWithEnergy(70);
    auto first = strategy.Choose(state, nullptr);
    REQUIRE((first.kind == ActionIntentKind::Training && first.trainingType == "speed"));
    // Repeated planning has no effect on the cycle.
    auto repeated = strategy.Choose(state, nullptr);
    REQUIRE(repeated.trainingType == "speed");
    strategy.ConfirmTraining(state, "stamina");
    REQUIRE(strategy.Choose(state, nullptr).trainingType == "speed");
    strategy.ConfirmTraining(state, "speed");
    strategy.ConfirmTraining(state, "speed");
    REQUIRE(strategy.Choose(state, nullptr).trainingType == "speed");
    strategy.ConfirmTraining(state, "speed");
    REQUIRE(strategy.Choose(state, nullptr).trainingType == "stamina");

    // A rest, infirmary, or a stale confirmation invalidates pending intent.
    state.energy = ObservedValue<int>::Make(20, 0.95, ObservationSource::Ocr);
    REQUIRE(strategy.Choose(state, nullptr).kind == ActionIntentKind::Rest);
    strategy.ConfirmTraining(state, "stamina");
    state.energy = ObservedValue<int>::Make(70, 0.95, ObservationSource::Ocr);
    REQUIRE(strategy.Choose(state, nullptr).trainingType == "stamina");
}

TEST_CASE("URA scoring excludes unknown and risky cards and strategy registry stays extensible", "[CareerDomainParity]")
{
    auto unknown = Candidate("speed", 20, 0);
    unknown.witGain.reset();
    auto risky = Candidate("stamina", 40, 6);
    auto safe = Candidate("power", 12, 2);
    auto decision = Ura::UraSmartTrainingScorer::Choose(
        {unknown, risky, safe}, Ura::RaceDistanceBand::Middle, "Classic Year", 60);
    REQUIRE(decision.IsSafe());
    REQUIRE(decision.chosenCandidate->trainingType == "power");
    REQUIRE(decision.allEvaluations.size() == 3);
    REQUIRE(decision.allEvaluations[0].second.IsExcluded());
    REQUIRE(decision.allEvaluations[1].second.IsExcluded());

    const auto ids = UraStrategyRegistry::AvailableStrategies();
    for (const auto* id : {"smart-balanced-safe", "default-speed-medium",
         "default-stamina-medium", "default-power-medium", "default-guts-medium",
         "default-wit-medium", "custom-ratio", "smart", "ura-smart"})
        REQUIRE(UraStrategyRegistry::IsRegistered(id));
    REQUIRE(UraStrategyRegistry::Create("custom-ratio", TrainingRatio{2, 1, 0, 0, 0}) != nullptr);

    // Confidence normalization prevents malformed data from becoming reliable.
    const auto malformed = ObservedValue<int>::Make(50,
        std::numeric_limits<double>::quiet_NaN(), ObservationSource::Ocr);
    REQUIRE(!malformed.IsReliable());
    REQUIRE(malformed.confidence == 0.0);
    const auto zeroConfidence = ObservedValue<int>::Make(42, 0.0, ObservationSource::Ocr);
    REQUIRE(!zeroConfidence.IsReliable());
    REQUIRE(zeroConfidence.HasValue());
}

TEST_CASE("smart strategy exposes candidate scan capability and preserves field evidence", "[CareerDomainParity]")
{
    UraSmartTrainingStrategy smart(Ura::RaceDistanceBand::Mile, 45);
    REQUIRE(smart.RequiresTrainingCandidateScan());

    TrainingCandidateEvidence evidence;
    evidence.trainingType = "speed";
    evidence.statGains = {
        ObservedValue<int>::Make(18, 0.96, ObservationSource::Ocr),
        ObservedValue<int>::Make(5, 0.95, ObservationSource::Ocr),
        ObservedValue<int>::Make(8, 0.94, ObservationSource::Ocr),
        ObservedValue<int>::Make(2, 0.93, ObservationSource::ColorProbe),
        ObservedValue<int>::Make(4, 0.96, ObservationSource::Ocr)};
    evidence.skillPointGain = ObservedValue<int>::Make(3, 0.95, ObservationSource::Ocr);
    evidence.failureRatePercent = ObservedValue<int>::Make(2, 0.91, ObservationSource::Ocr);
    smart.SetTrainingCandidateEvidence({evidence});

    REQUIRE(smart.LatestCandidates().size() == 1);
    REQUIRE(smart.LatestCandidates()[0].speedGain == 18);
    REQUIRE(smart.LatestCandidates()[0].coreConfidence == 0.93);
    REQUIRE(smart.LatestCandidates()[0].coreSource == ObservationSource::Derived);
    REQUIRE(!smart.LatestCandidates()[0].trainingLevel.has_value());
    REQUIRE(!smart.LatestCandidates()[0].unbondedSupportCount.has_value());
    REQUIRE(!smart.LatestCandidates()[0].hintCount.has_value());

    auto state = StateWithEnergy(70);
    REQUIRE(smart.Choose(state, nullptr).kind == ActionIntentKind::Training);
    evidence.statGains[4] = ObservedValue<int>::Unknown();
    smart.SetTrainingCandidateEvidence({evidence});
    REQUIRE(smart.LatestCandidates()[0].coreConfidence == 0.0);
    REQUIRE(!smart.LatestCandidates()[0].witGain.has_value());
    REQUIRE(smart.Choose(state, nullptr).kind == ActionIntentKind::Rest);

    smart.SetTrainingCandidateEvidence({evidence});
    smart.ClearTrainingCandidateEvidence();
    REQUIRE(smart.LatestCandidates().empty());
}

TEST_CASE("smart candidate scans follow strategy safety and scenario priorities", "[CareerDomainParity]")
{
    UraSmartTrainingStrategy smart(Ura::RaceDistanceBand::Mile, 45);
    CareerState state;
    REQUIRE(!smart.ShouldScanTrainingCandidates(state, nullptr));

    state.energy = ObservedValue<int>::Make(65, 0.91, ObservationSource::Ocr);
    REQUIRE(smart.ShouldScanTrainingCandidates(state, nullptr));

    state.energy = ObservedValue<int>::Make(40, 0.95, ObservationSource::Ocr);
    REQUIRE(!smart.ShouldScanTrainingCandidates(state, nullptr));
    state.energy = ObservedValue<int>::Make(65, 0.91, ObservationSource::Ocr);
    state.turnPositionText = "Classic Late June";
    REQUIRE(!smart.ShouldScanTrainingCandidates(state, nullptr));

    state.turnPositionText = "Classic Early May";
    state.mood = ObservedValue<std::string>::Make("normal", 0.96, ObservationSource::Ocr);
    UraScenarioModule scenario;
    REQUIRE(!smart.ShouldScanTrainingCandidates(state, &scenario));

    state.mood = ObservedValue<std::string>::Unknown();
    state.infirmaryAvailable = true;
    REQUIRE(!smart.ShouldScanTrainingCandidates(state, &scenario));
}

TEST_CASE("URA strategy and scanner resolve one objective race distance per turn", "[CareerDomainParity]")
{
    ScenarioInitialization initialization;
    initialization.trainingRaceDistances = {
        {12, 1, 0, {"long", 3000}},
        {24, 2, 0, {"mile", 1600}},
        {24, 2, 1, {"middle", 2000}},
    };
    initialization.scenarioTrainingRaceDistanceFallback = {"mile", 1800};
    UraScenarioModule scenario;
    scenario.Initialize(initialization);

    CareerState state = StateWithEnergy(72);
    state.turnIndex = 12;
    const auto distance = scenario.GetCurrentTrainingRaceDistance(state);
    REQUIRE(distance.has_value());
    REQUIRE(distance->distanceBand == "long");
    REQUIRE(distance->meters == 3000);

    auto speed = Candidate("speed", 20);
    auto stamina = Candidate("stamina", 1);
    stamina.staminaGain = 20;
    UraSmartTrainingStrategy strategy(Ura::RaceDistanceBand::Mile, 45);
    strategy.SetLatestCandidates({speed, stamina});
    const auto intent = strategy.Choose(state, &scenario);
    REQUIRE(intent.kind == ActionIntentKind::Training);
    REQUIRE(intent.trainingType == "stamina");
    REQUIRE(strategy.LastDecision().score.has_value());
    REQUIRE(strategy.LastDecision().score->distanceBand == Ura::RaceDistanceBand::Extended);
    const auto scanDecision = Ura::UraSmartTrainingScorer::Choose(
        {speed, stamina},
        Ura::UraSmartTrainingScorer::ParseDistanceBand(
            distance->distanceBand, distance->meters),
        state.turnPositionText.value_or(state.phaseId),
        state.energy.Value());
    REQUIRE(scanDecision.chosenCandidate.has_value());
    REQUIRE(scanDecision.chosenCandidate->trainingType == intent.trainingType);

    // The strategy's explicit fallback band applies only without a catalog
    // race for the current/upcoming objective. This confirms the scenario
    // descriptor, not the constructor default or OCR goal text, chose the
    // weights for the same candidate set a scanner will score.
    state.turnIndex = 13;
    const auto nextDistance = scenario.GetCurrentTrainingRaceDistance(state);
    REQUIRE(nextDistance.has_value());
    REQUIRE(nextDistance->distanceBand == "mile");
    REQUIRE(nextDistance->meters == 1600);
    REQUIRE(Ura::UraSmartTrainingScorer::ParseDistanceBand(
        nextDistance->distanceBand, nextDistance->meters) == Ura::RaceDistanceBand::Mile);
    state.turnIndex = 24;
    const auto sameTurnDistance = scenario.GetCurrentTrainingRaceDistance(state);
    REQUIRE(sameTurnDistance.has_value());
    REQUIRE(sameTurnDistance->distanceBand == "mile");

    state.turnIndex = 72;
    REQUIRE_FALSE(scenario.GetCurrentTrainingRaceDistance(state).has_value());
    state.phaseId = "finale_underway";
    const auto finaleDistance = scenario.GetCurrentTrainingRaceDistance(state);
    REQUIRE(finaleDistance.has_value());
    REQUIRE(finaleDistance->distanceBand == "mile");
    REQUIRE(finaleDistance->meters == 1800);
}

TEST_CASE("smart training resolves global trainee races before the scenario current race",
    "[CareerDomainParity][distance]")
{
    ScenarioInitialization initialization;
    initialization.trainingRaceDistances = {
        {12, 1, 0, {"long", 3000}, "global-first"},
        {18, 2, 0, {"mile", 1600}, "global-explicit"},
    };
    ScenarioObjectiveDefinition objective;
    objective.objectiveId = "scenario-debut-objective";
    objective.kind = "race_result";
    objective.raceId = "scenario-debut";
    initialization.scenarioObjectives.push_back(objective);
    ScenarioRaceDefinition scenarioRace;
    scenarioRace.raceId = "scenario-debut";
    scenarioRace.distanceBand = "sprint";
    scenarioRace.distanceMeters = 1200;
    initialization.scenarioRaces.push_back(scenarioRace);

    UraScenarioModule scenario;
    scenario.Initialize(initialization);
    CareerState state;
    state.turnIndex = 12;
    state.currentObjectiveId = objective.objectiveId;
    state.currentRaceId = scenarioRace.raceId;

    // A scenario-owned debut race is the managed fallback, but the selected
    // trainee's first upcoming global race defines smart-training weights.
    auto const upcoming = scenario.GetCurrentTrainingRaceDistance(state);
    REQUIRE(upcoming.has_value());
    CHECK(upcoming->distanceBand == "long");
    CHECK(upcoming->meters == 3000);

    // When the scenario reports a current ID that also exists in the global
    // race snapshot, that exact global record outranks a different upcoming
    // objective just as UraSmartTrainingDistanceResolver does.
    state.currentRaceId = "global-explicit";
    auto const exactCurrent = scenario.GetCurrentTrainingRaceDistance(state);
    REQUIRE(exactCurrent.has_value());
    CHECK(exactCurrent->distanceBand == "mile");
    CHECK(exactCurrent->meters == 1600);

    // With no trainee global race remaining, the scenario race stays available
    // as the distance fallback.
    state.turnIndex = 72;
    state.currentRaceId = scenarioRace.raceId;
    auto const scenarioFallback = scenario.GetCurrentTrainingRaceDistance(state);
    REQUIRE(scenarioFallback.has_value());
    CHECK(scenarioFallback->distanceBand == "sprint");
    CHECK(scenarioFallback->meters == 1200);
}

TEST_CASE("A due countdown alone does not claim objective completion", "[CareerDomainParity]")
{
    UraScenarioModule scenario;
    CareerState state;
    CareerObservation due;
    due.screenId = "career_main";
    due.screenKind = CareerScreenKind::MainTurn;
    due.turnsToGoalEvidence = ObservedValue<int>::Make(0, 0.99, ObservationSource::Ocr);
    scenario.OnObserve(state, due);
    // A countdown does not identify the goal kind. The managed module keeps
    // that objective unknown instead of treating the number as a race goal.
    REQUIRE(state.phaseId == "career");
    REQUIRE(!scenario.IsObjectiveCompleted(state));
    REQUIRE(!state.objectiveCompleted.HasValue());

    CareerObservation completed;
    completed.objectiveCompleted = ObservedValue<bool>::Make(true, 0.98, ObservationSource::TemplateMatch);
    scenario.OnObserve(state, completed);
    REQUIRE(state.phaseId == "GoalComplete");
    REQUIRE(scenario.IsObjectiveCompleted(state));

    CareerObservation nextObjective;
    nextObjective.screenKind = CareerScreenKind::MainTurn;
    nextObjective.turnsToGoalEvidence = ObservedValue<int>::Make(4, 0.95, ObservationSource::Ocr);
    scenario.OnObserve(state, nextObjective);
    REQUIRE(state.phaseId == "career");
    REQUIRE(!state.objectiveCompleted.HasValue());
    REQUIRE(!scenario.IsObjectiveCompleted(state));
}

TEST_CASE("URA Finale permits intervening turns and races only at a readable deadline", "[CareerDomainParity]")
{
    UraScenarioModule scenario;
    CareerState state;
    CareerObservation finaleStarted;
    finaleStarted.screenId = "goal_complete";
    finaleStarted.screenKind = CareerScreenKind::GoalUpdate;
    scenario.OnObserve(state, finaleStarted);

    CareerObservation finaleTurn;
    finaleTurn.screenId = "career_main";
    finaleTurn.screenKind = CareerScreenKind::MainTurn;
    finaleTurn.turnIndexEvidence = ObservedValue<int>::Make(72, 0.96, ObservationSource::Ocr);
    finaleTurn.turnsToGoalEvidence = ObservedValue<int>::Make(3, 0.95, ObservationSource::Ocr);
    finaleTurn.goalTextEvidence = ObservedValue<std::string>::Make(
        "Place 1st in the URA Finale Qualifier", 0.95, ObservationSource::Ocr);
    state.energy = ObservedValue<int>::Make(80, 0.96, ObservationSource::Ocr);
    scenario.OnObserve(state, finaleTurn);

    DefaultTrainingStrategy normal("speed", 45);
    auto intent = normal.Choose(state, &scenario);
    REQUIRE(intent.kind == ActionIntentKind::Training);

    state.energy = ObservedValue<int>::Make(35, 0.95, ObservationSource::Ocr);
    intent = normal.Choose(state, &scenario);
    REQUIRE(intent.kind == ActionIntentKind::Rest);

    finaleTurn.turnsToGoalEvidence = ObservedValue<int>::Make(0, 0.95, ObservationSource::Ocr);
    state.energy = ObservedValue<int>::Make(80, 0.96, ObservationSource::Ocr);
    scenario.OnObserve(state, finaleTurn);
    intent = normal.Choose(state, &scenario);
    REQUIRE(intent.kind == ActionIntentKind::Race);
    REQUIRE(intent.raceTarget == "finale");

    // Unknown goal text follows the managed baseline: ordinary training is
    // still available, while the smart strategy uses its cautious Rest path.
    finaleTurn.goalTextEvidence = ObservedValue<std::string>::Unknown();
    finaleTurn.turnsToGoalEvidence = ObservedValue<int>::Make(3, 0.95, ObservationSource::Ocr);
    scenario.OnObserve(state, finaleTurn);
    intent = normal.Choose(state, &scenario);
    REQUIRE(intent.kind == ActionIntentKind::Training);
    UraSmartTrainingStrategy smart(Ura::RaceDistanceBand::Unknown, 45);
    intent = smart.Choose(state, &scenario);
    REQUIRE(intent.kind == ActionIntentKind::Rest);
    REQUIRE(intent.source == ObservationSource::DefaultFallback);
}

TEST_CASE("Career goal parser preserves managed goal classifications and unknowns", "[CareerDomainParity]")
{
    REQUIRE(CareerGoalTextParser::ParseTurnsLeft("3 turns left") == 3);
    REQUIRE(CareerGoalTextParser::ParseTurnsLeft("  12 ") == 12);
    REQUIRE_FALSE(CareerGoalTextParser::ParseTurnsLeft("year 12").has_value());

    REQUIRE(CareerGoalTextParser::ParseFansToGo("1,234 fans to go") == 1234);
    REQUIRE(CareerGoalTextParser::ParseFansToGo("500 fan(s) to go") == 500);
    REQUIRE_FALSE(CareerGoalTextParser::ParseFansToGo("1,234 fans earned").has_value());

    REQUIRE(CareerGoalTextParser::ParseRaceCountLeft("Run in I time left") == 1);
    REQUIRE(CareerGoalTextParser::ParseRaceCountLeft("Run in 2 times left") == 2);
    REQUIRE_FALSE(CareerGoalTextParser::ParseRaceCountLeft("Run in 2 races").has_value());
    REQUIRE(CareerGoalTextParser::ParseRaceGrade("G 1") == std::optional<std::string>("G1"));
    REQUIRE(CareerGoalTextParser::ParseRaceGrade("G II") == std::optional<std::string>("G2"));
    REQUIRE(CareerGoalTextParser::ParseRaceGrade("G lll") == std::optional<std::string>("G3"));
    REQUIRE_FALSE(CareerGoalTextParser::ParseRaceGrade("G4").has_value());

    REQUIRE(CareerGoalTextParser::Classify("Get 1,000 fans to go") == CareerGoalKind::Fans);
    REQUIRE(CareerGoalTextParser::Classify("Run in 2 times left G3") == CareerGoalKind::GradeRaceCount);
    REQUIRE(CareerGoalTextParser::Classify("Goal Achieved") == CareerGoalKind::Completed);
    REQUIRE(CareerGoalTextParser::Classify("Place top 3 in the race") == CareerGoalKind::Race);
    REQUIRE(CareerGoalTextParser::Classify("Objective text was unreadable") == CareerGoalKind::Unknown);
}

TEST_CASE("career race slots map years and halves to the managed one-based calendar", "[CareerDomainParity]")
{
    REQUIRE(CareerCalendar::TryMapRaceSlot("First Year", "01_01") == 1);
    REQUIRE(CareerCalendar::TryMapRaceSlot("First Year", "07_02") == 14);
    REQUIRE(CareerCalendar::TryMapRaceSlot("Second Year", "01_01") == 25);
    REQUIRE(CareerCalendar::TryMapRaceSlot("Senior", "12_02") == 72);
    REQUIRE_FALSE(CareerCalendar::TryMapRaceSlot("Fourth Year", "01_01").has_value());
    REQUIRE_FALSE(CareerCalendar::TryMapRaceSlot("First Year", "13_01").has_value());
    REQUIRE_FALSE(CareerCalendar::TryMapRaceSlot("First Year", "01_03").has_value());
}

TEST_CASE("mandatory race goals precede infirmary, rest, and configured training", "[CareerDomainParity]")
{
    UraScenarioModule scenario;
    CareerState state;
    scenario.OnRunStart(state);
    state.energy = ObservedValue<int>::Make(15, 0.96, ObservationSource::Ocr);
    state.infirmaryAvailable = true;

    CareerObservation fans;
    fans.screenId = "career_main";
    fans.screenKind = CareerScreenKind::MainTurn;
    fans.score = 0.96;
    fans.turnIndexEvidence = ObservedValue<int>::Make(10, 0.96, ObservationSource::Ocr);
    fans.turnsToGoalEvidence = ObservedValue<int>::Make(4, 0.95, ObservationSource::Ocr);
    fans.goalTextEvidence = ObservedValue<std::string>::Make(
        "Earn 5,000 fans to go", 0.94, ObservationSource::Ocr);
    fans.fansToGoalEvidence = ObservedValue<int>::Make(5000, 0.93, ObservationSource::Ocr);
    scenario.OnObserve(state, fans);

    DefaultTrainingStrategy normal("speed", 45);
    const auto intent = normal.Choose(state, &scenario);
    REQUIRE(intent.kind == ActionIntentKind::Race);
    REQUIRE(intent.raceTarget == "fans");
    REQUIRE(intent.confidence == 0.93);

    // A deadline race must still win when the visible banner says only a
    // generic race goal and energy is below the rest threshold.
    CareerObservation race = fans;
    race.turnIndexEvidence = ObservedValue<int>::Make(11, 0.96, ObservationSource::Ocr);
    race.turnsToGoalEvidence = ObservedValue<int>::Make(0, 0.95, ObservationSource::Ocr);
    race.fansToGoalEvidence = ObservedValue<int>::Unknown();
    race.goalTextEvidence = ObservedValue<std::string>::Make(
        "Run in the Junior Stakes", 0.94, ObservationSource::Ocr);
    scenario.OnObserve(state, race);
    const auto dueIntent = normal.Choose(state, &scenario);
    REQUIRE(dueIntent.kind == ActionIntentKind::Race);
    REQUIRE(dueIntent.raceTarget == "goal");
}

TEST_CASE("grade race goals require reliable grade, count, calendar, and first-card schedule", "[CareerDomainParity]")
{
    UraScenarioModule scenario;
    ScenarioInitialization schedule;
    schedule.raceSchedule = {
        {25, "G3", true}, {26, "G3", true}, {27, "G2", true},
        {28, "G1", true}, {29, "G1", false}};
    scenario.Initialize(schedule);
    CareerState state;
    state.energy = ObservedValue<int>::Make(75, 0.95, ObservationSource::Ocr);

    CareerObservation goal;
    goal.screenId = "career_main";
    goal.screenKind = CareerScreenKind::MainTurn;
    goal.score = 0.95;
    goal.turnIndexEvidence = ObservedValue<int>::Make(25, 0.95, ObservationSource::Ocr);
    goal.turnsToGoalEvidence = ObservedValue<int>::Make(4, 0.95, ObservationSource::Ocr);
    goal.goalTextEvidence = ObservedValue<std::string>::Make(
        "Run in 3 times left G3", 0.94, ObservationSource::Ocr);
    scenario.OnObserve(state, goal);

    DefaultTrainingStrategy normal("power", 45);
    const auto currentRace = normal.Choose(state, &scenario);
    REQUIRE(currentRace.kind == ActionIntentKind::Race);
    REQUIRE(currentRace.raceTarget == "grade_race_count");

    // The incomplete first-card schedule removes the mandatory-race evidence:
    // unknown catalog slots cannot be treated as qualifying races.
    scenario.Initialize({{{25, "G3", false}, {26, "G3", true}}});
    scenario.OnObserve(state, goal);
    REQUIRE(normal.Choose(state, &scenario).kind == ActionIntentKind::Training);

    // Unknown calendar/count evidence must not turn an unreadable banner into
    // a race intent, even if the prior observation contained a graded goal.
    auto unknownCalendar = goal;
    unknownCalendar.turnIndexEvidence = ObservedValue<int>::Unknown();
    unknownCalendar.goalTextEvidence = ObservedValue<std::string>::Unknown();
    unknownCalendar.turnsToGoalEvidence = ObservedValue<int>::Unknown();
    scenario.OnObserve(state, unknownCalendar);
    REQUIRE(normal.Choose(state, &scenario).kind == ActionIntentKind::Training);
}

TEST_CASE("race streak defers optional races but never a deadline or finale race", "[CareerDomainParity]")
{
    UraScenarioModule scenario;
    CareerState state;
    state.energy = ObservedValue<int>::Make(75, 0.95, ObservationSource::Ocr);
    DefaultTrainingStrategy normal("speed", 45);

    auto observeFans = [&](int turn, int turnsLeft) {
        CareerObservation observation;
        observation.screenId = "career_main";
        observation.screenKind = CareerScreenKind::MainTurn;
        observation.score = 0.95;
        observation.turnIndexEvidence = ObservedValue<int>::Make(turn, 0.95, ObservationSource::Ocr);
        observation.turnsToGoalEvidence = ObservedValue<int>::Make(turnsLeft, 0.95, ObservationSource::Ocr);
        observation.fansToGoalEvidence = ObservedValue<int>::Make(900, 0.94, ObservationSource::Ocr);
        observation.goalTextEvidence = ObservedValue<std::string>::Make(
            "Earn 900 fans to go", 0.94, ObservationSource::Ocr);
        scenario.OnObserve(state, observation);
        scenario.OnTurnStart(state);
        return observation;
    };
    auto submitRace = [&](const CareerObservation& source) {
        scenario.OnTurnActionSubmitted(
            state,
            ActionIntent::MakeRace("fans"),
            source);
    };
    auto raceSource = [] {
        CareerObservation source;
        source.screenId = "race_runner";
        source.screenKind = CareerScreenKind::RaceExecution;
        return source;
    };

    observeFans(10, 5);
    REQUIRE(normal.Choose(state, &scenario).kind == ActionIntentKind::Race);
    submitRace(raceSource());
    observeFans(11, 4);
    REQUIRE(normal.Choose(state, &scenario).kind == ActionIntentKind::Race);
    submitRace(raceSource());
    observeFans(12, 3);
    REQUIRE(normal.Choose(state, &scenario).kind == ActionIntentKind::Training);

    // The next confirmed non-race action resets the streak, then the optional
    // fans race may be selected again on the following turn.
    CareerObservation trainingSource;
    trainingSource.screenId = "training_selection";
    trainingSource.screenKind = CareerScreenKind::TrainingSelect;
    scenario.OnTurnActionSubmitted(
        state,
        ActionIntent::MakeTraining("speed"),
        trainingSource);
    observeFans(13, 2);
    REQUIRE(normal.Choose(state, &scenario).kind == ActionIntentKind::Race);

    // At the goal deadline the same two-race streak cannot defer the goal.
    submitRace(raceSource());
    observeFans(14, 1);
    submitRace(raceSource());
    observeFans(15, 1);
    REQUIRE(normal.Choose(state, &scenario).kind == ActionIntentKind::Race);

    CareerObservation finale;
    finale.screenId = "goal_complete";
    finale.screenKind = CareerScreenKind::GoalUpdate;
    scenario.OnObserve(state, finale);
    const auto finaleIntent = normal.Choose(state, &scenario);
    REQUIRE(finaleIntent.kind == ActionIntentKind::Race);
    REQUIRE(finaleIntent.raceTarget == "finale");
}

TEST_CASE("race warning resolution uses current objective evidence", "[CareerDomainParity]")
{
    UraScenarioModule scenario;
    CareerState state;
    CareerObservation main;
    main.screenId = "career_main";
    main.screenKind = CareerScreenKind::MainTurn;
    main.score = 0.96;
    main.turnIndexEvidence = ObservedValue<int>::Make(10, 0.95, ObservationSource::Ocr);
    main.turnsToGoalEvidence = ObservedValue<int>::Make(4, 0.95, ObservationSource::Ocr);
    main.fansToGoalEvidence = ObservedValue<int>::Make(1200, 0.95, ObservationSource::Ocr);
    main.goalTextEvidence = ObservedValue<std::string>::Make(
        "Earn 1,200 fans to go", 0.95, ObservationSource::Ocr);
    scenario.OnObserve(state, main);

    CareerObservation warning;
    warning.screenId = "race_streak_warning";
    warning.screenKind = CareerScreenKind::RaceEntry;
    warning.score = 0.96;
    warning.goalTextEvidence = ObservedValue<std::string>::Make(
        "Earn 1,200 fans to go", 0.95, ObservationSource::Ocr);
    scenario.OnObserve(state, warning);
    REQUIRE(scenario.GetRaceStreakWarningResolution(state)
        == RaceStreakWarningResolution::CancelRace);

    // A current mandatory race banner overrides the earlier optional fans
    // objective even when the warning appears on top of the same screen.
    warning.goalTextEvidence = ObservedValue<std::string>::Make(
        "Run in the Junior Stakes", 0.97, ObservationSource::Ocr);
    scenario.OnObserve(state, warning);
    REQUIRE(scenario.GetRaceStreakWarningResolution(state)
        == RaceStreakWarningResolution::ConfirmRace);
}

TEST_CASE("race detail OCR cannot erase reliable grade race progress", "[CareerDomainParity]")
{
    UraScenarioModule scenario;
    ScenarioInitialization schedule;
    schedule.raceSchedule = {{25, "G3", true}, {26, "G3", true}};
    scenario.Initialize(schedule);

    CareerState state;
    CareerObservation main;
    main.screenId = "career_main";
    main.screenKind = CareerScreenKind::MainTurn;
    main.score = 0.96;
    main.turnIndexEvidence = ObservedValue<int>::Make(25, 0.95, ObservationSource::Ocr);
    main.turnsToGoalEvidence = ObservedValue<int>::Make(3, 0.95, ObservationSource::Ocr);
    main.goalTextEvidence = ObservedValue<std::string>::Make(
        "Run in 2 times left G3", 0.95, ObservationSource::Ocr);
    scenario.OnObserve(state, main);

    CareerObservation raceDetails;
    raceDetails.screenId = "race_details";
    raceDetails.screenKind = CareerScreenKind::RaceEntry;
    raceDetails.score = 0.96;
    raceDetails.goalTextEvidence = ObservedValue<std::string>::Make(
        "G3 race list", 0.96, ObservationSource::Ocr);
    scenario.OnObserve(state, raceDetails);

    REQUIRE(scenario.GetRaceStreakWarningResolution(state)
        == RaceStreakWarningResolution::ConfirmRace);
}

TEST_CASE("missing grade race schedule remains unknown at streak warning", "[CareerDomainParity]")
{
    UraScenarioModule scenario;
    CareerState state;
    CareerObservation main;
    main.screenId = "career_main";
    main.screenKind = CareerScreenKind::MainTurn;
    main.score = 0.96;
    main.turnIndexEvidence = ObservedValue<int>::Make(25, 0.95, ObservationSource::Ocr);
    main.turnsToGoalEvidence = ObservedValue<int>::Make(3, 0.95, ObservationSource::Ocr);
    main.goalTextEvidence = ObservedValue<std::string>::Make(
        "Run in 2 times left G3", 0.95, ObservationSource::Ocr);
    scenario.OnObserve(state, main);

    CareerObservation warning;
    warning.screenId = "race_streak_warning";
    warning.screenKind = CareerScreenKind::RaceEntry;
    warning.score = 0.96;
    warning.goalTextEvidence = main.goalTextEvidence;
    scenario.OnObserve(state, warning);

    // No catalog schedule means the remaining qualifying first-card races
    // cannot be counted, so a resumed warning must be cancelled and observed
    // again instead of assuming the grade goal is mandatory now.
    REQUIRE(scenario.GetRaceStreakWarningResolution(state)
        == RaceStreakWarningResolution::CancelRace);
}

TEST_CASE("confirmed grade race results consume observed goal counts", "[CareerDomainParity]")
{
    UraScenarioModule scenario;
    ScenarioInitialization schedule;
    schedule.raceSchedule = {
        {25, "G3", true}, {26, "G3", true}, {27, "G3", true}};
    scenario.Initialize(schedule);

    CareerState state;
    DefaultTrainingStrategy normal("speed", 45);
    const auto observeGradeGoal = [&](int turn, int countLeft, int turnsLeft) {
        CareerObservation observation;
        observation.screenId = "career_main";
        observation.screenKind = CareerScreenKind::MainTurn;
        observation.score = 0.96;
        observation.energyPercent = ObservedValue<int>::Make(
            75, 0.95, ObservationSource::Ocr);
        observation.turnIndexEvidence = ObservedValue<int>::Make(
            turn, 0.95, ObservationSource::Ocr);
        observation.turnsToGoalEvidence = ObservedValue<int>::Make(
            turnsLeft, 0.95, ObservationSource::Ocr);
        observation.goalTextEvidence = ObservedValue<std::string>::Make(
            "Run in " + std::to_string(countLeft) + " times left G3",
            0.95, ObservationSource::Ocr);
        scenario.OnObserve(state, observation);
    };
    CareerObservation raceResult;
    raceResult.screenId = "race_runner_result";
    raceResult.screenKind = CareerScreenKind::RaceResult;
    const auto submitConfirmedRace = [&] {
        scenario.OnTurnActionSubmitted(
            state,
            ActionIntent::MakeRace("grade_race_count"),
            raceResult);
    };

    observeGradeGoal(25, 2, 4);
    const auto first = normal.Choose(state, &scenario);
    REQUIRE(first.kind == ActionIntentKind::Race);
    REQUIRE(first.raceTarget == "grade_race_count");
    submitConfirmedRace();

    // The next observed Main screen consumes one result and still requires
    // another qualifying first-card race.
    observeGradeGoal(26, 1, 3);
    const auto second = normal.Choose(state, &scenario);
    REQUIRE(second.kind == ActionIntentKind::Race);
    REQUIRE(second.raceTarget == "grade_race_count");
    submitConfirmedRace();

    // A zero remaining count clears the pending race after the next confirmed
    // turn boundary, even though the banner still names a graded-race goal.
    observeGradeGoal(27, 0, 2);
    REQUIRE(normal.Choose(state, &scenario).kind == ActionIntentKind::Training);
}

TEST_CASE("continue-existing career starts at the optional race streak limit", "[CareerDomainParity]")
{
    UraScenarioModule scenario;
    CareerState state;
    scenario.OnRunStart(state, true);

    CareerObservation fans;
    fans.screenId = "career_main";
    fans.screenKind = CareerScreenKind::MainTurn;
    fans.score = 0.96;
    fans.energyPercent = ObservedValue<int>::Make(75, 0.95, ObservationSource::Ocr);
    fans.turnIndexEvidence = ObservedValue<int>::Make(12, 0.95, ObservationSource::Ocr);
    fans.turnsToGoalEvidence = ObservedValue<int>::Make(4, 0.95, ObservationSource::Ocr);
    fans.fansToGoalEvidence = ObservedValue<int>::Make(2500, 0.95, ObservationSource::Ocr);
    fans.goalTextEvidence = ObservedValue<std::string>::Make(
        "Earn 2,500 fans to go", 0.95, ObservationSource::Ocr);
    scenario.OnObserve(state, fans);

    DefaultTrainingStrategy normal("speed", 45);
    REQUIRE(normal.Choose(state, &scenario).kind == ActionIntentKind::Training);
}

TEST_CASE("URA race result gate requires matching scenario evidence and advances finale stages", "[CareerDomainParity][RaceResult]")
{
    ScenarioInitialization initialization;
    initialization.scenarioObjectives = {
        {"opening", 0, "race_result", std::string("race_opening"), {},
            std::string("fans_gate")},
        {"fans_gate", 1, "fans", std::nullopt, {}, std::string("finale_chain"),
            std::nullopt, std::nullopt, 1000},
        {"finale_chain", 2, "chain_complete"},
        {"finale_one", 3, "race_result", std::string("race_final_one")},
        {"finale_two", 4, "race_result", std::string("race_final_two")},
    };
    initialization.finalSeriesStages = {"finale_one", "finale_two"};
    const auto race = [](std::string id, int placement) {
        ScenarioRaceDefinition value;
        value.raceId = std::move(id);
        value.runtimeFrameCatalogId = "career.runtime_frame:" + value.raceId;
        value.expectedPlacement = placement;
        value.resultConfidence = 0.90;
        return value;
    };
    auto openingRace = race("race_opening", 1);
    openingRace.rewardFans = 1000;
    initialization.scenarioRaces = {
        openingRace, race("race_final_one", 2), race("race_final_two", 3)};

    UraScenarioModule scenario;
    scenario.Initialize(initialization);
    CareerState state;
    scenario.OnRunStart(state);
    // Match managed value semantics: a stored total remains useful even when
    // its confidence is below the threshold used for action decisions.
    state.fans = ObservedValue<int>::Make(50, 0.0, ObservationSource::Ocr);
    const auto initial = scenario.GetCurrentRaceResultExpectation(state);
    REQUIRE(initial.has_value());
    REQUIRE(initial->raceId == "race_opening");
    REQUIRE(initial->runtimeFrameCatalogId == "career.runtime_frame:race_opening");
    REQUIRE(initial->expectedPlacement == 1);

    const auto wrongRace = scenario.OnRaceResultConfirmed(
        state, {"unrelated_race", 1, 0.95});
    REQUIRE_FALSE(wrongRace.applied);
    REQUIRE(state.racePlacements.empty());

    const auto opening = scenario.OnRaceResultConfirmed(
        state, {"race_opening", 1, 0.80});
    REQUIRE(opening.applied);
    REQUIRE_FALSE(opening.retryPending);
    REQUIRE(state.currentObjectiveId == "finale_one");
    REQUIRE(state.phaseId == "finale_underway");
    REQUIRE(state.turnIndex == 1);
    REQUIRE(state.turnIndexEvidence.source == ObservationSource::Derived);
    REQUIRE(state.turnIndexEvidence.confidence == 0.5);
    REQUIRE(state.racePlacements.at("race_opening") == 1);
    REQUIRE(state.fans.Value() == 1050);
    REQUIRE(std::abs(state.fans.confidence - 0.64) < 1e-9);
    REQUIRE(state.completedObjectiveIds.size() == 2);
    REQUIRE(state.completedObjectiveIds[0] == "opening");
    REQUIRE(state.completedObjectiveIds[1] == "fans_gate");

    const auto firstFinale = scenario.GetCurrentRaceResultExpectation(state);
    REQUIRE(firstFinale.has_value());
    REQUIRE(firstFinale->raceId == "race_final_one");
    const auto firstFinaleResult = scenario.OnRaceResultConfirmed(
        state, {"race_final_one", 2, 0.85});
    REQUIRE(firstFinaleResult.applied);
    REQUIRE(state.currentObjectiveId == "finale_two");

    const auto lastFinaleResult = scenario.OnRaceResultConfirmed(
        state, {"race_final_two", 3, 0.85});
    REQUIRE(lastFinaleResult.applied);
    REQUIRE(state.phaseId == "finished");
    REQUIRE(state.currentRaceId.empty());
    REQUIRE_FALSE(scenario.GetCurrentRaceResultExpectation(state).has_value());
}

TEST_CASE("URA race result retries count the terminal failed attempt", "[CareerDomainParity][RaceResult]")
{
    ScenarioInitialization initialization;
    ScenarioObjectiveDefinition objective;
    objective.objectiveId = "target_race";
    objective.order = 0;
    objective.kind = "race_result";
    objective.raceId = "race";
    objective.targetPlacement = 1;
    initialization.scenarioObjectives.push_back(objective);

    ScenarioRaceDefinition race;
    race.raceId = "race";
    race.runtimeFrameCatalogId = "career.runtime_frame:race";
    race.expectedPlacement = 2;
    race.retryEnabled = true;
    race.maxRetryCount = 1;
    initialization.scenarioRaces.push_back(race);

    UraScenarioModule scenario;
    scenario.Initialize(initialization);
    CareerState state;
    scenario.OnRunStart(state);

    const auto first = scenario.OnRaceResultConfirmed(state, {"race", 2, 0.9});
    REQUIRE(first.applied);
    REQUIRE(first.retryPending);
    REQUIRE(state.raceRetryCount == 1);
    REQUIRE(state.currentRaceId == "race");

    const auto terminal = scenario.OnRaceResultConfirmed(state, {"race", 2, 0.9});
    REQUIRE_FALSE(terminal.applied);
    REQUIRE_FALSE(terminal.retryPending);
    REQUIRE(state.raceRetryCount == 2);
}

TEST_CASE("URA grade race count advances to the next unattempted race", "[CareerDomainParity][RaceResult]")
{
    ScenarioInitialization initialization;
    ScenarioObjectiveDefinition count;
    count.objectiveId = "grade_count";
    count.order = 0;
    count.kind = "race_result_count";
    count.observedRaceIds = {"race_one", "race_two"};
    count.targetCount = 2;
    count.targetPlacementAtMost = 3;
    initialization.scenarioObjectives.push_back(count);

    const auto race = [](std::string id, int placement) {
        ScenarioRaceDefinition value;
        value.raceId = std::move(id);
        value.runtimeFrameCatalogId = "career.runtime_frame:" + value.raceId;
        value.expectedPlacement = placement;
        return value;
    };
    initialization.scenarioRaces = {race("race_one", 1), race("race_two", 2)};

    UraScenarioModule scenario;
    scenario.Initialize(initialization);
    CareerState state;
    scenario.OnRunStart(state);

    const auto first = scenario.OnRaceResultConfirmed(state, {"race_one", 1, 0.9});
    REQUIRE(first.applied);
    REQUIRE(first.retryPending);
    REQUIRE(state.currentRaceId == "race_two");
    REQUIRE(state.racePlacements.at("race_one") == 1);

    const auto second = scenario.OnRaceResultConfirmed(state, {"race_two", 2, 0.9});
    REQUIRE(second.applied);
    REQUIRE_FALSE(second.retryPending);
    REQUIRE(state.completedObjectiveIds.size() == 1);
    REQUIRE(state.completedObjectiveIds.front() == "grade_count");
}

} // namespace

TEST_CASE("Declined race replay cannot arm the successful objective probe", "[CareerDomainParity][CareerAlarmHandoff]")
{
    UraScenarioModule scenario;
    CareerState state;
    scenario.OnRunStart(state, false);
    CareerObservation main;
    main.screenId = "career_main";
    main.screenKind = CareerScreenKind::MainTurn;
    main.goalTextEvidence = ObservedValue<std::string>::Make("Run in Junior Make Debut");
    main.turnsToGoalEvidence = ObservedValue<int>::Make(0);
    scenario.OnObserve(state, main);
    scenario.OnRaceReplayCompleted(state);
    REQUIRE(scenario.IsGoalCompletionProbeArmed(state));
    state.raceRetryDeclined = true;
    scenario.OnRaceReplayCompleted(state);
    CHECK(state.raceReplayFlowCompleted);
    CHECK_FALSE(scenario.IsGoalCompletionProbeArmed(state));
    CHECK_FALSE(state.lastRacePlacement.HasValue());
    CHECK(state.completedObjectiveIds.empty());
}

TEST_CASE("An empty race list blocks the current turn and becomes eligible on the next turn", "[CareerDomainParity][CareerParity]")
{
    for (int const turn : {0, 12})
    {
        CAPTURE(turn);
        UraScenarioModule scenario;
        CareerState state;
        scenario.OnRunStart(state, false);
        DefaultTrainingStrategy strategy;
        CareerObservation main;
        main.screenId = "career_main";
        main.screenKind = CareerScreenKind::MainTurn;
        main.turnIndexEvidence = ObservedValue<int>::Make(turn);
        main.energyPercent = ObservedValue<int>::Make(80);
        main.goalTextEvidence = ObservedValue<std::string>::Make("Run in Junior Make Debut");
        main.turnsToGoalEvidence = ObservedValue<int>::Make(0);
        scenario.OnObserve(state, main);
        REQUIRE(strategy.Choose(state, &scenario).kind == ActionIntentKind::Race);
        CareerObservation empty;
        empty.screenId = "race_list_empty";
        empty.screenKind = CareerScreenKind::RaceEntry;
        scenario.OnObserve(state, empty);
        scenario.OnObserve(state, main);
        CHECK(strategy.Choose(state, &scenario).kind == ActionIntentKind::Training);
        main.turnIndexEvidence = ObservedValue<int>::Make(turn + 1);
        scenario.OnObserve(state, main);
        CHECK(strategy.Choose(state, &scenario).kind == ActionIntentKind::Race);
    }
}

TEST_CASE("The inheritance boundary blocks another action even when energy is low", "[CareerDomainParity][CareerParity]")
{
    for (int const turn : {30, 54})
    {
        UraScenarioModule scenario;
        CareerState state;
        scenario.OnRunStart(state, false);
        CareerObservation main;
        main.screenId = "career_main";
        main.screenKind = CareerScreenKind::MainTurn;
        main.turnIndexEvidence = ObservedValue<int>::Make(turn);
        main.energyPercent = ObservedValue<int>::Make(20);
        scenario.OnObserve(state, main);
        CareerObservation drawer;
        drawer.screenId = "training_selection";
        drawer.screenKind = CareerScreenKind::TrainingSelect;
        scenario.OnTurnActionSubmitted(state, ActionIntent::MakeTraining("speed"), drawer);
        main.turnIndexEvidence = ObservedValue<int>::Make(turn + 1);
        scenario.OnObserve(state, main);
        DefaultTrainingStrategy strategy;
        CHECK(strategy.Choose(state, &scenario).kind == ActionIntentKind::DoNothing);
        UraSmartTrainingStrategy smart;
        CHECK(smart.Choose(state, &scenario).kind == ActionIntentKind::DoNothing);
        CareerObservation inheritance;
        inheritance.screenId = "inheritance_event";
        inheritance.screenKind = CareerScreenKind::EventChoice;
        scenario.OnObserve(state, inheritance);
        scenario.OnObserve(state, main);
        CHECK(strategy.Choose(state, &scenario).kind == ActionIntentKind::Rest);
    }
}


