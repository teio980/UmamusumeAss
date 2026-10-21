#include "StartGameMonitor.hpp"

#include "Common/PathEncoding.hpp"
#include "TaskEngine/HachimiPipelineLoader.hpp"

#include <algorithm>
#include <chrono>
#include <filesystem>
#include <optional>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

namespace UmaAssistant::GameTasks
{

namespace
{

using namespace TaskEngine;

struct Candidate
{
    std::string name;
    HachimiPipelineTask const* task{};
    std::string templatePath;
};

using MatchMap = std::unordered_map<std::string, TaskMatchResult, CaseInsensitiveHash, CaseInsensitiveEqual>;

std::string ResolveTemplatePath(
    HachimiPipelineDefinition const& definition,
    HachimiPipelineTask const& task)
{
    if (!task.templatePath || task.templatePath->empty()) return {};
    auto path = Paths::FromUtf8(*task.templatePath);
    if (path.is_relative()) path = Paths::FromUtf8(definition.baseDirectory) / path;
    return Paths::ToUtf8(path.lexically_normal());
}

void Emit(
    ITaskExecutionContext& context,
    TaskEventType type,
    std::string const& taskName,
    std::string const& pipelineName,
    int stepIndex,
    bool succeeded,
    double score,
    int targetX,
    int targetY,
    std::string message)
{
    auto const timestamp = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::system_clock::now().time_since_epoch()).count();
    int const completedUnits = (type == TaskEventType::PipelineCompleted && succeeded) ? 1 : 0;
    context.EmitEvent(TaskExecutionEvent{
        type, taskName, pipelineName, stepIndex, succeeded, score, targetX, targetY,
        std::move(message), timestamp, {}, completedUnits, 1});
}

bool Capture(ITaskExecutionContext& context)
{
    return !context.IsCancelled() && context.CaptureScreen()
        && static_cast<bool>(context.GetCurrentFrame());
}

MatchMap FindMatches(
    ITaskExecutionContext& context,
    std::vector<Candidate> const& candidates)
{
    MatchMap matches;
    for (auto const& candidate : candidates)
    {
        std::optional<TaskRoi> roi;
        if (candidate.task->roi && candidate.task->roi->size() >= 4)
        {
            roi = TaskRoi{
                (*candidate.task->roi)[0], (*candidate.task->roi)[1],
                (*candidate.task->roi)[2], (*candidate.task->roi)[3]};
        }
        auto const match = context.MatchTemplateAdvanced(
            candidate.templatePath,
            candidate.task->templateThreshold,
            roi,
            candidate.task->algorithm,
            candidate.task->referenceWidth,
            candidate.task->referenceHeight,
            candidate.task->scaleCandidates);
        matches.emplace(candidate.name, match);
    }
    return matches;
}

TaskMatchResult const* Found(MatchMap const& matches, std::string const& name)
{
    auto const position = matches.find(name);
    return position != matches.end() && position->second.found ? &position->second : nullptr;
}

bool RunMonitorAction(
    ITaskExecutionContext& context,
    Candidate const& candidate,
    TaskMatchResult const& match)
{
    auto const& task = *candidate.task;
    context.SetCoordinateReference(task.referenceWidth, task.referenceHeight);
    if (task.preDelay > 0) context.Sleep(task.preDelay);
    if (context.IsCancelled()) return false;

    Emit(context, TaskEventType::ActionExecuting, candidate.name, "start-game",
        0, true, match.score, match.CenterX(), match.CenterY(), "Executing startup monitor action: " + task.action);

    bool succeeded = false;
    if (CaseInsensitiveEqual{}(task.action, "ClickSelf"))
    {
        succeeded = match.found && context.TapMatch(match.CenterX(), match.CenterY(), match);
    }
    else if (CaseInsensitiveEqual{}(task.action, "ClickRect")
        || CaseInsensitiveEqual{}(task.action, "ClickSpecificRect")
        || CaseInsensitiveEqual{}(task.action, "TapToStart"))
    {
        if (task.specificRect && task.specificRect->size() >= 2)
        {
            auto const& rect = *task.specificRect;
            auto x = rect[0];
            auto y = rect[1];
            if (rect.size() >= 4)
            {
                x += rect[2] / 2;
                y += rect[3] / 2;
            }
            succeeded = context.Tap(x, y);
        }
    }
    else if (CaseInsensitiveEqual{}(task.action, "DoNothing")
        || CaseInsensitiveEqual{}(task.action, "JustReturn"))
    {
        succeeded = true;
    }
    else if (CaseInsensitiveEqual{}(task.action, "Wait"))
    {
        auto const wait = task.waitMilliseconds > 0 ? task.waitMilliseconds : task.postDelay;
        if (wait > 0) context.Sleep(wait);
        succeeded = !context.IsCancelled();
    }
    else if (CaseInsensitiveEqual{}(task.action, "Back"))
    {
        succeeded = context.SendKey("4");
    }
    else if (CaseInsensitiveEqual{}(task.action, "Input"))
    {
        succeeded = task.inputText && context.InputText(*task.inputText);
    }
    else if (CaseInsensitiveEqual{}(task.action, "KeyEvent")
        || CaseInsensitiveEqual{}(task.action, "Key"))
    {
        succeeded = task.keyCode && context.SendKey(*task.keyCode);
    }
    else
    {
        context.Log("ERROR", "Unsupported startup monitor action: " + task.action);
    }

    Emit(context, TaskEventType::ActionExecuted, candidate.name, "start-game",
        0, succeeded, match.score, match.CenterX(), match.CenterY(),
        succeeded ? "Startup monitor action completed." : "Startup monitor action failed.");
    if (succeeded && task.postDelay > 0) context.Sleep(task.postDelay);
    if (succeeded)
        Emit(context, TaskEventType::TaskCompleted, candidate.name, "start-game",
            0, true, match.score, match.CenterX(), match.CenterY(), "Startup monitor action completed.");
    return succeeded && !context.IsCancelled();
}

} // namespace

StartGameMonitorResult StartGameMonitor::Run(
    ITaskExecutionContext& context,
    const std::filesystem::path& resourceRoot,
    const Clock& clock)
{
    auto const definitionPath = resourceRoot / "hachimi" / "pipelines" / "start_game.json";
    auto definition = HachimiPipelineLoader::LoadFromFile(Paths::ToUtf8(definitionPath));
    if (!definition)
        return {false, false, "The packaged start_game.json could not be loaded."};

    auto monitorName = definition->start.value_or(
        definition->taskOrder.empty() ? std::string{} : definition->taskOrder.front());
    auto const* monitorTask = definition->TryGetTask(monitorName);
    if (monitorTask == nullptr)
        return {false, false, "The start_game.json entry task is not defined."};
    if (!CaseInsensitiveEqual{}(monitorTask->action, "StartupMonitor")
        && monitorTask->monitorTasks.empty())
        return {false, false, "The start_game.json entry is not a startup monitor."};

    std::vector<std::string> candidateNames = monitorTask->monitorTasks;
    candidateNames.insert(candidateNames.end(), monitorTask->triggerChain.begin(), monitorTask->triggerChain.end());
    if (monitorTask->triggerTask && !monitorTask->triggerTask->empty())
        candidateNames.push_back(*monitorTask->triggerTask);
    std::vector<Candidate> candidates;
    std::unordered_set<std::string, CaseInsensitiveHash, CaseInsensitiveEqual> seen;
    for (auto const& name : candidateNames)
    {
        if (name.empty() || !seen.insert(name).second) continue;
        auto const* task = definition->TryGetTask(name);
        if (task == nullptr)
            return {false, false, "Startup monitor task '" + name + "' is not defined."};
        auto path = ResolveTemplatePath(*definition, *task);
        if (path.empty() || !std::filesystem::is_regular_file(path))
            return {false, false, "Template for startup monitor task '" + name + "' is missing."};
        candidates.push_back(Candidate{name, task, std::move(path)});
    }

    std::unordered_map<std::string, Candidate const*, CaseInsensitiveHash, CaseInsensitiveEqual> byName;
    for (auto const& candidate : candidates) byName.emplace(candidate.name, &candidate);
    std::vector<Candidate const*> monitorCandidates;
    for (auto const& name : monitorTask->monitorTasks)
    {
        auto const found = byName.find(name);
        if (found != byName.end()) monitorCandidates.push_back(found->second);
    }
    std::vector<Candidate const*> successCandidates;
    std::unordered_set<std::string, CaseInsensitiveHash, CaseInsensitiveEqual> successNames;
    for (auto const* candidate : monitorCandidates)
    {
        if (candidate->task->success)
        {
            successCandidates.push_back(candidate);
            successNames.insert(candidate->name);
        }
    }
    if (monitorCandidates.empty() || successCandidates.empty())
        return {false, false, "The startup monitor has no check tasks or Home success task."};

    Candidate const* triggerCandidate = nullptr;
    if (monitorTask->triggerTask)
    {
        auto const found = byName.find(*monitorTask->triggerTask);
        if (found != byName.end()) triggerCandidate = found->second;
    }
    std::vector<Candidate const*> triggerChain;
    for (auto const& name : monitorTask->triggerChain)
    {
        auto const found = byName.find(name);
        if (found != byName.end()) triggerChain.push_back(found->second);
    }

    auto const timeout = std::chrono::milliseconds(std::clamp(monitorTask->timeoutMilliseconds, 0, 600000));
    auto const poll = std::chrono::milliseconds(std::clamp(monitorTask->pollIntervalMilliseconds, 50, 10000));
    auto const now = clock ? clock : Clock{[] { return std::chrono::steady_clock::now(); }};
    auto const started = now();
    auto firstIteration = true;
    int chainIndex = -1;
    std::chrono::steady_clock::time_point chainStepStarted{};
    int step = 0;
    MatchMap lastMatches;

    Emit(context, TaskEventType::PipelineStarted, monitorName, definition->name,
        step, true, 0.0, 0, 0, "Startup monitor started.");
    while (!context.IsCancelled())
    {
        try
        {
            // Give the monitor one initial observation even for timeoutMs=0, then
            // enforce the deadline before every cycle so capture failures, missing
            // frame metadata and repeated trigger hits cannot spin forever.
            if (!firstIteration && now() - started >= timeout)
            {
                std::string summary = "Startup monitor timed out waiting for Home.";
                if (!lastMatches.empty()) summary += " Last observed " + std::to_string(lastMatches.size()) + " monitor candidate(s).";
                Emit(context, TaskEventType::PipelineFailed, monitorName, definition->name,
                    step, false, 0.0, 0, 0, summary);
                return {false, false, summary};
            }
            firstIteration = false;
            ++step;
            if (!Capture(context))
            {
                if (context.IsCancelled()) break;
                context.Sleep(static_cast<int>(poll.count()));
                continue;
            }
            auto const frame = context.GetCurrentFrame();
            if (!frame)
            {
                context.Sleep(static_cast<int>(poll.count()));
                continue;
            }

            std::vector<Candidate> activeCandidates;
            if (chainIndex >= 0)
            {
                auto const* current = triggerChain[static_cast<std::size_t>(chainIndex)];
                auto const stepTimeout = std::chrono::milliseconds(
                    std::clamp(current->task->timeoutMilliseconds, 0, 600000));
                // Startup logos can disappear between captures. Do not let a
                // missed chain step hide TapToStart until the global deadline.
                if (now() - chainStepStarted >= stepTimeout)
                {
                    context.Log("WARN", "Startup trigger chain timed out waiting for " + current->name
                        + "; resuming all startup monitor checks.");
                    chainIndex = -1;
                }
            }
            if (chainIndex >= 0)
            {
                for (auto const* candidate : successCandidates) activeCandidates.push_back(*candidate);
                for (auto const* candidate : triggerChain) activeCandidates.push_back(*candidate);
            }
            else
            {
                for (auto const* candidate : monitorCandidates) activeCandidates.push_back(*candidate);
            }
            lastMatches = FindMatches(context, activeCandidates);

            Candidate const* successCandidate = nullptr;
            TaskMatchResult const* successMatch = nullptr;
            for (auto const* candidate : successCandidates)
            {
                if (auto const* match = Found(lastMatches, candidate->name))
                {
                    successCandidate = candidate;
                    successMatch = match;
                    break;
                }
            }
            if (successCandidate != nullptr)
            {
                context.Log("INFO", "Startup Home candidate found; waiting for stable confirmation.");
                context.Sleep(std::max(0, monitorTask->successConfirmDelayMs));
                if (Capture(context))
                {
                    std::vector<Candidate> onlySuccess;
                    for (auto const* candidate : successCandidates) onlySuccess.push_back(*candidate);
                    auto const confirmationMatches = FindMatches(context, onlySuccess);
                    auto const confirmed = std::any_of(successCandidates.begin(), successCandidates.end(),
                        [&confirmationMatches](Candidate const* candidate) {
                            return Found(confirmationMatches, candidate->name) != nullptr;
                        });
                    if (confirmed)
                    {
                        Emit(context, TaskEventType::TaskCompleted, successCandidate->name, definition->name,
                            step, true, successMatch->score, successMatch->CenterX(), successMatch->CenterY(),
                            "Home screen confirmed after settle delay.");
                        Emit(context, TaskEventType::PipelineCompleted, monitorName, definition->name,
                            step, true, successMatch->score, successMatch->CenterX(), successMatch->CenterY(),
                            "Startup monitor confirmed Home.");
                        return {true, true, "The game Home screen was detected and confirmed."};
                    }
                }
                chainIndex = -1;
                context.Log("WARN", "Home confirmation was transient; resuming all startup monitor checks.");
                continue;
            }

            if (chainIndex >= 0 && chainIndex < static_cast<int>(triggerChain.size()))
            {
                auto const* current = triggerChain[static_cast<std::size_t>(chainIndex)];
                if (auto const* match = Found(lastMatches, current->name))
                {
                    if (!RunMonitorAction(context, *current, *match))
                        return {false, false, "Startup trigger chain action failed: " + current->name};
                    ++chainIndex;
                    if (chainIndex >= static_cast<int>(triggerChain.size())) chainIndex = -1;
                    else chainStepStarted = now();
                    continue;
                }
            }

            if (chainIndex < 0)
            {
                if (triggerCandidate != nullptr)
                {
                    if (auto const* match = Found(lastMatches, triggerCandidate->name))
                    {
                        if (!RunMonitorAction(context, *triggerCandidate, *match))
                            return {false, false, "Startup trigger action failed: " + triggerCandidate->name};
                        chainIndex = triggerChain.empty() ? -1 : 0;
                        if (chainIndex >= 0) chainStepStarted = now();
                        continue;
                    }
                }

                Candidate const* independentCandidate = nullptr;
                for (auto const& name : monitorTask->monitorTasks)
                {
                    if (successNames.contains(name)
                        || (monitorTask->triggerTask && CaseInsensitiveEqual{}(name, *monitorTask->triggerTask)))
                        continue;
                    auto found = byName.find(name);
                    if (found != byName.end() && Found(lastMatches, name) != nullptr)
                    {
                        independentCandidate = found->second;
                        break;
                    }
                }
                if (independentCandidate != nullptr)
                {
                    auto const* match = Found(lastMatches, independentCandidate->name);
                    if (!RunMonitorAction(context, *independentCandidate, *match))
                        return {false, false, "Startup monitor action failed: " + independentCandidate->name};
                    continue;
                }
            }

            context.Sleep(static_cast<int>(poll.count()));
        }
        catch (StartupObservationInvalidated const&)
        {
            chainIndex = -1;
            lastMatches.clear();
            context.Log("INFO", "Startup recovery dialog dismissed; recognizing controls again.");
        }
    }
    return {false, false, context.IsCancelled()
        ? "Startup monitor was canceled."
        : "Startup monitor could not confirm Home."};
}

} // namespace UmaAssistant::GameTasks
