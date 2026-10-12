#pragma once

#include "UmaAssistantCore/CareerDomain/ActionIntent.hpp"
#include "UmaAssistantCore/CareerDomain/CareerObservation.hpp"
#include "UmaAssistantCore/CareerDomain/IScenarioModule.hpp"
#include "UmaAssistantCore/TaskEngine/ITaskExecutionContext.hpp"
#include <memory>
#include <optional>

namespace UmaAssistant::Career
{

class CareerVisualResources;

enum class ActionExecutionStatus
{
    Success,
    Failed,
    InterruptedByEvent,
    InterruptedByDateChange,
    Cancelled
};

class ICareerActionExecutor
{
public:
    virtual ~ICareerActionExecutor() = default;

    virtual ActionExecutionStatus Execute(
        const CareerDomain::ActionIntent& intent,
        TaskEngine::ITaskExecutionContext& context) = 0;

    // The source observation supplies the only valid screen/action binding.
    // Existing executors remain source-compatible through this fallback.
    virtual ActionExecutionStatus ExecuteForObservation(
        const CareerDomain::ActionIntent& intent,
        const CareerDomain::CareerObservation& observation,
        TaskEngine::ITaskExecutionContext& context)
    {
        (void)observation;
        return Execute(intent, context);
    }

    [[nodiscard]] virtual std::shared_ptr<CareerVisualResources> GetCareerVisualResources() const
    {
        return {};
    }

    // Returns a result only when the authored race-result runtime-frame
    // template matched the current capture. This keeps expected placement data
    // scenario-owned while the runtime owns image/resource verification.
    virtual std::optional<CareerDomain::CareerRaceResultObservation> MatchRaceResult(
        const CareerDomain::CareerRaceResultExpectation& expectation,
        TaskEngine::ITaskExecutionContext& context)
    {
        (void)expectation;
        (void)context;
        return std::nullopt;
    }

    // The task graph's terminal outcome distinguishes an accepted alarm-clock
    // retry from its resource-authored cancel fallback.
    virtual std::optional<bool> TakeRaceRetryDeclined() { return std::nullopt; }

    // Hand over action graph outcome (e.g. race.replay.completed, race.retry.declined, etc.)
    virtual std::optional<std::string> TakeLastActionOutcome() { return std::nullopt; }

    virtual bool ConfirmCompletion(
        const CareerDomain::ActionIntent& intent,
        TaskEngine::ITaskExecutionContext& context) = 0;

    virtual bool ConfirmCompletionFromObservation(
        const CareerDomain::ActionIntent& intent,
        const CareerDomain::CareerObservation& observation,
        TaskEngine::ITaskExecutionContext& context)
    {
        (void)observation;
        return ConfirmCompletion(intent, context);
    }

    virtual std::optional<CareerDomain::CareerObservation> TakeConfirmedObservation()
    {
        return std::nullopt;
    }

    virtual void ResetAfterRecovery() noexcept {}
    virtual void SetGoalObservationPolicy(bool armed, bool replayCompleted) noexcept
    {
        (void)armed;
        (void)replayCompleted;
    }
    virtual void ClearLearnedSkills(const std::string& deviceKey) { (void)deviceKey; }
    virtual void SetRaceRunnerResultExpected(bool expected) noexcept { (void)expected; }
};

} // namespace UmaAssistant::Career
