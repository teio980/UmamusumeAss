#pragma once

#include "UmaAssistantCore/CareerDomain/CareerObservation.hpp"
#include "UmaAssistantCore/TaskEngine/ITaskExecutionContext.hpp"

namespace UmaAssistant::Career
{

class ICareerObserver
{
public:
    virtual ~ICareerObserver() = default;

    [[nodiscard]] virtual CareerDomain::CareerObservation Observe(
        TaskEngine::ITaskExecutionContext& context) = 0;

    virtual void ResetForRecovery() noexcept {}
};

} // namespace UmaAssistant::Career
