#include "TaskGraphRunner.hpp"
#include "HachimiPipelineLoader.hpp"
#include "Common/PathEncoding.hpp"

#include <algorithm>
#include <chrono>
#include <cctype>
#include <filesystem>
#include <limits>
#include <unordered_set>

namespace UmaAssistant::TaskEngine
{

namespace
{

using Clock = std::chrono::steady_clock;

std::string NormalizeName(const std::string& value)
{
    std::string result;
    result.reserve(value.size());
    for (unsigned char c : value)
    {
        if (c == '_' || c == '-' || std::isspace(c))
            continue;
        result.push_back(static_cast<char>(std::tolower(c)));
    }
    return result;
}

std::optional<std::string> FirstExisting(
    const HachimiPipelineDefinition& definition,
    const std::vector<std::string>& candidates)
{
    for (const auto& candidate : candidates)
    {
        if (!candidate.empty() && definition.TryGetTask(candidate))
            return candidate;
    }
    return std::nullopt;
}

void AppendUnique(std::vector<std::string>& target, const std::string& value)
{
    if (value.empty())
        return;
    if (std::none_of(target.begin(), target.end(), [&](const auto& current) {
            return CaseInsensitiveEqual{}(current, value);
        }))
        target.push_back(value);
}

HachimiPipelineTask ApplyOverrides(
    const std::string& taskName,
    const HachimiPipelineTask& source,
    const TaskGraphRunOptions& options)
{
    HachimiPipelineTask task = source;
    if (const auto it = options.roiOverrides.find(taskName); it != options.roiOverrides.end())
        task.roi = it->second;
    if (const auto it = options.searchRoiOverrides.find(taskName); it != options.searchRoiOverrides.end())
        task.searchRois = it->second;
    if (const auto it = options.scaleCandidatesOverrides.find(taskName); it != options.scaleCandidatesOverrides.end())
        task.scaleCandidates = it->second;
    if (const auto it = options.templateOverrides.find(taskName);
        it != options.templateOverrides.end() && !it->second.empty())
        task.templatePath = it->second;
    if (const auto it = options.inputTextOverrides.find(taskName); it != options.inputTextOverrides.end())
        task.inputText = it->second;
    if (const auto it = options.targetTextOverrides.find(taskName);
        it != options.targetTextOverrides.end() && !it->second.empty())
        task.targetText = it->second;
    return task;
}

bool HasExceededMaxTimes(
    const std::string& taskName,
    const HachimiPipelineTask& task,
    const std::unordered_map<std::string, int, CaseInsensitiveHash, CaseInsensitiveEqual>& counts,
    const TaskGraphRunOptions& options,
    std::string& outCountKey)
{
    outCountKey = task.countKey && !task.countKey->empty() ? *task.countKey : taskName;
    const auto countIt = counts.find(outCountKey);
    const int count = countIt == counts.end() ? 0 : countIt->second;

    int recoveredOffset = 0;
    if (const auto it = options.recoveredCountOffsets.find(outCountKey);
        it != options.recoveredCountOffsets.end())
        recoveredOffset = (std::max)(0, it->second);

    auto limit = options.maxTimesOverrides.find(taskName);
    if (limit == options.maxTimesOverrides.end())
        limit = options.maxTimesOverrides.find(outCountKey);
    if (limit != options.maxTimesOverrides.end())
    {
        // An explicit zero is meaningful: it bypasses the task immediately.
        const int adjustedLimit = (std::max)(0, limit->second - recoveredOffset);
        return count >= adjustedLimit;
    }
    return task.maxTimes > 0 && count >= task.maxTimes;
}

std::filesystem::path ResolveNestedPath(
    const HachimiPipelineDefinition& definition,
    const std::string& pipeline)
{
    auto path = Paths::FromUtf8(pipeline);
    if (path.is_relative() && !definition.baseDirectory.empty())
        path = Paths::FromUtf8(definition.baseDirectory) / path;
    return path.lexically_normal();
}

std::string NormalizePathKey(std::string value)
{
    std::replace(value.begin(), value.end(), '\\', '/');
    while (value.size() > 1 && value.back() == '/')
        value.pop_back();
    return value;
}

bool IsNestedPipelineSkipped(
    const std::filesystem::path& resolvedPath,
    const TaskGraphRunOptions& options)
{
    std::error_code error;
    auto absolutePath = std::filesystem::absolute(resolvedPath, error);
    if (error)
        absolutePath = resolvedPath;
    const auto fullPathKey = NormalizePathKey(Paths::ToUtf8(absolutePath.lexically_normal()));
    const auto baseNameKey = NormalizePathKey(Paths::ToUtf8(resolvedPath.filename()));

    for (const auto& configured : options.skipNestedPipelines)
    {
        const auto key = NormalizePathKey(configured);
        if (CaseInsensitiveEqual{}(key, fullPathKey) || CaseInsensitiveEqual{}(key, baseNameKey))
            return true;
        if (key.find('/') != std::string::npos)
        {
            auto configuredPath = Paths::FromUtf8(key);
            if (configuredPath.is_relative() && !resolvedPath.parent_path().empty())
                configuredPath = resolvedPath.parent_path() / configuredPath;
            if (CaseInsensitiveEqual{}(
                    NormalizePathKey(Paths::ToUtf8(configuredPath.lexically_normal())), fullPathKey))
                return true;
        }
    }
    return false;
}

std::pair<int, int> ResolveActionTapPoint(
    const HachimiPipelineTask& task,
    const TaskMatchResult& match)
{
    int x = match.CenterX();
    int y = match.CenterY();
    const auto action = NormalizeName(task.action);
    if (action == "clickrect" && task.specificRect && task.specificRect->size() >= 4)
    {
        x = (*task.specificRect)[0] + (*task.specificRect)[2] / 2;
        y = (*task.specificRect)[1] + (*task.specificRect)[3] / 2;
    }
    else if (action == "clickspecificrect" && task.specificRect && task.specificRect->size() >= 4)
    {
        x = (*task.specificRect)[0] + (*task.specificRect)[2] / 2;
        y = (*task.specificRect)[1] + (*task.specificRect)[3] / 2;
    }
    else if (action == "clicktext")
    {
        int left = match.x;
        int top = match.y;
        int right = match.x + (std::max)(0, match.width);
        int bottom = match.y + (std::max)(0, match.height);
        if (task.rowExpansion && task.rowExpansion->size() >= 4)
        {
            const auto& expansion = *task.rowExpansion;
            left = (std::max)(0, left - (std::max)(0, expansion[0]));
            top = (std::max)(0, top - (std::max)(0, expansion[1]));
            right = (std::max)(left, right + (std::max)(0, expansion[2]));
            bottom = (std::max)(top, bottom + (std::max)(0, expansion[3]));
        }
        x = left + (right - left) / 2;
        y = top + (bottom - top) / 2;
        if (task.clickAnchor && !task.clickAnchor->empty())
        {
            x = (*task.clickAnchor)[0];
            if (task.clickAnchor->size() >= 2)
                y = (*task.clickAnchor)[1];
        }
        else if (task.clickOffset && task.clickOffset->size() >= 2)
        {
            x += (*task.clickOffset)[0];
            y += (*task.clickOffset)[1];
        }
    }
    else if (task.clickOffset && task.clickOffset->size() >= 2)
    {
        x += (*task.clickOffset)[0];
        y += (*task.clickOffset)[1];
    }
    else if (action == "clickself" && task.fallbackClickOffset && task.fallbackClickOffset->size() >= 2)
    {
        x += (*task.fallbackClickOffset)[0];
        y += (*task.fallbackClickOffset)[1];
    }
    return {x, y};
}

std::optional<TaskRoi> ResolveRoi(const std::optional<std::vector<int>>& value)
{
    if (!value || value->size() < 4)
        return std::nullopt;
    return TaskRoi{(*value)[0], (*value)[1], (*value)[2], (*value)[3]};
}

struct TemplateBaseScope
{
    ITaskExecutionContext& context;
    std::string previous;
    std::pair<int, int> previousCoordinates;
    explicit TemplateBaseScope(ITaskExecutionContext& executionContext, const std::string& current)
        : context(executionContext), previous(executionContext.GetTemplateBaseDirectory()),
          previousCoordinates(executionContext.GetCoordinateReference())
    {
        context.SetTemplateBaseDirectory(current);
    }
    ~TemplateBaseScope()
    {
        context.SetTemplateBaseDirectory(previous);
        context.SetCoordinateReference(previousCoordinates.first, previousCoordinates.second);
    }
};

} // namespace

TaskGraphRunner::TaskGraphRunner(ActionRegistry actionRegistry)
    : m_actionRegistry(std::move(actionRegistry))
{
}

TaskGraphRunResult TaskGraphRunner::Run(
    const HachimiPipelineDefinition& definition,
    const std::string& entryTask,
    ITaskExecutionContext& context,
    const TaskGraphRunOptions& options)
{
    CountMap counts;
    auto result = RunImpl(definition, entryTask, context, options, 0, 0, counts);
    result.taskExecutionCounts = std::move(counts);
    return result;
}

TaskGraphRunResult TaskGraphRunner::RunImpl(
    const HachimiPipelineDefinition& definition,
    const std::string& entryTask,
    ITaskExecutionContext& context,
    const TaskGraphRunOptions& options,
    int depth,
    int pipelineDepth,
    CountMap& taskCounts)
{
    TaskGraphRunResult result;
    TemplateBaseScope templateBaseScope(context, definition.baseDirectory);
    // Bound all recursive graph calls to protect the native worker stack.
    constexpr int maximumCallDepth = 16;
    if (depth >= maximumCallDepth)
    {
        result.status = TaskGraphStatus::GuardLimitExceeded;
        result.errorMessage = "Pipeline exceeded maximum recursive call depth (16)";
        return result;
    }
    // The managed runner separately limits nested RunPipeline calls to four.
    if (pipelineDepth > 4)
    {
        result.status = TaskGraphStatus::GuardLimitExceeded;
        result.errorMessage = "Pipeline exceeded maximum nested pipeline depth (4)";
        return result;
    }
    if (definition.tasks.empty())
    {
        result.status = TaskGraphStatus::Failed;
        result.errorMessage = "Pipeline definition contains no tasks";
        return result;
    }

    // Transient matches are scoped to one pipeline invocation; the native
    // context retains the source frame so a retry cannot be rebound to a new
    // screenshot's coordinate geometry.
    std::unordered_map<std::string, TaskMatchResult, CaseInsensitiveHash, CaseInsensitiveEqual> transientMatchCache;
    std::string currentTaskName = entryTask;
    if (currentTaskName.empty())
    {
        if (definition.start && !definition.start->empty())
            currentTaskName = *definition.start;
        else if (!definition.taskOrder.empty())
            currentTaskName = definition.taskOrder.front();
        else
            currentTaskName = definition.tasks.begin()->first;
    }

    context.EmitEvent(TaskExecutionEvent{
        TaskEventType::PipelineStarted, currentTaskName, definition.name, 0, true, 0.0, 0, 0,
        "Pipeline started at entry: " + currentTaskName,
        std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now().time_since_epoch()).count()});

    const int guardLimit = (std::max)(100, static_cast<int>(definition.tasks.size() * 40));
    for (int step = 0; step < guardLimit; ++step)
    {
        if (context.IsCancelled())
        {
            result.status = TaskGraphStatus::Cancelled;
            result.finalTask = currentTaskName;
            return result;
        }
        result.totalStepsExecuted++;
        result.finalTask = currentTaskName;

        const auto* sourceTask = definition.TryGetTask(currentTaskName);
        if (sourceTask == nullptr)
        {
            result.status = TaskGraphStatus::Failed;
            result.errorMessage = "Task not found in definition: " + currentTaskName;
            context.Log("ERROR", result.errorMessage);
            return result;
        }
        const HachimiPipelineTask task = ApplyOverrides(currentTaskName, *sourceTask, options);
        context.SetCoordinateReference(task.referenceWidth, task.referenceHeight);

        context.EmitEvent(TaskExecutionEvent{
            TaskEventType::TaskEntering, currentTaskName, definition.name, step, true, 0.0, 0, 0,
            "Entering task: " + currentTaskName,
            std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now().time_since_epoch()).count()});

        std::string countKey;
        if (HasExceededMaxTimes(currentTaskName, task, taskCounts, options, countKey))
        {
            context.Log("INFO", "Task '" + currentTaskName + "' reached maxTimes; following exceededNext.");
            if (const auto next = FirstExisting(definition, task.exceededNext))
            {
                currentTaskName = *next;
                continue;
            }
            if (task.required)
            {
                result.status = TaskGraphStatus::ExceededMaxTimes;
                result.errorMessage = "Required task exceeded maxTimes without exceededNext: " + currentTaskName;
                return result;
            }
            result.status = TaskGraphStatus::Completed;
            return result;
        }

        // The managed engine counts a task entry before attempting subgraphs,
        // recognition, or input, so failures also consume one maxTimes slot.
        ++taskCounts[countKey];

        bool optionalTerminal = false;
        auto routeFailure = [&](const std::string& message, TaskGraphStatus failureStatus) -> bool {
            optionalTerminal = false;
            if (failureStatus == TaskGraphStatus::TimedOut && !task.required)
                context.Log("DEBUG", "Optional visual marker is absent: " + currentTaskName);
            else
            {
                context.Log("WARN", message);
                context.EmitEvent(TaskExecutionEvent{
                    TaskEventType::TaskFailed, currentTaskName, definition.name, step, false, 0.0, 0, 0,
                    message,
                    std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now().time_since_epoch()).count()});
            }
            if (const auto next = FirstExisting(definition, task.onErrorNext))
            {
                currentTaskName = *next;
                return true;
            }
            if (!task.required)
            {
                if (const auto next = FirstExisting(definition, task.next))
                    currentTaskName = *next;
                else
                {
                    result.status = TaskGraphStatus::Completed;
                    result.finalTask = currentTaskName;
                    optionalTerminal = true;
                }
                return true;
            }
            result.status = failureStatus;
            result.errorMessage = message;
            return false;
        };

        bool subRouted = false;
        for (const auto& subName : task.sub)
        {
            context.Log("INFO", "Executing sub-task: " + subName);
            auto subResult = RunImpl(definition, subName, context, options, depth + 1, pipelineDepth, taskCounts);
            result.totalStepsExecuted += subResult.totalStepsExecuted;
            result.completedUnits += subResult.completedUnits;
            context.SetCoordinateReference(task.referenceWidth, task.referenceHeight);
            if (subResult.status != TaskGraphStatus::Success && subResult.status != TaskGraphStatus::Completed)
            {
                const std::string message = "Sub-task '" + subName + "' failed: " + subResult.errorMessage;
                if (routeFailure(message, subResult.status))
                {
                    subRouted = true;
                    break;
                }
                return result;
            }
        }
        if (subRouted)
        {
            if (optionalTerminal)
                return result;
            continue;
        }

        if (NormalizeName(task.action) == "runpipeline" &&
            (!task.pipeline || task.pipeline->empty()))
        {
            if (!routeFailure("RunPipeline task has no pipeline path.", TaskGraphStatus::Failed))
                return result;
            if (optionalTerminal)
                return result;
            continue;
        }
        if (NormalizeName(task.action) == "runpipeline" && pipelineDepth >= 4)
        {
            if (!routeFailure("RunPipeline task exceeded the nested pipeline depth limit.",
                              TaskGraphStatus::GuardLimitExceeded))
                return result;
            if (optionalTerminal)
                return result;
            continue;
        }

        // A template collection is a declarative reference, not a filesystem
        // path. This engine has no collection resolver yet; do not silently
        // turn it into an unbounded recognition timeout.
        if (task.templateCollection && !task.templateCollection->empty() &&
            (!task.templatePath || task.templatePath->empty()))
        {
            if (!routeFailure("Task '" + currentTaskName +
                              "' requires a template collection, but no collection resolver is registered.",
                              TaskGraphStatus::Failed))
                return result;
            if (optionalTerminal)
                return result;
            continue;
        }

        RecognitionEvaluation eval;
        const bool hasNoVisualSelector =
            (!task.templatePath || task.templatePath->empty()) &&
            (!task.templateCollection || task.templateCollection->empty()) &&
            task.alternativeTemplateTasks.empty() &&
            (!task.targetText || task.targetText->empty()) &&
            task.monitorTasks.empty() && !task.successTask && task.successTasks.empty() &&
            !task.clickVerification && !task.clickUntilGone && !task.repeatTapUntilTransition &&
            task.transitionTemplates.empty();
        const bool actionNeedsNoFrame = NormalizeName(task.action) == "donothing" &&
            hasNoVisualSelector && NormalizeName(task.algorithm) != "parallelmonitor" &&
            NormalizeName(task.algorithm) != "raceresultmonitor";
        bool recognized = actionNeedsNoFrame;
        if (actionNeedsNoFrame)
        {
            eval.matched = true;
            eval.matchedTaskName = currentTaskName;
            eval.methodUsed = "NoRecognitionRequired";
        }
        const bool isParallelMonitor = NormalizeName(task.algorithm) == "parallelmonitor" ||
            NormalizeName(task.algorithm) == "raceresultmonitor";
        if (isParallelMonitor && !actionNeedsNoFrame)
        {
            std::vector<std::string> successNames;
            if (task.successTask)
                AppendUnique(successNames, *task.successTask);
            for (const auto& name : task.successTasks)
                AppendUnique(successNames, name);
            std::vector<std::string> monitorNames;
            for (const auto& name : task.monitorTasks)
                AppendUnique(monitorNames, name);

            if (successNames.empty() || monitorNames.empty())
            {
                if (!routeFailure("Parallel monitor '" + currentTaskName +
                                  "' requires monitorTasks and successTask(s).", TaskGraphStatus::Failed))
                    return result;
                if (optionalTerminal)
                    return result;
                continue;
            }
            bool invalidSuccessCandidate = false;
            for (const auto& name : successNames)
            {
                const auto* candidate = definition.TryGetTask(name);
                if (!candidate || !candidate->templatePath || candidate->templatePath->empty())
                {
                    invalidSuccessCandidate = true;
                    break;
                }
            }
            if (invalidSuccessCandidate)
            {
                if (!routeFailure("Parallel monitor has an undefined or template-less success candidate.",
                                  TaskGraphStatus::Failed))
                    return result;
                if (optionalTerminal)
                    return result;
                continue;
            }

            std::unordered_map<std::string, Clock::time_point, CaseInsensitiveHash, CaseInsensitiveEqual> stableSince;
            const auto started = Clock::now();
            const int timeoutMs = (std::max)(0, task.timeoutMilliseconds);
            const int pollMs = task.pollIntervalMilliseconds > 0
                ? task.pollIntervalMilliseconds
                : (definition.timing.pollIntervalMilliseconds > 0 ? definition.timing.pollIntervalMilliseconds : 250);
            bool transitionFound = false;
            bool monitorFailed = false;
            bool firstFrame = true;

            while (!context.IsCancelled())
            {
                const bool captured = context.CaptureScreen();
                bool hasPendingSuccess = false;
                if (captured)
                {
                    std::unordered_set<std::string, CaseInsensitiveHash, CaseInsensitiveEqual> seenSuccess;
                    for (const auto& name : successNames)
                    {
                        auto candidateEval = RecognitionPipeline::EvaluateSameFrameMonitors(
                            definition, std::vector<std::string>{name}, context);
                        if (!candidateEval)
                        {
                            stableSince.erase(name);
                            continue;
                        }
                        seenSuccess.insert(name);
                        const auto* candidate = definition.TryGetTask(name);
                        const int stableMs = candidate ? (std::max)(0, candidate->monitorStableMilliseconds) : 0;
                        if (stableMs == 0)
                        {
                            eval = *candidateEval;
                            transitionFound = true;
                            break;
                        }
                        const auto [it, inserted] = stableSince.emplace(name, Clock::now());
                        if (inserted || Clock::now() - it->second < std::chrono::milliseconds(stableMs))
                        {
                            hasPendingSuccess = true;
                            continue;
                        }
                        eval = *candidateEval;
                        transitionFound = true;
                        break;
                    }
                    for (auto it = stableSince.begin(); it != stableSince.end();)
                    {
                        if (seenSuccess.find(it->first) == seenSuccess.end())
                            it = stableSince.erase(it);
                        else
                            ++it;
                    }

                    if (!transitionFound && !hasPendingSuccess)
                    {
                        for (const auto& name : monitorNames)
                        {
                            auto actionEval = RecognitionPipeline::EvaluateSameFrameMonitors(
                                definition, std::vector<std::string>{name}, context);
                            if (!actionEval)
                                continue;
                            const auto* monitorSource = definition.TryGetTask(name);
                            if (!monitorSource)
                                continue;
                            auto monitorTask = ApplyOverrides(name, *monitorSource, options);
                            context.SetCoordinateReference(monitorTask.referenceWidth, monitorTask.referenceHeight);
                            if (monitorTask.preDelay > 0)
                                context.Sleep(monitorTask.preDelay);
                            ActionResult actionResult = ActionResult::Failed;
                            if (NormalizeName(monitorTask.action) == "runpipeline" && pipelineDepth >= 4)
                            {
                                context.Log("ERROR", "Parallel monitor RunPipeline exceeded the nested pipeline depth limit.");
                            }
                            else if (NormalizeName(monitorTask.action) == "runpipeline" && monitorTask.pipeline)
                            {
                                const auto pipelinePath = ResolveNestedPath(definition, *monitorTask.pipeline);
                                if (IsNestedPipelineSkipped(pipelinePath, options))
                                {
                                    context.Log("INFO", "Skipping completed nested pipeline: " +
                                        Paths::ToUtf8(pipelinePath));
                                    actionResult = ActionResult::Success;
                                }
                                else
                                {
                                    auto nested = HachimiPipelineLoader::LoadFromFile(Paths::ToUtf8(pipelinePath));
                                    if (nested)
                                    {
                                        CountMap nestedCounts;
                                        const auto nestedResult = RunImpl(*nested, monitorTask.entry.value_or(""),
                                            context, options, depth + 1, pipelineDepth + 1, nestedCounts);
                                        result.totalStepsExecuted += nestedResult.totalStepsExecuted;
                                        actionResult = nestedResult.status == TaskGraphStatus::Success ||
                                            nestedResult.status == TaskGraphStatus::Completed
                                            ? ActionResult::Success : ActionResult::Failed;
                                    }
                                }
                            }
                            else
                            {
                                actionResult = m_actionRegistry.ExecuteAction(
                                    monitorTask, actionEval->matchResult, context);
                            }
                            context.SetCoordinateReference(task.referenceWidth, task.referenceHeight);
                            if (actionResult == ActionResult::Failed && monitorTask.required)
                            {
                                monitorFailed = true;
                                if (!routeFailure("Parallel monitor action failed: " + name, TaskGraphStatus::Failed))
                                    return result;
                                if (optionalTerminal)
                                    return result;
                            }
                            else
                            {
                                if (monitorTask.waitMilliseconds > 0)
                                    context.Sleep(monitorTask.waitMilliseconds);
                                if (monitorTask.postDelay > 0)
                                    context.Sleep(monitorTask.postDelay);
                            }
                            // Only one action can consume a captured frame.
                            break;
                        }
                    }
                }

                if (transitionFound)
                {
                    context.EmitEvent(TaskExecutionEvent{
                        TaskEventType::MonitorTriggered, eval.matchedTaskName, definition.name, step, true,
                        eval.matchResult.score, eval.matchResult.CenterX(), eval.matchResult.CenterY(),
                        "Parallel monitor success condition detected: " + eval.matchedTaskName,
                        std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now().time_since_epoch()).count()});
                    currentTaskName = eval.matchedTaskName;
                    break;
                }
                if (monitorFailed)
                    break;
                if (Clock::now() - started >= std::chrono::milliseconds(timeoutMs))
                    break;
                if (!firstFrame || timeoutMs > 0)
                    context.Sleep(pollMs);
                firstFrame = false;
            }
            if (context.IsCancelled())
            {
                result.status = TaskGraphStatus::Cancelled;
                return result;
            }
            if (transitionFound)
                continue;
            if (monitorFailed)
                continue;
            if (!routeFailure("Parallel monitor timed out: " + currentTaskName, TaskGraphStatus::TimedOut))
                return result;
            if (optionalTerminal)
                return result;
            continue;
        }

        const int timeoutMs = task.timeoutMilliseconds;
        const int pollInterval = task.pollIntervalMilliseconds > 0
            ? task.pollIntervalMilliseconds
            : (definition.timing.pollIntervalMilliseconds > 0 ? definition.timing.pollIntervalMilliseconds : 250);
        int retryAttempt = 0;
        for (;;)
        {
            if (recognized)
                break;
            const auto waitTaskStarted = Clock::now();
            bool firstCapture = true;
            while (!context.IsCancelled())
            {
                const bool captured = context.CaptureScreen();
                if (captured)
                {
                    // Configured monitor order is preserved. If a task declares
                    // stop candidates, test those first so one frame cannot
                    // trigger a stale click before the stop is observed.
                    std::vector<std::string> stopNames;
                    if (task.successTask)
                        AppendUnique(stopNames, *task.successTask);
                    for (const auto& name : task.successTasks)
                        AppendUnique(stopNames, name);
                    if (!stopNames.empty())
                    {
                        auto stopEval = RecognitionPipeline::EvaluateSameFrameMonitors(
                            definition, stopNames, context);
                        if (stopEval)
                        {
                            eval = *stopEval;
                            recognized = true;
                            eval.methodUsed = "SuccessMonitor";
                            break;
                        }
                    }
                    if (!task.monitorTasks.empty())
                    {
                        auto monitorEval = RecognitionPipeline::EvaluateSameFrameMonitors(
                            definition, task.monitorTasks, context);
                        if (monitorEval)
                        {
                            eval = *monitorEval;
                            recognized = true;
                            eval.methodUsed = "ParallelMonitorCandidate";
                            break;
                        }
                    }

                    context.SetCoordinateReference(task.referenceWidth, task.referenceHeight);
                    eval = RecognitionPipeline::EvaluateTask(definition, currentTaskName, task, context);
                    if (eval.matched)
                    {
                        recognized = true;
                        break;
                    }
                    if (task.reuseLastMatchOnRetry)
                    {
                        const auto cached = transientMatchCache.find(currentTaskName);
                        if (cached != transientMatchCache.end())
                        {
                            eval.matched = true;
                            eval.matchedTaskName = currentTaskName;
                            eval.matchResult = cached->second;
                            eval.methodUsed = "CachedTransient";
                            transientMatchCache.erase(cached);
                            recognized = true;
                            context.Log("INFO", "Reusing retained match for: " + currentTaskName);
                            break;
                        }
                    }
                }

                const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
                    Clock::now() - waitTaskStarted).count();
                if (timeoutMs >= 0 && (elapsed >= timeoutMs || (timeoutMs == 0 && !firstCapture)))
                    break;
                if (timeoutMs == 0)
                    break;
                context.Sleep(pollInterval);
                firstCapture = false;
            }
            if (recognized || context.IsCancelled() || retryAttempt >= task.retryTimes)
                break;
            ++retryAttempt;
            context.EmitEvent(TaskExecutionEvent{
                TaskEventType::TaskRetrying, currentTaskName, definition.name, step, true, 0.0, 0, 0,
                "Recognition retry " + std::to_string(retryAttempt) + "/" + std::to_string(task.retryTimes),
                std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now().time_since_epoch()).count()});
            context.Sleep(task.retryDelayMilliseconds > 0 ? task.retryDelayMilliseconds : 750);
        }

        if (context.IsCancelled())
        {
            result.status = TaskGraphStatus::Cancelled;
            return result;
        }
        if (!recognized)
        {
            const std::string message = "Recognition timed out on task: " + currentTaskName;
            if (!routeFailure(message, TaskGraphStatus::TimedOut))
                return result;
            if (optionalTerminal)
                return result;
            continue;
        }
        if (eval.matchedTaskName != currentTaskName)
        {
            currentTaskName = eval.matchedTaskName;
            continue;
        }

        context.SetCoordinateReference(task.referenceWidth, task.referenceHeight);
        context.EmitEvent(TaskExecutionEvent{
            TaskEventType::RecognitionEvaluated, currentTaskName, definition.name, step, true,
            eval.matchResult.score, eval.matchResult.CenterX(), eval.matchResult.CenterY(),
            "Matched via " + eval.methodUsed,
            std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now().time_since_epoch()).count()});

        bool alreadySatisfied = false;
        if (task.clickVerification && task.clickVerification->preCheck)
        {
            int ax = eval.matchResult.CenterX();
            int ay = eval.matchResult.CenterY();
            if (task.clickAnchor && task.clickAnchor->size() >= 2)
            {
                ax = (*task.clickAnchor)[0];
                ay = (*task.clickAnchor)[1];
            }
            alreadySatisfied = TransitionValidator::PreCheckClickState(task, ax, ay, context);
        }

        bool actionSucceeded = true;
        bool initialTapPerformed = false;
        bool retainedForRetry = false;
        if (!alreadySatisfied)
        {
            if (task.preDelay > 0)
                context.Sleep(task.preDelay);
            if (context.IsCancelled())
            {
                result.status = TaskGraphStatus::Cancelled;
                return result;
            }
            context.EmitEvent(TaskExecutionEvent{
                TaskEventType::ActionExecuting, currentTaskName, definition.name, step, true,
                eval.matchResult.score, eval.matchResult.CenterX(), eval.matchResult.CenterY(),
                "Executing action: " + task.action,
                std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now().time_since_epoch()).count()});

            const auto normalizedAction = NormalizeName(task.action);
            const bool isRunPipeline = normalizedAction == "runpipeline";
            if (normalizedAction == "clickselfuntiltransition" && task.transitionTemplates.empty())
            {
                actionSucceeded = false;
                context.Log("ERROR", "ClickSelfUntilTransition requires transitionTemplates.");
            }
            if ((task.clickUntilGone || task.repeatTapUntilTransition) && !eval.matchResult.found)
            {
                actionSucceeded = false;
                context.Log("ERROR", "Transition-aware click requires a template match.");
            }
            if (task.clickUntilGone && !task.templatePath)
            {
                actionSucceeded = false;
                context.Log("ERROR", "clickUntilGone requires a current template.");
            }
            if (isRunPipeline && (!task.pipeline || task.pipeline->empty()))
            {
                actionSucceeded = false;
                context.Log("ERROR", "RunPipeline task has no pipeline path.");
            }

            if (actionSucceeded && eval.matchResult.found &&
                (task.reuseLastMatchOnRetry || (task.clickVerification && task.clickVerification->maxRetries > 0)))
            {
                // A cached retry may already own a bounded lease from the
                // original recognition. Preserve its remaining input budget
                // and frozen target instead of renewing it after recapture.
                retainedForRetry = context.IsMatchRetained(eval.matchResult);
                if (!retainedForRetry)
                {
                    const auto [tapX, tapY] = ResolveActionTapPoint(task, eval.matchResult);
                    int attempts = task.reuseLastMatchOnRetry ? 2 : 1;
                    if (task.clickVerification)
                        attempts = (std::max)(attempts, task.clickVerification->maxRetries + 1);
                    retainedForRetry = context.RetainMatchForRetry(
                        eval.matchResult, (std::clamp)(attempts, 1, 100), 30000, tapX, tapY);
                }
            }

            if (actionSucceeded && isRunPipeline)
            {
                const auto pipelinePath = ResolveNestedPath(definition, *task.pipeline);
                if (IsNestedPipelineSkipped(pipelinePath, options))
                {
                    context.Log("INFO", "Skipping completed nested pipeline: " + Paths::ToUtf8(pipelinePath));
                }
                else
                {
                    auto nestedDefinition = HachimiPipelineLoader::LoadFromFile(Paths::ToUtf8(pipelinePath));
                    if (!nestedDefinition)
                    {
                        actionSucceeded = false;
                        context.Log("ERROR", "Failed to load nested pipeline: " + Paths::ToUtf8(pipelinePath));
                    }
                    else
                    {
                        CountMap nestedCounts;
                        const auto nestedResult = RunImpl(*nestedDefinition, task.entry.value_or(""),
                            context, options, depth + 1, pipelineDepth + 1, nestedCounts);
                        result.totalStepsExecuted += nestedResult.totalStepsExecuted;
                        result.completedUnits += nestedResult.completedUnits;
                        context.SetCoordinateReference(task.referenceWidth, task.referenceHeight);
                        if (nestedResult.status != TaskGraphStatus::Success && nestedResult.status != TaskGraphStatus::Completed)
                        {
                            actionSucceeded = false;
                            context.Log("ERROR", "Nested pipeline failed: " + nestedResult.errorMessage);
                        }
                    }
                }
            }
            if (actionSucceeded && !task.clickUntilGone && !task.repeatTapUntilTransition)
            {
                const auto actionResult = m_actionRegistry.ExecuteAction(task, eval.matchResult, context);
                actionSucceeded = actionResult != ActionResult::Failed;
                initialTapPerformed = actionResult == ActionResult::Success &&
                    (NormalizeName(task.action) == "clickself" || NormalizeName(task.action) == "clickrect" ||
                     NormalizeName(task.action) == "clickspecificrect" || NormalizeName(task.action) == "clickselfuntiltransition");
                context.EmitEvent(TaskExecutionEvent{
                    TaskEventType::ActionExecuted, currentTaskName, definition.name, step, actionSucceeded,
                    eval.matchResult.score, eval.matchResult.CenterX(), eval.matchResult.CenterY(),
                    "Action executed result: " + std::to_string(static_cast<int>(actionResult)),
                    std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now().time_since_epoch()).count()});
            }
            else if (actionSucceeded)
            {
                // TransitionValidator owns the initial and repeated taps for
                // these actions so maxClickAttempts includes the first tap.
                initialTapPerformed = false;
            }

            if (!actionSucceeded)
            {
                if (retainedForRetry)
                    context.ReleaseRetainedMatch();
                if (!routeFailure("Action execution failed on task: " + currentTaskName, TaskGraphStatus::Failed))
                    return result;
                if (optionalTerminal)
                    return result;
                continue;
            }

            if (task.reuseLastMatchOnRetry && eval.matchResult.found && !retainedForRetry)
            {
                const auto [tapX, tapY] = ResolveActionTapPoint(task, eval.matchResult);
                const bool retained = context.RetainMatchForRetry(eval.matchResult, 2, 30000, tapX, tapY);
                if (retained)
                    transientMatchCache[currentTaskName] = eval.matchResult;
            }
            else if (task.reuseLastMatchOnRetry && retainedForRetry)
            {
                transientMatchCache[currentTaskName] = eval.matchResult;
            }

            const auto transition = TransitionValidator::ValidateTransition(
                task, eval.matchResult, context, initialTapPerformed);
            if (retainedForRetry && !task.reuseLastMatchOnRetry)
                context.ReleaseRetainedMatch();
            if (context.IsCancelled())
            {
                result.status = TaskGraphStatus::Cancelled;
                return result;
            }
            if (!transition.confirmed)
            {
                if (!routeFailure("Transition confirmation failed: " + transition.failureReason,
                                  TaskGraphStatus::Failed))
                    return result;
                if (optionalTerminal)
                    return result;
                continue;
            }
            context.EmitEvent(TaskExecutionEvent{
                TaskEventType::TransitionVerified, currentTaskName, definition.name, step, true,
                0.0, 0, 0, "Transition confirmed: " + transition.confirmedTemplate,
                std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now().time_since_epoch()).count()});
        }

        if (task.countAs && !task.countAs->empty())
            ++result.completedUnits;
        if (task.postDelay > 0)
            context.Sleep(task.postDelay);
        TaskExecutionEvent completedEvent{
            TaskEventType::TaskCompleted, currentTaskName, definition.name, step, true, 0.0, 0, 0,
            "Task completed successfully",
            std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now().time_since_epoch()).count()};
        completedEvent.completedUnits = result.completedUnits;
        context.EmitEvent(completedEvent);

        if (task.success)
        {
            result.status = TaskGraphStatus::Success;
            return result;
        }
        if (const auto next = FirstExisting(definition, task.next))
        {
            currentTaskName = *next;
            continue;
        }
        result.status = TaskGraphStatus::Completed;
        return result;
    }

    result.status = TaskGraphStatus::GuardLimitExceeded;
    result.errorMessage = "Pipeline exceeded maximum execution guard limit (" + std::to_string(guardLimit) + " steps)";
    return result;
}

} // namespace UmaAssistant::TaskEngine
