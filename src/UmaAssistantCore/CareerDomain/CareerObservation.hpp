#pragma once

#include "ObservedValue.hpp"
#include <string>
#include <optional>
#include <cstdint>

namespace UmaAssistant::CareerDomain
{

enum class CareerScreenKind
{
    Unknown,
    MainTurn,
    TrainingSelect,
    TrainingResult,
    EventChoice,
    RaceEntry,
    RaceExecution,
    RaceResult,
    SkillLearning,
    Settlement,
    Infirmary,
    Rest,
    Recreation,
    ClawMachine,
    GoalUpdate,
    ConfirmationDialog
};

struct CareerObservation
{
    std::string screenId{"unknown"};
    double score{0.0};
    CareerScreenKind screenKind{CareerScreenKind::Unknown};

    // Legacy scalar fields stay available to the runtime. Evidence fields
    // preserve provenance and allow an absent observation to remain unknown.
    ObservedValue<int> energyPercent{ObservedValue<int>::Unknown()};
    ObservedValue<int> turnIndexEvidence{ObservedValue<int>::Unknown()};
    ObservedValue<int> turnsToGoalEvidence{ObservedValue<int>::Unknown()};
    ObservedValue<int> fansToGoalEvidence{ObservedValue<int>::Unknown()};
    ObservedValue<std::string> moodEvidence{ObservedValue<std::string>::Unknown()};
    ObservedValue<std::string> goalTextEvidence{ObservedValue<std::string>::Unknown()};
    ObservedValue<bool> infirmaryEvidence{ObservedValue<bool>::Unknown()};
    ObservedValue<bool> objectiveCompleted{ObservedValue<bool>::Unknown()};
    std::optional<std::string> turnPositionText;
    std::optional<int> turnsToGoal;
    std::optional<std::string> goalText;
    std::optional<int> fansToGoal;
    bool infirmaryAvailable{false};
    std::optional<std::string> moodText;
    std::optional<std::string> eventId;
    std::optional<std::string> eventTitle;
    // Stable, scenario-neutral evidence for event-state changes where a choice
    // panel reuses the same screen ID and title.
    std::optional<std::uint64_t> eventStateSignature;
};

} // namespace UmaAssistant::CareerDomain
