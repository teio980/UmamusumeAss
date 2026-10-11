#pragma once
#include "CareerEntryFlow.hpp"

namespace UmaAssistant::GameTasks
{
class NativeNormalCareerStartup final
{
public:
    [[nodiscard]] static CareerEntryResult Run(
        const Career::CareerRuntimeConfig& config,
        Career::CareerVisualResources& resources,
        Career::ICareerObserver& observer,
        TaskEngine::ITaskExecutionContext& context);
};
}
