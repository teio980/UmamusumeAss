#pragma once

#include "UmaAssistantCore/CareerDomain/IScenarioModule.hpp"
#include "UmaAssistantCore/CareerDomain/CareerGoalTextParser.hpp"
#include <map>
#include <optional>
#include <string>
#include <unordered_map>
#include <vector>

namespace UmaAssistant::CareerDomain
{

class UraScenarioModule : public IScenarioModule
{
public:
    UraScenarioModule();

    void Initialize(const ScenarioInitialization& initialization) override;
    void OnRunStart(CareerState& state, bool continueExistingCareer = false) override;

    [[nodiscard]] std::string GetScenarioId() const override { return "ura"; }
    [[nodiscard]] std::string GetDisplayName() const override { return "URA Finals"; }

    void OnTurnStart(CareerState& state) override;
    void OnObserve(CareerState& state, const CareerObservation& observation) override;
    void OnTurnActionSubmitted(
        CareerState& state,
        const ActionIntent& intent,
        const CareerObservation& source) override;
    [[nodiscard]] std::optional<RaceStreakWarningResolution>
    GetRaceStreakWarningResolution(const CareerState& state) const override;
    [[nodiscard]] std::optional<TrainingRaceDistance>
    GetCurrentTrainingRaceDistance(const CareerState& state) const override;
    [[nodiscard]] std::optional<CareerRaceResultExpectation>
    GetCurrentRaceResultExpectation(const CareerState& state) const override;
    CareerRaceResultApplication OnRaceResultConfirmed(
        CareerState& state,
        const CareerRaceResultObservation& result) override;
    [[nodiscard]] bool IsObjectiveCompleted(const CareerState& state) const override;
    [[nodiscard]] bool IsGoalCompletionProbeArmed(const CareerState& state) const override;
    void ArmGoalCompletionProbe(CareerState& state) override;
    void DisarmGoalCompletionProbe(CareerState& state) override;
    void OnRaceReplayCompleted(CareerState& state) override;
    void OnRaceStrategyConfigured(CareerState& state) override;
    [[nodiscard]] bool ShouldDeferRace(const CareerState& state) const override;
    [[nodiscard]] bool WouldBeThirdRace() const override;
    [[nodiscard]] bool MustRaceNow() const override;
    [[nodiscard]] std::optional<ActionIntent> GetTrainingOverride(
        const CareerState& state,
        int restThreshold,
        bool smartStrategy) const override;

    static void Register();

private:
    struct GoalEvidence
    {
        CareerGoalKind kind{CareerGoalKind::Unknown};
        ObservedValue<int> turnsToGoal{ObservedValue<int>::Unknown()};
        ObservedValue<int> fansToGoal{ObservedValue<int>::Unknown()};
        ObservedValue<int> gradeRaceCountLeft{ObservedValue<int>::Unknown()};
        ObservedValue<std::string> targetRaceGrade{ObservedValue<std::string>::Unknown()};
        ObservedValue<std::string> text{ObservedValue<std::string>::Unknown()};
    };

    [[nodiscard]] bool HasRaceGradeScheduleData() const noexcept;
    [[nodiscard]] bool HasQualifyingFirstCard(int turnIndex, const std::string& grade) const;
    [[nodiscard]] std::optional<int> CountLaterQualifyingFirstCards(
        int firstTurnIndex, int lastTurnIndex, const std::string& grade) const;
    [[nodiscard]] bool ShouldDeferRace() const;
    [[nodiscard]] const ScenarioObjectiveDefinition* FindObjective(const std::string& id) const noexcept;
    [[nodiscard]] const ScenarioRaceDefinition* FindRace(const std::string& id) const noexcept;
    [[nodiscard]] std::string ResolveCurrentRaceId(const CareerState& state) const;
    [[nodiscard]] bool IsObjectiveSatisfied(
        const ScenarioObjectiveDefinition& objective,
        int placement,
        const CareerState& state) const;
    void AdvanceObjectiveAfterRace(CareerState& state, const ScenarioObjectiveDefinition& objective);
    void AdvanceNonRaceObjectives(CareerState& state);
    [[nodiscard]] CareerRaceResultApplication RetryOrFailRace(
        CareerState& state, const ScenarioRaceDefinition& race, int placement);
    void RefreshMainTurnEvidence(CareerState& state, const CareerObservation& observation);
    void ConfirmTurnAdvance(CareerState& state, const CareerObservation& observation);

    GoalEvidence m_goal;
    std::map<int, int> m_firstCardGradeRanks;
    std::vector<CareerTrainingRaceDistanceEntry> m_trainingRaceDistances;
    std::optional<TrainingRaceDistance> m_scenarioTrainingRaceDistanceFallback;
    std::unordered_map<std::string, ScenarioObjectiveDefinition> m_scenarioObjectives;
    std::unordered_map<std::string, ScenarioRaceDefinition> m_scenarioRaces;
    std::vector<std::string> m_finalSeriesStages;
    int m_finaleStageIndex{0};
    bool m_currentTurnIndexObserved{false};
    int m_currentTurnIndex{0};
    bool m_hasPendingRace{false};
    std::optional<int> m_unavailableRaceTurn;
    bool m_inheritancePending{false};
    bool m_isFinale{false};
    int m_consecutiveRaceTurns{0};
    std::optional<ActionIntentKind> m_pendingActionKind;
    std::optional<int> m_pendingActionTurnIndex;
    ObservedValue<int> m_pendingActionTurnsToGoal{ObservedValue<int>::Unknown()};
    std::optional<int> m_gradeRaceStartedTurnIndex;
    bool m_goalCompletionProbeArmed{false};
    CareerGoalKind m_goalKindAtRaceStart{CareerGoalKind::Unknown};
    ObservedValue<int> m_gradeRaceCountAtRaceStart{ObservedValue<int>::Unknown()};
};

} // namespace UmaAssistant::CareerDomain
