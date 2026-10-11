#pragma once

#include "HachimiPipelineModel.hpp"
#include "ITaskExecutionContext.hpp"
#include "ActionRegistry.hpp"
#include "RecognitionPipeline.hpp"
#include "TransitionValidator.hpp"
#include "TaskEvents.hpp"
#include <string>
#include <unordered_map>
#include <vector>
#include <memory>
#include <optional>
#include <unordered_set>

namespace UmaAssistant::TaskEngine
{

enum class TaskGraphStatus
{
    Success,
    Completed,
    Failed,
    Cancelled,
    ExceededMaxTimes,
    TimedOut,
    GuardLimitExceeded
};

struct TaskGraphRunResult
{
    TaskGraphStatus status{TaskGraphStatus::Completed};
    std::string finalTask;
    std::unordered_map<std::string, int, CaseInsensitiveHash, CaseInsensitiveEqual> taskExecutionCounts;
    std::string errorMessage;
    int totalStepsExecuted{0};
    int completedUnits{0};
};

struct TaskGraphRunOptions
{
    std::unordered_map<std::string, int, CaseInsensitiveHash, CaseInsensitiveEqual> maxTimesOverrides;
    std::unordered_map<std::string, std::vector<int>, CaseInsensitiveHash, CaseInsensitiveEqual> roiOverrides;
    std::unordered_map<std::string, std::vector<std::vector<int>>, CaseInsensitiveHash, CaseInsensitiveEqual> searchRoiOverrides;
    std::unordered_map<std::string, std::vector<double>, CaseInsensitiveHash, CaseInsensitiveEqual> scaleCandidatesOverrides;
    std::unordered_map<std::string, std::string, CaseInsensitiveHash, CaseInsensitiveEqual> templateOverrides;
    std::unordered_map<std::string, std::string, CaseInsensitiveHash, CaseInsensitiveEqual> inputTextOverrides;
    std::unordered_map<std::string, std::string, CaseInsensitiveHash, CaseInsensitiveEqual> targetTextOverrides;
    std::unordered_map<std::string, int, CaseInsensitiveHash, CaseInsensitiveEqual> recoveredCountOffsets;
    std::unordered_set<std::string, CaseInsensitiveHash, CaseInsensitiveEqual> skipNestedPipelines;
};

class TaskGraphRunner
{
public:
    explicit TaskGraphRunner(ActionRegistry actionRegistry = ActionRegistry());

    TaskGraphRunResult Run(
        const HachimiPipelineDefinition& definition,
        const std::string& entryTask,
        ITaskExecutionContext& context,
        const TaskGraphRunOptions& options = {});

    ActionRegistry& GetActionRegistry() noexcept { return m_actionRegistry; }
    const ActionRegistry& GetActionRegistry() const noexcept { return m_actionRegistry; }

private:
    using CountMap = std::unordered_map<std::string, int, CaseInsensitiveHash, CaseInsensitiveEqual>;
    ActionRegistry m_actionRegistry;
    TaskGraphRunResult RunImpl(
        const HachimiPipelineDefinition& definition,
        const std::string& entryTask,
        ITaskExecutionContext& context,
        const TaskGraphRunOptions& options,
        int depth,
        int pipelineDepth,
        CountMap& taskCounts);
};

} // namespace UmaAssistant::TaskEngine
