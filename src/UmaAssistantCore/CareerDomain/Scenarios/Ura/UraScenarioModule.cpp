#include "UraScenarioModule.hpp"

#include "UmaAssistantCore/CareerDomain/CareerCalendar.hpp"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <memory>
#include <string>
#include <utility>

namespace UmaAssistant::CareerDomain
{

namespace
{

std::string Lower(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(),
        [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return value;
}

int GradeRank(const std::string& grade)
{
    const auto normalized = Lower(grade);
    if (normalized == "g1") return 1;
    if (normalized == "g2") return 2;
    if (normalized == "g3") return 3;
    return 0;
}

bool IsSummerCamp(const CareerState& state)
{
    if (!state.turnPositionText.has_value())
        return false;
    const auto text = Lower(*state.turnPositionText);
    const bool eligibleYear = text.find("classic") != std::string::npos ||
                              text.find("senior") != std::string::npos;
    const bool summerMonth = text.find("month 7") != std::string::npos ||
                             text.find("month 8") != std::string::npos ||
                             text.find("july") != std::string::npos ||
                             text.find("august") != std::string::npos ||
                             text.find("jul ") != std::string::npos ||
                             text.find("aug ") != std::string::npos ||
                             text.find("7月") != std::string::npos ||
                             text.find("8月") != std::string::npos;
    return eligibleYear && summerMonth;
}

template <typename T>
ObservedValue<T> FromObservation(
    const ObservedValue<T>& evidence,
    const std::optional<T>& legacy,
    double score)
{
    if (evidence.HasValue())
        return evidence;
    if (legacy.has_value())
        return ObservedValue<T>::Make(*legacy, score, ObservationSource::Ocr);
    return ObservedValue<T>::Unknown();
}

template <typename T>
ObservedValue<T> ParsedFromText(
    std::optional<T> value,
    const ObservedValue<std::string>& text)
{
    if (!value.has_value() || !text.IsReliable())
        return ObservedValue<T>::Unknown();
    return ObservedValue<T>::Make(
        std::move(*value), text.confidence, text.source, "parsed from observed goal text");
}

ObservedValue<std::string> GoalTextFromObservation(const CareerObservation& observation)
{
    if (observation.goalTextEvidence.HasValue())
        return observation.goalTextEvidence;
    if (observation.goalText.has_value())
        return ObservedValue<std::string>::Make(
            *observation.goalText, observation.score, ObservationSource::Ocr);
    return ObservedValue<std::string>::Unknown();
}

bool IsActionSource(ActionIntentKind action, const CareerObservation& source)
{
    const auto id = Lower(source.screenId);
    switch (action)
    {
    case ActionIntentKind::Training:
        return source.screenKind == CareerScreenKind::TrainingSelect
            || source.screenKind == CareerScreenKind::TrainingResult
            || id == "training_selection" || id == "training_result";
    case ActionIntentKind::Rest:
        return source.screenKind == CareerScreenKind::Rest
            || id == "rest_confirmation" || id == "summer_rest_confirmation";
    case ActionIntentKind::Infirmary:
        return source.screenKind == CareerScreenKind::Infirmary
            || id == "infirmary_confirmation";
    case ActionIntentKind::Recreation:
        return source.screenKind == CareerScreenKind::Recreation
            || id == "recreation_confirmation" || id == "recreation_selection";
    case ActionIntentKind::Race:
        return source.screenKind == CareerScreenKind::RaceEntry
            || source.screenKind == CareerScreenKind::RaceExecution
            || source.screenKind == CareerScreenKind::RaceResult
            || id == "race_day" || id == "race_list" || id == "race_runner"
            || id == "race_result" || id == "race_runner_result";
    default:
        return false;
    }
}

} // namespace

UraScenarioModule::UraScenarioModule() = default;

void UraScenarioModule::Initialize(const ScenarioInitialization& initialization)
{
    m_firstCardGradeRanks.clear();
    m_scenarioTrainingRaceDistanceFallback = initialization.scenarioTrainingRaceDistanceFallback;
    for (const auto& entry : initialization.raceSchedule)
    {
        const int gradeRank = GradeRank(entry.grade);
        if (entry.topCardAvailable && entry.turnIndex > 0 && gradeRank > 0)
            m_firstCardGradeRanks.emplace(entry.turnIndex, gradeRank);
    }

    m_trainingRaceDistances.clear();
    for (const auto& entry : initialization.trainingRaceDistances)
    {
        if (entry.turnIndex > 0)
            m_trainingRaceDistances.push_back(entry);
    }
    std::stable_sort(m_trainingRaceDistances.begin(), m_trainingRaceDistances.end(),
        [](const auto& left, const auto& right) {
            if (left.turnIndex != right.turnIndex)
                return left.turnIndex < right.turnIndex;
            if (left.objectiveOrder != right.objectiveOrder)
                return left.objectiveOrder < right.objectiveOrder;
            return left.raceOrder < right.raceOrder;
        });

    m_scenarioObjectives.clear();
    for (const auto& objective : initialization.scenarioObjectives)
        if (!objective.objectiveId.empty())
            m_scenarioObjectives.emplace(Lower(objective.objectiveId), objective);

    m_scenarioRaces.clear();
    for (const auto& race : initialization.scenarioRaces)
        if (!race.raceId.empty())
            m_scenarioRaces.emplace(Lower(race.raceId), race);

    m_finalSeriesStages = initialization.finalSeriesStages;
    m_finaleStageIndex = 0;
}

void UraScenarioModule::OnRunStart(CareerState& state, bool continueExistingCareer)
{
    m_goal = {};
    m_currentTurnIndexObserved = false;
    m_currentTurnIndex = 0;
    m_hasPendingRace = false;
    m_unavailableRaceTurn.reset();
    m_inheritancePending = false;
    m_isFinale = false;
    m_consecutiveRaceTurns = continueExistingCareer ? 2 : 0;
    m_pendingActionKind.reset();
    m_pendingActionTurnIndex.reset();
    m_pendingActionTurnsToGoal = ObservedValue<int>::Unknown();
    m_gradeRaceStartedTurnIndex.reset();
    m_goalCompletionProbeArmed = false;
    m_goalKindAtRaceStart = CareerGoalKind::Unknown;
    m_gradeRaceCountAtRaceStart = ObservedValue<int>::Unknown();
    m_finaleStageIndex = 0;

    state.currentObjectiveId.clear();
    state.currentRaceId.clear();
    state.raceRetryCount = 0;
    state.raceRetryDeclined = false;
    state.racePlacements.clear();
    state.completedObjectiveIds.clear();
    state.fans = ObservedValue<int>::Unknown();
    state.lastRacePlacement = ObservedValue<int>::Unknown();

    const auto first = std::min_element(m_scenarioObjectives.begin(), m_scenarioObjectives.end(),
        [](const auto& left, const auto& right) {
            if (left.second.order != right.second.order)
                return left.second.order < right.second.order;
            return left.first < right.first;
        });
    if (first != m_scenarioObjectives.end())
    {
        state.currentObjectiveId = first->second.objectiveId;
        state.currentRaceId = ResolveCurrentRaceId(state);
    }
}

void UraScenarioModule::OnTurnStart(CareerState& state)
{
    state.careerStarted = true;
    if (state.phaseId.empty() || state.phaseId == "Debut")
        state.phaseId = "career";

    // A main screen can be observed repeatedly while the UI settles. A turn
    // advances only from a distinct, confidence-qualified calendar reading.
    if (!state.turnIndexEvidence.IsReliable())
        return;

    const int observedTurn = state.turnIndexEvidence.Value();
    if (state.lastStartedTurnIndex == observedTurn)
        return;
    state.turnIndex = observedTurn;
    state.lastStartedTurnIndex = observedTurn;
}

void UraScenarioModule::OnObserve(CareerState& state, const CareerObservation& observation)
{
    const auto screenId = Lower(observation.screenId);
    state.UpdateFromObservation(observation);
    if (screenId == "inheritance_event") m_inheritancePending = false;

    if (screenId == "race_list_empty")
    {
        m_unavailableRaceTurn = state.turnIndex;
        m_hasPendingRace = false;
        m_goalCompletionProbeArmed = false;
    }

    if (observation.screenKind == CareerScreenKind::MainTurn || screenId == "career_main")
    {
        state.careerStarted = true;
        // Objective completion is a per-goal edge. The next main page is a
        // new goal boundary, and unreadable fields are unknown for this turn.
        state.objectiveCompleted = ObservedValue<bool>::Unknown();
        if (!m_isFinale)
            state.phaseId = "career";

        ConfirmTurnAdvance(state, observation);
        RefreshMainTurnEvidence(state, observation);

        if (!state.currentObjectiveId.empty())
            state.currentRaceId = ResolveCurrentRaceId(state);

        if (m_goalCompletionProbeArmed)
        {
            const bool goalChanged = m_goal.kind != CareerGoalKind::Unknown
                && (m_goal.kind != m_goalKindAtRaceStart
                    || (m_goalKindAtRaceStart == CareerGoalKind::GradeRaceCount
                        && m_goal.gradeRaceCountLeft.IsReliable()
                        && m_gradeRaceCountAtRaceStart.IsReliable()
                        && m_goal.gradeRaceCountLeft.Value()
                            < m_gradeRaceCountAtRaceStart.Value()));
            if (goalChanged || (m_currentTurnIndexObserved && m_gradeRaceStartedTurnIndex
                && m_currentTurnIndex > *m_gradeRaceStartedTurnIndex))
                m_goalCompletionProbeArmed = false;
        }

        if (m_isFinale || Lower(state.phaseId) == "finale_underway")
        {
            m_isFinale = true;
            state.phaseId = "finale_underway";
            if (m_goal.kind == CareerGoalKind::Unknown)
            {
                // Unknown OCR must not invent a pending race. This matches
                // the managed scenario's OCR-owned objective handling.
                m_hasPendingRace = false;
            }
            else if (m_goal.kind == CareerGoalKind::Race)
            {
                m_hasPendingRace = m_goal.turnsToGoal.IsReliable()
                    && m_goal.turnsToGoal.Value() <= 0;
            }
            else if (m_goal.kind == CareerGoalKind::Fans)
            {
                m_hasPendingRace = m_goal.fansToGoal.IsReliable()
                    && m_goal.fansToGoal.Value() > 0;
            }
            else if (m_goal.kind == CareerGoalKind::GradeRaceCount)
            {
                m_hasPendingRace = m_goal.gradeRaceCountLeft.IsReliable()
                    && m_goal.gradeRaceCountLeft.Value() > 0
                    && m_goal.turnsToGoal.IsReliable()
                    && m_goal.turnsToGoal.Value() > 0
                    && m_currentTurnIndexObserved
                    && m_goal.targetRaceGrade.IsReliable()
                    && HasQualifyingFirstCard(
                        m_currentTurnIndex, m_goal.targetRaceGrade.Value());
            }
            else
            {
                m_hasPendingRace = false;
            }
        }
        else
        {
            m_hasPendingRace = false;
            switch (m_goal.kind)
            {
            case CareerGoalKind::Race:
                m_hasPendingRace = m_goal.turnsToGoal.IsReliable()
                    && m_goal.turnsToGoal.Value() <= 0;
                break;
            case CareerGoalKind::Fans:
                m_hasPendingRace = m_goal.fansToGoal.IsReliable()
                    && m_goal.fansToGoal.Value() > 0;
                break;
            case CareerGoalKind::GradeRaceCount:
                m_hasPendingRace = m_goal.gradeRaceCountLeft.IsReliable()
                    && m_goal.gradeRaceCountLeft.Value() > 0
                    && m_goal.turnsToGoal.IsReliable()
                    && m_goal.turnsToGoal.Value() > 0
                    && m_currentTurnIndexObserved
                    && m_goal.targetRaceGrade.IsReliable()
                    && HasQualifyingFirstCard(
                        m_currentTurnIndex, m_goal.targetRaceGrade.Value());
                break;
            default:
                break;
            }
        }
        if (m_unavailableRaceTurn && *m_unavailableRaceTurn == state.turnIndex)
            m_hasPendingRace = false;
        return;
    }

    if (screenId == "goal_complete")
    {
        // This banner means all career goals are complete; a per-objective
        // completion is reported on a separate screen and does not start URA.
        m_isFinale = true;
        m_hasPendingRace = true;
        m_goalCompletionProbeArmed = false;
        state.phaseId = "finale_underway";
        m_finaleStageIndex = 0;
        if (!m_finalSeriesStages.empty())
        {
            state.currentObjectiveId = m_finalSeriesStages.front();
            state.currentRaceId = ResolveCurrentRaceId(state);
        }
    }
    else if (screenId == "goal_objective_complete"
        || (observation.objectiveCompleted.IsReliable() && observation.objectiveCompleted.Value()))
    {
        m_hasPendingRace = false;
        m_goalCompletionProbeArmed = false;
        state.phaseId = "GoalComplete";
    }
    else if (screenId == "career_complete" || screenId == "career_complete_close")
    {
        m_isFinale = false;
        m_hasPendingRace = false;
        m_goalCompletionProbeArmed = false;
        state.phaseId = "career_complete";
        state.currentRaceId.clear();
    }

    if (observation.screenKind == CareerScreenKind::RaceEntry
        || observation.screenKind == CareerScreenKind::RaceExecution
        || observation.screenKind == CareerScreenKind::RaceResult
        || screenId == "race_day" || screenId == "race_list"
        || screenId == "race_runner" || screenId == "race_result"
        || screenId == "race_streak_warning" || screenId == "race_details"
        || screenId == "race_attributes")
    {
        const auto text = GoalTextFromObservation(observation);
        if (text.IsReliable())
        {
            const auto parsedKind = CareerGoalTextParser::Classify(text.Value());
            if (parsedKind != CareerGoalKind::Unknown
                && m_goal.kind != CareerGoalKind::GradeRaceCount)
            {
                m_goal.kind = parsedKind;
                m_goal.text = text;
            }
            if (m_goal.kind == CareerGoalKind::GradeRaceCount)
            {
                const auto count = ParsedFromText(
                    CareerGoalTextParser::ParseRaceCountLeft(text.Value()), text);
                if (count.IsReliable())
                    m_goal.gradeRaceCountLeft = count;
                if (!m_goal.targetRaceGrade.IsReliable())
                    m_goal.targetRaceGrade = ParsedFromText(
                        CareerGoalTextParser::ParseRaceGrade(text.Value()), text);
            }
        }
        m_hasPendingRace = true;
        if (state.currentRaceId.empty())
            state.currentRaceId = ResolveCurrentRaceId(state);
    }

    if (screenId == "race_streak_warning")
    {
        // The warning itself proves that a third consecutive race is being
        // attempted, including when resuming into the modal.
        m_consecutiveRaceTurns = 2;
        m_hasPendingRace = true;
    }
}

void UraScenarioModule::OnTurnActionSubmitted(
    CareerState& state,
    const ActionIntent& intent,
    const CareerObservation& source)
{
    if (!IsActionSource(intent.kind, source))
        return;

    if (m_currentTurnIndexObserved && (m_currentTurnIndex == 30 || m_currentTurnIndex == 54))
        m_inheritancePending = true;

    m_pendingActionKind = intent.kind;
    m_pendingActionTurnIndex = m_currentTurnIndexObserved
        ? m_currentTurnIndex : state.turnIndex;
    m_pendingActionTurnsToGoal = m_goal.turnsToGoal;
    if (intent.kind == ActionIntentKind::Race)
    {
        m_goalCompletionProbeArmed = true;
        m_goalKindAtRaceStart = m_goal.kind;
        m_gradeRaceCountAtRaceStart = m_goal.gradeRaceCountLeft;
        m_gradeRaceStartedTurnIndex = m_pendingActionTurnIndex;
    }
}

std::optional<RaceStreakWarningResolution>
UraScenarioModule::GetRaceStreakWarningResolution(const CareerState& state) const
{
    (void)state;
    return MustRaceNow()
        ? RaceStreakWarningResolution::ConfirmRace
        : RaceStreakWarningResolution::CancelRace;
}

std::optional<TrainingRaceDistance>
UraScenarioModule::GetCurrentTrainingRaceDistance(const CareerState& state) const
{
    if (!state.currentRaceId.empty())
    {
        const auto requestedRaceId = Lower(state.currentRaceId);
        const auto globalCurrent = std::find_if(
            m_trainingRaceDistances.begin(), m_trainingRaceDistances.end(),
            [&requestedRaceId](const CareerTrainingRaceDistanceEntry& entry) {
                return !entry.raceId.empty() && Lower(entry.raceId) == requestedRaceId;
            });
        if (globalCurrent != m_trainingRaceDistances.end())
            return globalCurrent->distance;
    }

    // The managed smart strategy resolves a trainee's next global race before
    // it falls back to the scenario's own current race. Keep that order even
    // when OnRunStart/OnObserve has populated currentRaceId from scenario data.
    const auto current = std::lower_bound(
        m_trainingRaceDistances.begin(), m_trainingRaceDistances.end(), state.turnIndex,
        [](const CareerTrainingRaceDistanceEntry& entry, int turnIndex) {
            return entry.turnIndex < turnIndex;
        });
    if (current != m_trainingRaceDistances.end())
        return current->distance;

    if (!state.currentRaceId.empty())
    {
        if (const auto* race = FindRace(state.currentRaceId))
            return TrainingRaceDistance{race->distanceBand, race->distanceMeters};
    }
    const auto scenarioRaceId = ResolveCurrentRaceId(state);
    if (!scenarioRaceId.empty())
    {
        if (const auto* race = FindRace(scenarioRaceId))
            return TrainingRaceDistance{race->distanceBand, race->distanceMeters};
    }
    if (m_isFinale || Lower(state.phaseId) == "finale_underway")
        return m_scenarioTrainingRaceDistanceFallback;
    return std::nullopt;
}

const ScenarioObjectiveDefinition* UraScenarioModule::FindObjective(const std::string& id) const noexcept
{
    const auto it = m_scenarioObjectives.find(Lower(id));
    return it == m_scenarioObjectives.end() ? nullptr : &it->second;
}

const ScenarioRaceDefinition* UraScenarioModule::FindRace(const std::string& id) const noexcept
{
    const auto it = m_scenarioRaces.find(Lower(id));
    return it == m_scenarioRaces.end() ? nullptr : &it->second;
}

std::string UraScenarioModule::ResolveCurrentRaceId(const CareerState& state) const
{
    if (m_isFinale || Lower(state.phaseId) == "finale_underway")
    {
        if (m_finaleStageIndex >= 0
            && m_finaleStageIndex < static_cast<int>(m_finalSeriesStages.size()))
        {
            const auto* stage = FindObjective(m_finalSeriesStages[static_cast<std::size_t>(m_finaleStageIndex)]);
            if (stage && stage->raceId)
                return *stage->raceId;
        }
    }

    const ScenarioObjectiveDefinition* objective = FindObjective(state.currentObjectiveId);

    std::unordered_map<std::string, bool> visited;
    while (objective && visited.emplace(Lower(objective->objectiveId), true).second)
    {
        const auto kind = Lower(objective->kind);
        if (kind == "race_result" && objective->raceId)
            return *objective->raceId;
        if (kind == "race_result_count")
        {
            for (const auto& raceId : objective->observedRaceIds)
                if (state.racePlacements.find(Lower(raceId)) == state.racePlacements.end())
                    return raceId;
            if (!objective->observedRaceIds.empty())
                return objective->observedRaceIds.back();
        }
        if (kind == "chain_complete" && !m_finalSeriesStages.empty())
        {
            const auto* stage = FindObjective(m_finalSeriesStages.front());
            if (stage && stage->raceId)
                return *stage->raceId;
        }
        if (!objective->nextObjectiveId)
            break;
        objective = FindObjective(*objective->nextObjectiveId);
    }
    return {};
}

std::optional<CareerRaceResultExpectation>
UraScenarioModule::GetCurrentRaceResultExpectation(const CareerState& state) const
{
    const auto raceId = !state.currentRaceId.empty()
        ? state.currentRaceId : ResolveCurrentRaceId(state);
    const auto* race = FindRace(raceId);
    if (!race || race->runtimeFrameCatalogId.empty()
        || !race->expectedPlacement || *race->expectedPlacement < 1
        || !std::isfinite(race->resultConfidence)
        || race->resultConfidence <= 0.0 || race->resultConfidence > 1.0)
        return std::nullopt;
    return CareerRaceResultExpectation{
        race->raceId, race->runtimeFrameCatalogId, *race->expectedPlacement, race->resultConfidence};
}

bool UraScenarioModule::IsObjectiveSatisfied(
    const ScenarioObjectiveDefinition& objective,
    int placement,
    const CareerState& state) const
{
    if (objective.targetPlacement && placement != *objective.targetPlacement)
        return false;
    if (objective.targetPlacementAtMost && placement > *objective.targetPlacementAtMost)
        return false;
    if (Lower(objective.kind) == "race_result_count")
    {
        const int qualified = static_cast<int>(std::count_if(
            state.racePlacements.begin(), state.racePlacements.end(), [&](const auto& item) {
                const bool belongs = std::any_of(objective.observedRaceIds.begin(),
                    objective.observedRaceIds.end(), [&](const auto& id) {
                        return Lower(id) == item.first;
                    });
                return belongs && (!objective.targetPlacementAtMost
                    || item.second <= *objective.targetPlacementAtMost);
            }));
        return qualified >= objective.targetCount.value_or(0);
    }
    return true;
}

CareerRaceResultApplication UraScenarioModule::RetryOrFailRace(
    CareerState& state, const ScenarioRaceDefinition& race, int placement)
{
    // Count every failed observed attempt, including the terminal one.
    ++state.raceRetryCount;
    if (race.retryEnabled && state.raceRetryCount <= race.maxRetryCount)
    {
        state.currentRaceId = race.raceId;
        m_hasPendingRace = true;
        return {true, true, {}};
    }
    return {false, false, "Race '" + race.raceId + "' finished " + std::to_string(placement)
        + " and the scenario objective was not satisfied; no retry remains."};
}

void UraScenarioModule::AdvanceObjectiveAfterRace(
    CareerState& state, const ScenarioObjectiveDefinition& objective)
{
    if (std::find_if(state.completedObjectiveIds.begin(), state.completedObjectiveIds.end(),
            [&](const auto& id) { return Lower(id) == Lower(objective.objectiveId); })
        == state.completedObjectiveIds.end())
        state.completedObjectiveIds.push_back(objective.objectiveId);
    state.raceRetryCount = 0;

    if (m_isFinale)
    {
        ++m_finaleStageIndex;
        if (m_finaleStageIndex >= static_cast<int>(m_finalSeriesStages.size()))
        {
            state.phaseId = "finished";
            state.currentObjectiveId = "scenario_complete";
            state.currentRaceId.clear();
            m_hasPendingRace = false;
            return;
        }
        state.currentObjectiveId = m_finalSeriesStages[static_cast<std::size_t>(m_finaleStageIndex)];
        state.currentRaceId = ResolveCurrentRaceId(state);
        m_hasPendingRace = true;
        return;
    }

    state.currentObjectiveId = objective.nextObjectiveId.value_or("scenario_complete");
    state.currentRaceId.clear();
    AdvanceNonRaceObjectives(state);
}

void UraScenarioModule::AdvanceNonRaceObjectives(CareerState& state)
{
    // Resolve fans and chain-complete nodes synchronously, as the managed
    // scenario does after each successful race result.
    for (std::size_t visited = 0; visited <= m_scenarioObjectives.size(); ++visited)
    {
        const auto* objective = FindObjective(state.currentObjectiveId);
        if (!objective)
        {
            state.currentRaceId.clear();
            m_hasPendingRace = false;
            return;
        }

        const auto kind = Lower(objective->kind);
        if (kind == "fans" && objective->targetMinimum
            && state.fans.ValueOr(0) >= *objective->targetMinimum)
        {
            if (std::find_if(state.completedObjectiveIds.begin(), state.completedObjectiveIds.end(),
                    [&](const auto& id) { return Lower(id) == Lower(objective->objectiveId); })
                == state.completedObjectiveIds.end())
                state.completedObjectiveIds.push_back(objective->objectiveId);
            state.currentObjectiveId = objective->nextObjectiveId.value_or("scenario_complete");
            state.currentRaceId.clear();
            continue;
        }

        if (kind == "chain_complete")
        {
            if (m_finalSeriesStages.empty())
            {
                state.phaseId = "finished";
                state.currentObjectiveId = "scenario_complete";
                state.currentRaceId.clear();
                m_hasPendingRace = false;
                return;
            }
            m_isFinale = true;
            m_finaleStageIndex = 0;
            state.phaseId = "finale_underway";
            state.currentObjectiveId = m_finalSeriesStages.front();
            state.currentRaceId = ResolveCurrentRaceId(state);
            m_hasPendingRace = true;
            return;
        }

        state.currentRaceId = ResolveCurrentRaceId(state);
        m_hasPendingRace = false;
        return;
    }

    // A cycle of already-satisfied non-race objectives is malformed package
    // data. Stop with no active race instead of looping or inventing one.
    state.currentRaceId.clear();
    m_hasPendingRace = false;
}

CareerRaceResultApplication UraScenarioModule::OnRaceResultConfirmed(
    CareerState& state,
    const CareerRaceResultObservation& result)
{
    if (state.raceRetryDeclined)
        return {false, false, "Declined race retry has no confirmed race result to apply."};
    const auto expected = GetCurrentRaceResultExpectation(state);
    if (!expected || Lower(expected->raceId) != Lower(result.raceId))
        return {false, false, "Verified race result does not match the active scenario race expectation."};
    if (result.placement < 1 || result.placement != expected->expectedPlacement
        || !std::isfinite(result.confidence) || result.confidence <= 0.0 || result.confidence > 1.0)
        return {false, false, "Verified race placement is invalid or differs from scenario evidence."};

    const auto* race = FindRace(expected->raceId);
    if (!race)
        return {false, false, "Active scenario race metadata is unavailable."};
    const auto* objective = FindObjective(state.currentObjectiveId);
    if (!objective)
        return {false, false, "Active scenario objective metadata is unavailable."};
    const double confirmedConfidence = std::min(result.confidence, expected->confidence);
    if (!std::isfinite(confirmedConfidence) || confirmedConfidence <= 0.0)
        return {false, false, "Verified race result confidence is invalid."};

    // The managed scenario advances its local turn index when a recognized
    // race result is applied. A later OCR Main observation replaces this
    // derived estimate with the calendar's observed value.
    ++state.turnIndex;
    state.turnIndexEvidence = ObservedValue<int>::Make(
        state.turnIndex, 0.5, ObservationSource::Derived, "advanced after confirmed race result");
    state.lastRacePlacement = ObservedValue<int>::Make(
        result.placement, confirmedConfidence, ObservationSource::TemplateMatch,
        "verified package race-result frame");
    state.racePlacements[Lower(race->raceId)] = result.placement;
    if (race->rewardFans)
    {
        const int priorFans = state.fans.HasValue() ? state.fans.Value() : 0;
        state.fans = ObservedValue<int>::Make(
            priorFans + *race->rewardFans, confirmedConfidence * 0.8,
            ObservationSource::Derived, "scenario race reward fans");
    }

    const auto objectiveKind = Lower(objective->kind);

    if (!IsObjectiveSatisfied(*objective, result.placement, state))
    {
        if (objectiveKind == "race_result_count")
        {
            const auto next = std::find_if(objective->observedRaceIds.begin(),
                objective->observedRaceIds.end(), [&](const auto& id) {
                    return state.racePlacements.find(Lower(id)) == state.racePlacements.end();
                });
            if (next != objective->observedRaceIds.end())
            {
                state.currentRaceId = *next;
                m_hasPendingRace = true;
                return {true, true, {}};
            }
        }
        return RetryOrFailRace(state, *race, result.placement);
    }

    AdvanceObjectiveAfterRace(state, *objective);
    return {true, false, {}};
}

void UraScenarioModule::RefreshMainTurnEvidence(
    CareerState& state,
    const CareerObservation& observation)
{
    m_goal = {};
    auto text = GoalTextFromObservation(observation);
    m_goal.text = text;
    if (text.IsReliable())
    {
        m_goal.kind = CareerGoalTextParser::Classify(text.Value());
        if (m_goal.kind == CareerGoalKind::GradeRaceCount)
        {
            m_goal.gradeRaceCountLeft = ParsedFromText(
                CareerGoalTextParser::ParseRaceCountLeft(text.Value()), text);
            m_goal.targetRaceGrade = ParsedFromText(
                CareerGoalTextParser::ParseRaceGrade(text.Value()), text);
        }
    }

    m_goal.turnsToGoal = FromObservation(
        observation.turnsToGoalEvidence, observation.turnsToGoal, observation.score);
    if (!m_goal.turnsToGoal.IsReliable() && text.IsReliable())
        m_goal.turnsToGoal = ParsedFromText(
            CareerGoalTextParser::ParseTurnsLeft(text.Value()), text);

    m_goal.fansToGoal = FromObservation(
        observation.fansToGoalEvidence, observation.fansToGoal, observation.score);
    if (!m_goal.fansToGoal.IsReliable() && text.IsReliable())
        m_goal.fansToGoal = ParsedFromText(
            CareerGoalTextParser::ParseFansToGo(text.Value()), text);

    m_currentTurnIndexObserved = observation.turnIndexEvidence.IsReliable();
    if (m_currentTurnIndexObserved)
    {
        m_currentTurnIndex = observation.turnIndexEvidence.Value();
        state.turnIndex = m_currentTurnIndex;
    }
}

void UraScenarioModule::ConfirmTurnAdvance(
    CareerState& state,
    const CareerObservation& observation)
{
    if (!m_pendingActionTurnIndex.has_value() || !m_pendingActionKind.has_value())
        return;

    const auto currentTurnEvidence = FromObservation(
        observation.turnIndexEvidence, std::optional<int>{}, observation.score);
    const auto currentTurnsToGoal = [&]() {
        auto turns = FromObservation(
            observation.turnsToGoalEvidence, observation.turnsToGoal, observation.score);
        if (turns.IsReliable()) return turns;
        const auto text = GoalTextFromObservation(observation);
        return text.IsReliable()
            ? ParsedFromText(CareerGoalTextParser::ParseTurnsLeft(text.Value()), text)
            : ObservedValue<int>::Unknown();
    }();
    const bool preDebut = observation.turnPositionText.has_value()
        && Lower(*observation.turnPositionText).find("pre-debut") != std::string::npos;
    const bool countdownAdvanced = (preDebut || m_isFinale)
        && currentTurnEvidence.IsReliable()
        && currentTurnEvidence.Value() == *m_pendingActionTurnIndex
        && *m_pendingActionKind != ActionIntentKind::Race
        && m_pendingActionTurnsToGoal.IsReliable()
        && m_pendingActionTurnsToGoal.Value() > 0
        && currentTurnsToGoal.IsReliable()
        && currentTurnsToGoal.Value() == m_pendingActionTurnsToGoal.Value() - 1;

    const bool calendarAdvanced = currentTurnEvidence.IsReliable()
        && currentTurnEvidence.Value() > *m_pendingActionTurnIndex;
    if (!calendarAdvanced && !countdownAdvanced)
        return;

    ++state.confirmedTurnActions;
    m_unavailableRaceTurn.reset();

    if (*m_pendingActionKind == ActionIntentKind::Race)
        m_consecutiveRaceTurns = std::min(2, m_consecutiveRaceTurns + 1);
    else
        m_consecutiveRaceTurns = 0;
    m_pendingActionKind.reset();
    m_pendingActionTurnIndex.reset();
    m_pendingActionTurnsToGoal = ObservedValue<int>::Unknown();
    state.turnIndex = currentTurnEvidence.IsReliable()
        ? currentTurnEvidence.Value() : state.turnIndex;
}

bool UraScenarioModule::IsObjectiveCompleted(const CareerState& state) const
{
    return state.objectiveCompleted.IsReliable() && state.objectiveCompleted.Value();
}

std::optional<ActionIntent> UraScenarioModule::GetTrainingOverride(
    const CareerState& state,
    int restThreshold,
    bool smartStrategy) const
{
    if (m_inheritancePending)
    {
        ActionIntent wait;
        wait.reason = "Wait for the pending inheritance GO event before selecting another turn action.";
        return wait;
    }
    if (m_unavailableRaceTurn && *m_unavailableRaceTurn == state.turnIndex)
        return std::nullopt;
    if ((m_isFinale || Lower(state.phaseId) == "finale_underway") && m_hasPendingRace)
    {
        return ActionIntent::MakeRace(
            "finale", {}, "A URA Finale race is due; continue the final series.",
            m_goal.text.IsReliable() ? m_goal.text.confidence : 1.0,
            m_goal.text.IsReliable() ? m_goal.text.source : ObservationSource::Derived);
    }

    std::string deferredReason;
    if (m_hasPendingRace && !ShouldDeferRace())
    {
        std::string target;
        std::string reason;
        double confidence = 1.0;
        ObservationSource source = ObservationSource::Derived;
        switch (m_goal.kind)
        {
        case CareerGoalKind::Fans:
            target = "fans";
            reason = "The observed goal still needs "
                + (m_goal.fansToGoal.IsReliable()
                    ? std::to_string(m_goal.fansToGoal.Value()) : std::string("unknown"))
                + " fans; choose a race before training.";
            if (m_goal.fansToGoal.IsReliable())
            {
                confidence = m_goal.fansToGoal.confidence;
                source = m_goal.fansToGoal.source;
            }
            break;
        case CareerGoalKind::GradeRaceCount:
            target = "grade_race_count";
            reason = "A qualifying first-card race is available on the observed calendar; "
                + (m_goal.gradeRaceCountLeft.IsReliable()
                    ? std::to_string(m_goal.gradeRaceCountLeft.Value()) : std::string("unknown"))
                + " result(s) remain.";
            if (m_goal.gradeRaceCountLeft.IsReliable())
            {
                confidence = m_goal.gradeRaceCountLeft.confidence;
                source = m_goal.gradeRaceCountLeft.source;
            }
            break;
        case CareerGoalKind::Race:
            target = "goal";
            reason = "The visible race objective has reached its deadline.";
            if (m_goal.turnsToGoal.IsReliable())
            {
                confidence = m_goal.turnsToGoal.confidence;
                source = m_goal.turnsToGoal.source;
            }
            break;
        default:
            target = "recommended";
            reason = "A race is pending for the current URA objective.";
            break;
        }
        if (WouldBeThirdRace())
            reason = "The current objective requires another race despite two consecutive races. " + reason;
        return ActionIntent::MakeRace(
            std::move(target), {}, std::move(reason), confidence, source);
    }

    if (m_hasPendingRace && ShouldDeferRace())
        deferredReason = "Two consecutive races are confirmed; defer this optional race for a non-race turn. ";

    // Smart mode preserves its safety rule: when energy is already below the
    // configured threshold, it rests before considering recreation.
    if (smartStrategy && state.energy.IsReliable() && state.energy.Value() < restThreshold)
        return std::nullopt;

    if (!state.mood.IsReliable() || IsSummerCamp(state))
        return std::nullopt;

    const auto mood = Lower(state.mood.Value());
    const bool needsRecovery = mood == "normal" || mood == "bad" || mood == "awful";
    if (!needsRecovery)
        return std::nullopt;

    return ActionIntent::MakeRecreation(
        deferredReason + "Observed mood is Normal or lower; choose Recreation.",
        state.mood.confidence,
        state.mood.source);
}

bool UraScenarioModule::HasRaceGradeScheduleData() const noexcept
{
    return !m_firstCardGradeRanks.empty();
}

bool UraScenarioModule::HasQualifyingFirstCard(int turnIndex, const std::string& grade) const
{
    const auto targetRank = GradeRank(grade);
    const auto it = m_firstCardGradeRanks.find(turnIndex);
    return targetRank > 0 && it != m_firstCardGradeRanks.end() && it->second <= targetRank;
}

std::optional<int> UraScenarioModule::CountLaterQualifyingFirstCards(
    int firstTurnIndex,
    int lastTurnIndex,
    const std::string& grade) const
{
    const auto targetRank = GradeRank(grade);
    if (targetRank == 0 || lastTurnIndex < firstTurnIndex)
        return 0;
    int count = 0;
    for (auto it = m_firstCardGradeRanks.lower_bound(firstTurnIndex);
         it != m_firstCardGradeRanks.end() && it->first <= lastTurnIndex; ++it)
    {
        if (it->second <= targetRank)
            ++count;
    }
    return count;
}

bool UraScenarioModule::MustRaceNow() const
{
    if (m_isFinale || m_goal.kind == CareerGoalKind::Race)
        return true;
    if (m_goal.kind == CareerGoalKind::Fans)
        return m_goal.turnsToGoal.IsReliable() && m_goal.turnsToGoal.Value() <= 1;
    if (m_goal.kind != CareerGoalKind::GradeRaceCount)
        return false;
    if (m_goal.turnsToGoal.IsReliable() && m_goal.turnsToGoal.Value() <= 1)
        return true;
    if (!HasRaceGradeScheduleData()
        || !m_goal.turnsToGoal.IsReliable() || !m_currentTurnIndexObserved
        || !m_goal.targetRaceGrade.IsReliable()
        || !m_goal.gradeRaceCountLeft.IsReliable())
        return false;

    const auto laterRaceTurns = CountLaterQualifyingFirstCards(
        m_currentTurnIndex + 1,
        m_currentTurnIndex + m_goal.turnsToGoal.Value() - 1,
        m_goal.targetRaceGrade.Value());
    return laterRaceTurns.has_value()
        && *laterRaceTurns < m_goal.gradeRaceCountLeft.Value();
}

bool UraScenarioModule::ShouldDeferRace() const
{
    return m_hasPendingRace && WouldBeThirdRace() && !MustRaceNow();
}

bool UraScenarioModule::WouldBeThirdRace() const
{
    return m_consecutiveRaceTurns >= 2
        || (m_pendingActionKind == ActionIntentKind::Race
            && (m_goalCompletionProbeArmed
                || (m_pendingActionTurnIndex.has_value()
                    && m_currentTurnIndexObserved
                    && m_currentTurnIndex > *m_pendingActionTurnIndex)));
}

void UraScenarioModule::Register()
{
    ScenarioRegistry::Instance().RegisterFactory("ura", [] {
        return std::make_shared<UraScenarioModule>();
    });
}

} // namespace UmaAssistant::CareerDomain
