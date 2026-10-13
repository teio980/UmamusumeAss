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

struct CareerPageStateEvidence
{
    std::uint64_t signature{0};
    int frameWidth{0};
    int frameHeight{0};
    std::uint64_t sessionGeneration{0};
    std::uint64_t geometryGeneration{0};
};

struct CareerObservation
{
    std::string screenId{"unknown"};
    double score{0.0};
    CareerScreenKind screenKind{CareerScreenKind::Unknown};
    // Only identical complete frames permit a second race-entry input.
    bool consecutiveFramesIdentical{false};
    // Independent samples supporting this page, rather than template hits.
    int stableFrameCount{0};

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
    std::optional<std::string> eventActionId;
    // Stable, scenario-neutral evidence for event-state changes where a choice
    // panel reuses the same screen ID and title.
    std::optional<std::uint64_t> eventStateSignature;
    // An authored content marker, identical across two confirmed samples,
    // distinguishes successive pages sharing a screen ID (e.g. Rewards).
    std::optional<CareerPageStateEvidence> pageStateEvidence;
};

} // namespace UmaAssistant::CareerDomain
