#pragma once

#include "HachimiPipelineModel.hpp"
#include "ITaskExecutionContext.hpp"

namespace UmaAssistant::TaskEngine
{

struct TransitionResult
{
    bool confirmed{false};
    std::string confirmedTemplate;
    int tapAttemptsMade{0};
    bool verifiedByHsv{false};
    std::string failureReason;
};

class TransitionValidator
{
public:
    static bool PreCheckClickState(
        const HachimiPipelineTask& task,
        int anchorX,
        int anchorY,
        ITaskExecutionContext& context);

    static TransitionResult ValidateTransition(
        const HachimiPipelineTask& task,
        const TaskMatchResult& originalMatch,
        ITaskExecutionContext& context,
        bool initialTapPerformed = true);

private:
    static bool ProbeHsvState(
        const HachimiClickVerification& config,
        int anchorX,
        int anchorY,
        ITaskExecutionContext& context);
};

} // namespace UmaAssistant::TaskEngine
