#pragma once

#include "ObservedValue.hpp"
#include "ActionIntent.hpp"
#include "CareerObservation.hpp"
#include <algorithm>
#include <cctype>
#include <optional>
#include <string>
#include <unordered_map>
#include <vector>

namespace UmaAssistant::CareerDomain
{

struct CareerState
{
    std::string scenarioId;
    std::string phaseId;
    int turnIndex{0};
    ObservedValue<int> turnIndexEvidence{ObservedValue<int>::Unknown()};
    std::optional<int> lastStartedTurnIndex;
    std::optional<std::string> turnPositionText;

    ObservedValue<int> energy{ObservedValue<int>::Unknown()};
    ObservedValue<std::string> mood{ObservedValue<std::string>::Unknown()};
    bool infirmaryAvailable{false};
    ObservedValue<bool> infirmaryEvidence{ObservedValue<bool>::Unknown()};

    ObservedValue<int> turnsToGoal{ObservedValue<int>::Unknown()};
    ObservedValue<int> fansToGoal{ObservedValue<int>::Unknown()};
    ObservedValue<int> fans{ObservedValue<int>::Unknown()};
    ObservedValue<int> lastRacePlacement{ObservedValue<int>::Unknown()};
    ObservedValue<bool> objectiveCompleted{ObservedValue<bool>::Unknown()};
    std::string goalText;

    // Generic scenario-owned objective/race progress. The scenario module
    // interprets IDs and applies package rules; shared strategies do not.
    std::string currentObjectiveId;
    std::string currentRaceId;
    int raceRetryCount{0};
    bool raceRetryDeclined{false};
    std::unordered_map<std::string, int> racePlacements;
    std::vector<std::string> completedObjectiveIds;

    std::string lastScreenId{"unknown"};
    ActionIntent lastIntent;
    bool careerStarted{false};
    // Calendar indices can stay unchanged during Pre-Debut and the Finale.
    // Scenario-confirmed action progress is monotonic across those turns.
    int confirmedTurnActions{0};
    bool raceReplayFlowCompleted{false};
    bool raceStrategyConfigured{false};
    int consecutiveRaceTurns{0};
    std::string observedGoalKind;

    void UpdateFromObservation(const CareerObservation& obs)
    {
        auto normalizedScreenId = obs.screenId;
        std::transform(normalizedScreenId.begin(), normalizedScreenId.end(), normalizedScreenId.begin(),
            [](unsigned char character) { return static_cast<char>(std::tolower(character)); });
        const bool isMainTurn = obs.screenKind == CareerScreenKind::MainTurn
            || normalizedScreenId == "career_main";
        lastScreenId = obs.screenId;
        if (obs.turnPositionText.has_value())
        {
            turnPositionText = obs.turnPositionText;
        }
        else if (isMainTurn)
        {
            turnPositionText.reset();
        }
        if (obs.turnIndexEvidence.IsReliable())
        {
            turnIndexEvidence = obs.turnIndexEvidence;
            turnIndex = obs.turnIndexEvidence.Value();
        }
        else if (isMainTurn)
        {
            // Keep the last numeric turn as a convenience, but do not treat
            // it as evidence for this newly observed Main screen.
            turnIndexEvidence = ObservedValue<int>::Unknown();
        }
        if (obs.energyPercent.IsReliable())
        {
            energy = obs.energyPercent;
        }
        else if (isMainTurn)
        {
            energy = ObservedValue<int>::Unknown();
        }
        if (obs.moodEvidence.IsReliable())
        {
            mood = obs.moodEvidence;
        }
        else if (obs.moodText.has_value())
        {
            mood = ObservedValue<std::string>::Make(*obs.moodText, obs.score, ObservationSource::Ocr);
        }
        else if (isMainTurn)
        {
            mood = ObservedValue<std::string>::Unknown();
        }
        if (obs.infirmaryEvidence.IsReliable())
        {
            infirmaryEvidence = obs.infirmaryEvidence;
            infirmaryAvailable = obs.infirmaryEvidence.Value();
        }
        else if (obs.screenKind == CareerScreenKind::MainTurn)
        {
            infirmaryAvailable = obs.infirmaryAvailable;
            infirmaryEvidence = ObservedValue<bool>::Make(
                obs.infirmaryAvailable, obs.score, ObservationSource::TemplateMatch);
        }
        else if (isMainTurn)
        {
            infirmaryAvailable = false;
            infirmaryEvidence = ObservedValue<bool>::Unknown();
        }
        if (obs.turnsToGoalEvidence.IsReliable())
        {
            turnsToGoal = obs.turnsToGoalEvidence;
        }
        else if (obs.turnsToGoal.has_value())
        {
            turnsToGoal = ObservedValue<int>::Make(*obs.turnsToGoal, obs.score, ObservationSource::Ocr);
        }
        else if (isMainTurn)
        {
            turnsToGoal = ObservedValue<int>::Unknown();
        }
        if (obs.fansToGoalEvidence.IsReliable())
        {
            fansToGoal = obs.fansToGoalEvidence;
        }
        else if (obs.fansToGoal.has_value())
        {
            fansToGoal = ObservedValue<int>::Make(*obs.fansToGoal, obs.score, ObservationSource::Ocr);
        }
        else if (isMainTurn)
        {
            fansToGoal = ObservedValue<int>::Unknown();
        }
        if (obs.goalTextEvidence.IsReliable())
        {
            goalText = obs.goalTextEvidence.Value();
        }
        else if (obs.goalText.has_value())
        {
            goalText = *obs.goalText;
        }
        else if (isMainTurn)
        {
            goalText.clear();
        }
        if (obs.objectiveCompleted.IsReliable())
        {
            objectiveCompleted = obs.objectiveCompleted;
        }
    }
};

} // namespace UmaAssistant::CareerDomain
