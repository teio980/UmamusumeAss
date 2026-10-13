#pragma once

#include "UmaAssistantCore/CareerDomain/CareerObservation.hpp"
#include "UmaAssistantCore/TaskEngine/ITaskExecutionContext.hpp"
#include "CareerTransitionPolicy.hpp"
#include <algorithm>
#include <vector>

namespace UmaAssistant::Career
{

class ICareerObserver
{
public:
    virtual ~ICareerObserver() = default;

    [[nodiscard]] virtual CareerDomain::CareerObservation Observe(
        TaskEngine::ITaskExecutionContext& context) = 0;

    [[nodiscard]] virtual CareerDomain::CareerObservation ObserveCareer(
        TaskEngine::ITaskExecutionContext& context) { return Observe(context); }

    // Entry owns navigation stages; recognition only evaluates the screens
    // eligible for that stage. Runtime observers retain their unrestricted API.
    [[nodiscard]] virtual CareerDomain::CareerObservation ObserveScreens(
        TaskEngine::ITaskExecutionContext& context, const std::vector<std::string>& screenIds,
        bool allowResume = false)
    {
        (void)allowResume;
        auto observation = Observe(context);
        if (!screenIds.empty() && std::find(screenIds.begin(), screenIds.end(), observation.screenId) == screenIds.end()
            && observation.screenId != "capture_failed" && observation.screenId != "resource_error"
            && observation.screenId != "capture_cancelled")
            return {};
        return observation;
    }

    virtual void ResetForRecovery() noexcept {}
    [[nodiscard]] virtual CareerDomain::CareerObservation ObserveTransition(
        TaskEngine::ITaskExecutionContext& context, const CareerTransitionContext& transition,
        std::shared_ptr<const TaskEngine::TaskFrameSnapshot> seed = {})
    {
        (void)seed;
        return ObserveScreens(context, transition.candidates);
    }

    [[nodiscard]] virtual CareerDomain::CareerObservation ObserveStableScreens(
        TaskEngine::ITaskExecutionContext& context, const std::vector<std::string>& screens,
        bool allowResume = false)
    {
        CareerDomain::CareerObservation prior;
        for (int attempt = 0; attempt <= 30 && !context.IsCancelled(); ++attempt)
        {
            auto current = ObserveScreens(context, screens, allowResume);
            if (current.screenId == "resource_error" || current.screenId == "capture_cancelled") return current;
            const bool known = current.screenId != "unknown" && current.screenId != "capture_failed";
            if (known && (current.stableFrameCount >= 2 || (attempt > 0 && prior.screenId == current.screenId)))
                return current;
            prior = std::move(current);
            if (attempt < 30) context.Sleep(250);
        }
        return {};
    }
    virtual void SetGoalCompletionProbeArmed(bool armed) noexcept { (void)armed; }
    virtual void SetRaceReplayFlowCompleted(bool completed) noexcept { (void)completed; }
    virtual void SetRaceRunnerResultExpected(bool expected) noexcept { (void)expected; }
    virtual void ClearMainTextCache() noexcept {}
};

} // namespace UmaAssistant::Career
