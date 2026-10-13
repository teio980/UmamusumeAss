#pragma once

#include <string>
#include <vector>

namespace UmaAssistant::Career
{

enum class CareerTransitionPhase
{
    Unknown, Main, TrainingSelection, TrainingOutcome, TurnOutcome,
    RaceEntry, RaceExecution, RaceOutcome, Goal, Settlement, ReturnHome,
    Entry, NormalStartup, Independent
};

// The continuation survives successive events; lastIntent is only the most
// recent input and cannot identify the turn action that triggered an event.
struct CareerTransitionContext
{
    CareerTransitionPhase phase{CareerTransitionPhase::Unknown};
    CareerTransitionPhase continuation{CareerTransitionPhase::TurnOutcome};
    std::string sourceScreenId;
    std::string semanticActionId;
    std::string actionOutcome;
    std::vector<std::string> candidates;
    int timeoutMs{45000};
    bool retryUnchangedRaceEntry{false};
    bool resumedEvent{false};
};

class CareerTransitionPolicy
{
public:
    static CareerTransitionContext AfterAction(const CareerTransitionContext& previous,
        const std::string& source, const std::string& semantic, const std::string& outcome = {});
    static CareerTransitionContext FromObservation(const std::string& screenId,
        const CareerTransitionContext& previous = {});
    static bool IsEvent(const std::string& screenId);
};

} // namespace UmaAssistant::Career
