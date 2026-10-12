#pragma once

#include "UmaAssistantCore/CareerDomain/CareerObservation.hpp"
#include "UmaAssistantCore/TaskEngine/ITaskExecutionContext.hpp"
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
};

} // namespace UmaAssistant::Career
