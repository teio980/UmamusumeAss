#pragma once

#include "UmaAssistantCore/CareerDomain/CareerObservation.hpp"
#include "UmaAssistantCore/TaskEngine/ITaskExecutionContext.hpp"

#include <chrono>
#include <memory>

namespace UmaAssistant::Career
{

// Reuse visual evidence only while the independent confirmation's final frame
// is still current. The action must rematch its own button before input.
inline std::shared_ptr<const TaskEngine::TaskFrameSnapshot> CurrentConfirmedCareerFrame(
    const CareerDomain::CareerObservation& observation,
    TaskEngine::ITaskExecutionContext& context)
{
    if (observation.stableFrameCount < 2 || !observation.visualFrameEvidence)
        return {};
    const auto& proof = *observation.visualFrameEvidence;
    const auto frame = context.GetCurrentFrame();
    const auto now = std::chrono::steady_clock::now();
    if (!frame || proof.frameId == 0 || frame->frameId != proof.frameId
        || frame->sessionGeneration != proof.sessionGeneration
        || frame->geometryGeneration != proof.geometryGeneration
        || frame->width != proof.width || frame->height != proof.height
        || frame->orientation != proof.orientation || now < proof.capturedAt
        || now - proof.capturedAt >= std::chrono::seconds(1))
        return {};
    return frame;
}

} // namespace UmaAssistant::Career
