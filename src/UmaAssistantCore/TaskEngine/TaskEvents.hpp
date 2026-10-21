#pragma once

#include <string>
#include <cstdint>

namespace UmaAssistant::TaskEngine
{

enum class TaskEventType
{
    PipelineStarted,
    PipelineCompleted,
    PipelineFailed,
    TaskEntering,
    RecognitionEvaluated,
    ActionExecuting,
    ActionExecuted,
    TransitionVerified,
    MonitorTriggered,
    TaskCompleted,
    TaskFailed,
    TaskRetrying
};

struct TaskExecutionEvent
{
    TaskEventType type;
    std::string taskName;
    std::string pipelineName;
    int stepIndex{0};
    bool succeeded{false};
    double score{0.0};
    int targetX{0};
    int targetY{0};
    std::string message;
    int64_t timestampMs{0};
    std::string taskId;
    int completedUnits{0};
    int totalUnits{0};
};

} // namespace UmaAssistant::TaskEngine
