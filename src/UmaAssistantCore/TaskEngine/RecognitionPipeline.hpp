#pragma once

#include "HachimiPipelineModel.hpp"
#include "ITaskExecutionContext.hpp"
#include <optional>
#include <vector>
#include <string>

namespace UmaAssistant::TaskEngine
{

struct RecognitionEvaluation
{
    bool matched{false};
    std::string matchedTaskName;
    TaskMatchResult matchResult;
    std::string methodUsed; // "Template", "AlternativeTemplate", "Ocr", "Direct"
    int scrollCount{0};
};

class RecognitionPipeline
{
public:
    static RecognitionEvaluation EvaluateTask(
        const HachimiPipelineDefinition& definition,
        const std::string& taskName,
        const HachimiPipelineTask& task,
        ITaskExecutionContext& context);

    static std::optional<RecognitionEvaluation> EvaluateSameFrameMonitors(
        const HachimiPipelineDefinition& definition,
        const std::vector<std::string>& monitorTasks,
        ITaskExecutionContext& context);

private:
    static std::optional<TaskMatchResult> DetectSingleFrame(
        const HachimiPipelineDefinition& definition,
        const HachimiPipelineTask& task,
        ITaskExecutionContext& context,
        std::string& outMethod);

    static bool CheckOcrText(
        const std::string& targetText,
        const std::vector<TaskOcrDetection>& detections,
        double fuzzyThreshold,
        const std::optional<std::string>& matchMode,
        bool requireAllTokens,
        bool unique,
        int groupRowHeight,
        int rowGap,
        TaskMatchResult& outMatch);
};

} // namespace UmaAssistant::TaskEngine
