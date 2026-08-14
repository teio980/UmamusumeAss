using System.Diagnostics;
using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Generic MAA-style executor for ordinary Hachimi JSON definitions.
/// Task names and transitions come from JSON; this class only implements the
/// reusable recognition, action, delay and state-machine mechanics.
/// </summary>
public sealed class HachimiJsonPipelineRunner
{
    private readonly IAdbRuntime _adbRuntime;
    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly ISettingsService _settingsService;

    public HachimiJsonPipelineRunner(
        IAdbRuntime adbRuntime,
        IVisualPipelineRuntime visualRuntime,
        ISettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(adbRuntime);
        ArgumentNullException.ThrowIfNull(visualRuntime);
        ArgumentNullException.ThrowIfNull(settingsService);
        _adbRuntime = adbRuntime;
        _visualRuntime = visualRuntime;
        _settingsService = settingsService;
    }

    public async Task<HachimiPipelineRunResult> RunAsync(
        LastVerifiedConnection connection,
        string definitionPath,
        string entryTask,
        HachimiPipelineRunOptions? options = null,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryTask);

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(
                definitionPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (definition is null)
        {
            return new HachimiPipelineRunResult(
                false,
                "The Hachimi pipeline definition could not be loaded.",
                0,
                null);
        }

        return await RunAsync(
                connection,
                definition,
                entryTask,
                options,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Executes an in-memory definition. The Developer page uses this overload
    /// so unsaved editor changes can be tested without writing a temporary JSON
    /// file or changing the user's resource directory.
    /// </summary>
    public async Task<HachimiPipelineRunResult> RunAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        string entryTask,
        HachimiPipelineRunOptions? options = null,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryTask);

        definition.Tasks = new Dictionary<string, HachimiPipelineTask>(
            definition.Tasks,
            StringComparer.OrdinalIgnoreCase);

        var state = new RunState(options ?? new HachimiPipelineRunOptions());
        return await RunGraphAsync(
                connection,
                definition,
                entryTask,
                state,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<HachimiPipelineRunResult> RunGraphAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        string entryTask,
        RunState state,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var current = entryTask;
        var guardLimit = Math.Max(100, definition.Tasks.Count * 40);

        for (var step = 0; step < guardLimit; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!definition.TryGetTask(current, out var task) || task is null)
            {
                return Fail(
                    logSink,
                    $"Pipeline transition points to undefined task '{current}'.",
                    state.CompletedUnits,
                    current);
            }

            var taskCount = state.GetTaskCount(current);
            if (HasExceededLimit(current, task, taskCount, state.Options))
            {
                AddLog(
                    logSink,
                    $"Task '{current}' reached maxTimes; following exceededNext.");
                var exceeded = FirstExisting(definition, task.ExceededNext);
                if (exceeded is null)
                {
                    return task.Required
                        ? Fail(
                            logSink,
                            $"Required task '{current}' exceeded maxTimes without exceededNext.",
                            state.CompletedUnits,
                            current)
                        : Succeed(state, current);
                }

                current = exceeded;
                continue;
            }

            state.IncrementTaskCount(current);
            AddTaskLog(
                logSink,
                current,
                $"Run #{taskCount + 1}: algorithm={task.Algorithm}, action={task.Action}, "
                + $"template={task.Template ?? "none"}, roi={FormatArray(task.Roi)}, "
                + $"threshold={task.TemplateThreshold:0.000}, timeout={task.TimeoutMilliseconds}ms, "
                + $"preDelay={task.PreDelay}ms, wait={task.WaitMilliseconds}ms, postDelay={task.PostDelay}ms.");

            var execution = await ExecuteTaskAsync(
                    connection,
                    definition,
                    current,
                    task,
                    state.Options,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!execution.Succeeded)
            {
                var errorNext = FirstExisting(definition, task.OnErrorNext);
                if (errorNext is not null)
                {
                    AddLog(
                        logSink,
                        $"Task '{current}' failed; following onErrorNext '{errorNext}'.",
                        LogEntryKind.Info);
                    current = errorNext;
                    continue;
                }

                if (!task.Required)
                {
                    AddLog(
                        logSink,
                        $"Optional task '{current}' was not completed; continuing.",
                        LogEntryKind.Info);
                    current = FirstExisting(definition, task.Next)
                        ?? string.Empty;
                    if (string.IsNullOrEmpty(current))
                        return Succeed(state, execution.LastTask ?? current);
                    continue;
                }

                return Fail(
                    logSink,
                    execution.Message,
                    state.CompletedUnits,
                    current);
            }

            if (!string.IsNullOrWhiteSpace(task.CountAs))
                state.CompletedUnits++;

            if (task.Success)
                return Succeed(state, current);

            current = FirstExisting(definition, task.Next) ?? string.Empty;
            if (string.IsNullOrEmpty(current))
                return Succeed(state, execution.LastTask ?? entryTask);
        }

        return Fail(
            logSink,
            $"Pipeline exceeded the transition guard ({guardLimit} steps). Check JSON next/maxTimes transitions.",
            state.CompletedUnits,
            current);
    }

    private async Task<TaskExecutionResult> ExecuteTaskAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        string taskName,
        HachimiPipelineTask task,
        HachimiPipelineRunOptions runOptions,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        if (task.PreDelay > 0)
        {
            await _visualRuntime.DelayAsync(task.PreDelay, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var subTask in task.Sub)
        {
            if (!definition.TryGetTask(subTask, out _))
            {
                return TaskExecutionResult.Failed(
                    $"Task '{taskName}' references undefined sub task '{subTask}'.");
            }

            var subResult = await RunGraphAsync(
                    connection,
                    definition,
                    subTask,
                    new RunState(new HachimiPipelineRunOptions()),
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!subResult.Succeeded)
                return TaskExecutionResult.Failed(subResult.Message);
        }

        TemplateMatchResult? match = null;
        var action = Normalize(task.Action);
        var algorithm = Normalize(task.Algorithm);

        if (algorithm is "parallelmonitor" or "raceresultmonitor")
        {
            var monitorResult = await ExecuteParallelMonitorAsync(
                    connection,
                    definition,
                    taskName,
                    task,
                    runOptions,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (monitorResult.Succeeded)
            {
                if (task.WaitMilliseconds > 0)
                {
                    await _visualRuntime.DelayAsync(task.WaitMilliseconds, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (task.PostDelay > 0)
                {
                    await _visualRuntime.DelayAsync(task.PostDelay, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return monitorResult;
        }

        if (!string.IsNullOrWhiteSpace(task.Template))
        {
            var roi = task.Roi;
            var pollInterval = task.PollIntervalMilliseconds > 0
                ? task.PollIntervalMilliseconds
                : definition.Timing.PollIntervalMilliseconds;
            if (runOptions.RoiOverrides is not null
                && runOptions.RoiOverrides.TryGetValue(taskName, out var roiOverride))
            {
                roi = roiOverride;
            }

            AddTaskLog(
                logSink,
                taskName,
                $"Waiting for template '{task.Template}' in ROI {FormatArray(roi)} "
                + $"(threshold {task.TemplateThreshold:0.000}, timeout {task.TimeoutMilliseconds}ms, "
                + $"poll {pollInterval}ms).");

            match = await _visualRuntime.WaitForMatchAsync(
                    connection,
                    task.Template,
                    roi,
                    task.TemplateThreshold,
                    definition.ReferenceWidth,
                    definition.ReferenceHeight,
                    task.TimeoutMilliseconds,
                    pollInterval,
                    taskName,
                    definition.BaseDirectory,
                    cancellationToken)
                .ConfigureAwait(false);

            if (match is null || !match.Found)
            {
                var bestScore = match is null
                    ? "none"
                    : match.Score.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
                return TaskExecutionResult.Failed(
                    $"Timed out waiting for JSON task '{taskName}' "
                    + $"(best score {bestScore} / threshold {task.TemplateThreshold:0.000}).");
            }

            AddTaskLog(
                logSink,
                taskName,
                $"Template matched: score {match.Score:0.000}, center ({match.CenterX},{match.CenterY}), "
                + $"size {match.Width}x{match.Height}.",
                LogEntryKind.Success);
        }

        switch (action)
        {
            case "selectdailyracerunner":
            case "selecturatrainee":
                if (runOptions.CustomActionExecutor is null)
                {
                    return TaskExecutionResult.Failed(
                        $"JSON task '{taskName}' requires a custom action executor.");
                }

                var customResult = await runOptions.CustomActionExecutor(
                        connection,
                        definition,
                        taskName,
                        task,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!customResult.Succeeded)
                    return TaskExecutionResult.Failed(customResult.Message);
                if (customResult.Match is not null)
                {
                    await _visualRuntime.TapMatchAsync(
                            connection,
                            customResult.Match,
                            taskName,
                            cancellationToken)
                        .ConfigureAwait(false);
                    AddTaskLog(
                        logSink,
                        taskName,
                        $"Clicked dynamic match at ({customResult.Match.CenterX},{customResult.Match.CenterY}), "
                        + $"score {customResult.Match.Score:0.000}.",
                        LogEntryKind.Success);
                }
                if (!string.IsNullOrWhiteSpace(customResult.Message))
                {
                    AddTaskLog(logSink, taskName, customResult.Message, LogEntryKind.Success);
                }

                break;

            case "clickrect":
                var rectCenter = ResolveRectCenter(
                    task.SpecificRect,
                    definition.ReferenceWidth,
                    definition.ReferenceHeight,
                    connection.Width,
                    connection.Height);
                if (rectCenter is null)
                {
                    return TaskExecutionResult.Failed(
                        $"JSON task '{taskName}' uses ClickRect but has no valid specificRect.");
                }

                var rectTap = await _adbRuntime.TapAsync(
                        connection.AdbPath,
                        connection.Serial,
                        rectCenter.Value.X,
                        rectCenter.Value.Y,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (rectTap.Error is not null || rectTap.TimedOut || rectTap.ExitCode != 0)
                {
                    return TaskExecutionResult.Failed(
                        $"ADB ClickRect failed for '{taskName}': {rectTap.Stderr}");
                }

                AddTaskLog(
                    logSink,
                    taskName,
                    $"Clicked rect '{taskName}' at ({rectCenter.Value.X},{rectCenter.Value.Y}).",
                    LogEntryKind.Success);
                break;

            case "clickself":
                if (match is null)
                {
                    return TaskExecutionResult.Failed(
                        $"JSON task '{taskName}' uses ClickSelf but has no template match.");
                }

                await _visualRuntime.TapMatchAsync(
                        connection,
                        match,
                        taskName,
                        cancellationToken)
                    .ConfigureAwait(false);
                AddTaskLog(
                    logSink,
                    taskName,
                    $"Clicked '{taskName}' at ({match.CenterX},{match.CenterY}), "
                    + $"score {match.Score:0.000} / threshold {task.TemplateThreshold:0.000}.",
                    LogEntryKind.Success);
                break;

            case "swipe":
                if (task.Swipe is null)
                {
                    return TaskExecutionResult.Failed(
                        $"JSON task '{taskName}' uses Swipe but has no swipe coordinates.");
                }

                await _visualRuntime.SwipeAsync(
                        connection,
                        task.Swipe,
                        definition.ReferenceWidth,
                        definition.ReferenceHeight,
                        taskName,
                        cancellationToken)
                    .ConfigureAwait(false);
                AddTaskLog(
                    logSink,
                    taskName,
                    $"Swiped [{FormatArray(task.Swipe)}] for '{taskName}'.",
                    LogEntryKind.Success);
                break;

            case "runpipeline":
                var nestedResult = await RunNestedPipelineAsync(
                        connection,
                        definition,
                        taskName,
                        task,
                        runOptions,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!nestedResult.Succeeded)
                    return nestedResult;
                break;

            case "back":
                var back = await _adbRuntime.BackAsync(
                        connection.AdbPath,
                        connection.Serial,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (back.Error is not null || back.TimedOut || back.ExitCode != 0)
                {
                    return TaskExecutionResult.Failed(
                        $"ADB Back failed for JSON task '{taskName}': {back.Stderr}");
                }

                AddTaskLog(logSink, taskName, "Pressed Android Back.", LogEntryKind.Success);
                break;

            case "wait":
                if (match is not null)
                {
                    AddTaskLog(
                        logSink,
                        taskName,
                        $"Matched '{taskName}': score {match.Score:0.000} "
                        + $"/ threshold {task.TemplateThreshold:0.000}.",
                        LogEntryKind.Success);
                }
                break;

            case "donothing":
            case "screenshot":
            case "capturescreenshot":
                AddTaskLog(
                    logSink,
                    taskName,
                    $"Completed action '{task.Action}'.",
                    LogEntryKind.Success);
                break;

            case "stop":
                return TaskExecutionResult.Failed(
                    $"JSON task '{taskName}' requested pipeline stop.");

            case "":
                if (algorithm is "justreturn" or "wait")
                    break;
                return TaskExecutionResult.Failed(
                    $"JSON task '{taskName}' has no action.");

            default:
                return TaskExecutionResult.Failed(
                    $"JSON task '{taskName}' uses unsupported action '{task.Action}'.");
        }

        if (task.WaitMilliseconds > 0)
        {
            await _visualRuntime.DelayAsync(task.WaitMilliseconds, cancellationToken)
                .ConfigureAwait(false);
        }

        if (task.PostDelay > 0)
        {
            await _visualRuntime.DelayAsync(task.PostDelay, cancellationToken)
                .ConfigureAwait(false);
        }

        return TaskExecutionResult.Completed(taskName);
    }

    private async Task<TaskExecutionResult> ExecuteParallelMonitorAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        string taskName,
        HachimiPipelineTask monitorTask,
        HachimiPipelineRunOptions runOptions,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var monitorNames = monitorTask.MonitorTasks
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var successName = monitorTask.SuccessTask?.Trim();
        if (string.IsNullOrWhiteSpace(successName))
        {
            return TaskExecutionResult.Failed(
                $"Parallel monitor '{taskName}' has no successTask.");
        }

        var candidateNames = monitorNames
            .Append(successName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidates = new List<ParallelMonitorCandidate>(candidateNames.Length);
        foreach (var candidateName in candidateNames)
        {
            if (!definition.TryGetTask(candidateName, out var candidateTask)
                || candidateTask is null)
            {
                return TaskExecutionResult.Failed(
                    $"Parallel monitor task '{candidateName}' is not defined.");
            }

            var template = await _visualRuntime.LoadTemplateAsync(
                    candidateTask.Template,
                    definition.BaseDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            if (template is null)
            {
                return TaskExecutionResult.Failed(
                    $"Template for parallel monitor task '{candidateName}' "
                    + "was not found or could not be decoded.");
            }

            candidates.Add(new ParallelMonitorCandidate(candidateName, candidateTask, template));
        }

        var byName = candidates.ToDictionary(
            candidate => candidate.Name,
            StringComparer.OrdinalIgnoreCase);
        var monitorCandidates = monitorNames
            .Where(byName.ContainsKey)
            .Select(name => byName[name])
            .ToArray();
        var successCandidate = byName[successName];
        if (monitorCandidates.Length == 0)
        {
            return TaskExecutionResult.Failed(
                $"Parallel monitor '{taskName}' has no monitorTasks.");
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(
            monitorTask.TimeoutMilliseconds,
            0,
            10 * 60 * 1000));
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(
            monitorTask.PollIntervalMilliseconds > 0
                ? monitorTask.PollIntervalMilliseconds
                : definition.Timing.PollIntervalMilliseconds,
            50,
            10_000));
        AddTaskLog(
            logSink,
            taskName,
            $"Parallel monitor: candidates=[{string.Join(", ", candidateNames)}], "
            + $"success='{successCandidate.Name}', timeout={timeout.TotalMilliseconds:0}ms, "
            + $"poll={poll.TotalMilliseconds:0}ms.");
        var started = Stopwatch.GetTimestamp();
        Dictionary<string, TemplateMatchResult>? lastMatches = null;
        GrayImage? lastScreen = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var screen = await _visualRuntime.CaptureGrayAsync(
                    connection,
                    cancellationToken)
                .ConfigureAwait(false);
            if (screen is not null)
            {
                lastScreen = screen;
                var matches = await FindParallelMonitorMatchesAsync(
                        screen,
                        candidates,
                        definition,
                        cancellationToken)
                    .ConfigureAwait(false);
                lastMatches = matches;

                // The stop condition always wins over a stale button match in
                // the same screenshot.
                if (matches.TryGetValue(successCandidate.Name, out var successMatch)
                    && successMatch.Found)
                {
                    AddTaskLog(
                        logSink,
                        taskName,
                        $"{taskName}: success task '{successCandidate.Name}' detected; leaving parallel monitor.",
                        LogEntryKind.Success);
                    return TaskExecutionResult.Completed(taskName);
                }

                foreach (var candidate in monitorCandidates)
                {
                    if (!matches.TryGetValue(candidate.Name, out var match)
                        || !match.Found)
                    {
                        continue;
                    }

                    var actionResult = await ExecuteParallelMonitorActionAsync(
                            connection,
                            definition,
                            candidate,
                            match,
                            runOptions,
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!actionResult.Succeeded)
                    {
                        if (candidate.Task.Required)
                            return actionResult;

                        AddTaskLog(
                            logSink,
                            taskName,
                            $"{taskName}: optional monitor task '{candidate.Name}' failed; continuing to monitor.",
                            LogEntryKind.Info);
                    }
                    else
                    {
                        AddTaskLog(
                            logSink,
                            taskName,
                            $"{taskName}: parallel monitor action '{candidate.Name}' completed.",
                            LogEntryKind.Success);
                    }

                    // Execute at most one action from a screenshot, then take
                    // a fresh screenshot after the UI has settled.
                    break;
                }
            }

            if (Stopwatch.GetElapsedTime(started) >= timeout)
            {
                var matchSummary = lastMatches is null
                    ? "no decoded screenshot"
                    : string.Join(
                        ", ",
                        lastMatches.Select(pair =>
                            $"{pair.Key}={pair.Value.Score:0.000}"));
                return TaskExecutionResult.Failed(
                    $"Timed out waiting for parallel monitor '{taskName}' "
                    + $"(screen={lastScreen?.Width}x{lastScreen?.Height}; {matchSummary}).");
            }

            await _visualRuntime.DelayAsync(
                    (int)poll.TotalMilliseconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<string, TemplateMatchResult>> FindParallelMonitorMatchesAsync(
        GrayImage screen,
        IReadOnlyList<ParallelMonitorCandidate> candidates,
        HachimiPipelineDefinition definition,
        CancellationToken cancellationToken)
    {
        var matchTasks = candidates.Select(candidate => Task.Run(
            () => (candidate.Name, Match: TemplateMatcher.Find(
                screen,
                candidate.Template,
                candidate.Task.Roi,
                candidate.Task.TemplateThreshold,
                definition.ReferenceWidth,
                definition.ReferenceHeight)),
            cancellationToken));
        var results = await Task.WhenAll(matchTasks).ConfigureAwait(false);
        return results.ToDictionary(
            result => result.Name,
            result => result.Match,
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<TaskExecutionResult> ExecuteParallelMonitorActionAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        ParallelMonitorCandidate candidate,
        TemplateMatchResult match,
        HachimiPipelineRunOptions runOptions,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var task = candidate.Task;
        if (task.PreDelay > 0)
        {
            await _visualRuntime.DelayAsync(task.PreDelay, cancellationToken)
                .ConfigureAwait(false);
        }

        TaskExecutionResult actionResult;
        switch (Normalize(task.Action))
        {
            case "clickself":
                await _visualRuntime.TapMatchAsync(
                        connection,
                        match,
                        candidate.Name,
                        cancellationToken)
                    .ConfigureAwait(false);
                actionResult = TaskExecutionResult.Completed(candidate.Name);
                break;

            case "runpipeline":
                actionResult = await RunNestedPipelineAsync(
                        connection,
                        definition,
                        candidate.Name,
                        task,
                        runOptions,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                return TaskExecutionResult.Failed(
                    $"Parallel monitor task '{candidate.Name}' uses unsupported action '{task.Action}'.");
        }

        if (!actionResult.Succeeded)
            return actionResult;

        if (task.WaitMilliseconds > 0)
        {
            await _visualRuntime.DelayAsync(task.WaitMilliseconds, cancellationToken)
                .ConfigureAwait(false);
        }

        if (task.PostDelay > 0)
        {
            await _visualRuntime.DelayAsync(task.PostDelay, cancellationToken)
                .ConfigureAwait(false);
        }

        return actionResult;
    }

    private async Task<TaskExecutionResult> RunNestedPipelineAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition parentDefinition,
        string taskName,
        HachimiPipelineTask task,
        HachimiPipelineRunOptions parentOptions,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(task.Pipeline))
        {
            return TaskExecutionResult.Failed(
                $"JSON task '{taskName}' uses RunPipeline but has no pipeline path.");
        }

        var requestedPipeline = task.Pipeline.Trim();
        var isShopPipeline = IsShopPipeline(requestedPipeline);

        if (parentOptions.PipelineDepth >= HachimiPipelineRunOptions.MaximumPipelineDepth)
        {
            return TaskExecutionResult.Failed(
                $"JSON task '{taskName}' exceeded the nested pipeline depth limit.");
        }

        var nestedPath = Path.IsPathRooted(requestedPipeline)
            ? requestedPipeline
            : Path.GetFullPath(Path.Combine(parentDefinition.BaseDirectory, requestedPipeline));
        var nestedEntry = task.Entry?.Trim() is { Length: > 0 } entry
            ? entry
            : "home";
        var nestedOptions = new HachimiPipelineRunOptions
        {
            PipelineDepth = parentOptions.PipelineDepth + 1,
            MaxTimesOverrides = isShopPipeline
                ? CreateShopOverrides(_settingsService.Load().Hachimi.Shop)
                : null,
        };

        AddLog(
            logSink,
            $"Calling nested JSON pipeline '{nestedPath}' from '{taskName}'.");
        var result = await RunAsync(
                connection,
                nestedPath,
                nestedEntry,
                nestedOptions,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return TaskExecutionResult.Failed(
                $"Nested pipeline '{nestedPath}' failed: {result.Message}");
        }

        AddLog(
            logSink,
            $"Nested JSON pipeline '{nestedPath}' completed.",
            LogEntryKind.Success);
        return TaskExecutionResult.Completed(taskName);
    }

    private static bool IsShopPipeline(string path) =>
        string.Equals(
            Path.GetFileName(path),
            "shop.json",
            StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, int> CreateShopOverrides(
        HachimiShopSettings settings)
    {
        if (!settings.Enabled)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["shopProbe"] = 0,
            };
        }

        return settings.ToOptions().ToMaxTimesOverrides();
    }

    private static bool HasExceededLimit(
        string taskName,
        HachimiPipelineTask task,
        int taskCount,
        HachimiPipelineRunOptions options)
    {
        if (options.MaxTimesOverrides is not null
            && options.MaxTimesOverrides.TryGetValue(taskName, out var overrideLimit))
        {
            return taskCount >= Math.Max(0, overrideLimit);
        }

        return task.MaxTimes > 0 && taskCount >= task.MaxTimes;
    }

    private static string? FirstExisting(
        HachimiPipelineDefinition definition,
        IEnumerable<string>? candidates)
    {
        if (candidates is null)
            return null;

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate)
                && definition.TryGetTask(candidate, out _))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Normalize(string? value) =>
        value?.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant()
        ?? string.Empty;

    private static (int X, int Y)? ResolveRectCenter(
        int[]? rect,
        int referenceWidth,
        int referenceHeight,
        int actualWidth,
        int actualHeight)
    {
        if (rect is null || rect.Length < 4)
            return null;

        var width = Math.Max(1, referenceWidth);
        var height = Math.Max(1, referenceHeight);
        var centerX = rect[0] + rect[2] / 2.0;
        var centerY = rect[1] + rect[3] / 2.0;
        var x = (int)Math.Round(centerX * Math.Max(1, actualWidth) / width);
        var y = (int)Math.Round(centerY * Math.Max(1, actualHeight) / height);
        return (
            Math.Clamp(x, 0, Math.Max(0, actualWidth - 1)),
            Math.Clamp(y, 0, Math.Max(0, actualHeight - 1)));
    }

    private static HachimiPipelineRunResult Succeed(
        RunState state,
        string? lastTask) =>
        new(
            true,
            $"JSON pipeline completed ({state.CompletedUnits} counted unit(s)).",
            state.CompletedUnits,
            lastTask);

    private static HachimiPipelineRunResult Fail(
        IGrassTaskLogSink? logSink,
        string message,
        int completedUnits,
        string? lastTask)
    {
        AddLog(logSink, message, LogEntryKind.Failure);
        return new HachimiPipelineRunResult(false, message, completedUnits, lastTask);
    }

    private static void AddLog(
        IGrassTaskLogSink? logSink,
        string details,
        LogEntryKind kind = LogEntryKind.Info) =>
        logSink?.Add("JSON Pipeline", details, kind);

    private static void AddTaskLog(
        IGrassTaskLogSink? logSink,
        string taskName,
        string details,
        LogEntryKind kind = LogEntryKind.Info) =>
        logSink?.Add(taskName, details, kind);

    private static string FormatArray(int[]? values) =>
        values is { Length: > 0 }
            ? $"[{string.Join(",", values)}]"
            : "none";

    private sealed class RunState
    {
        private readonly Dictionary<string, int> _taskCounts =
            new(StringComparer.OrdinalIgnoreCase);

        public RunState(HachimiPipelineRunOptions options) => Options = options;

        public HachimiPipelineRunOptions Options { get; }

        public int CompletedUnits { get; set; }

        public int GetTaskCount(string taskName) =>
            _taskCounts.TryGetValue(taskName, out var count) ? count : 0;

        public void IncrementTaskCount(string taskName) =>
            _taskCounts[taskName] = GetTaskCount(taskName) + 1;
    }

    private sealed record ParallelMonitorCandidate(
        string Name,
        HachimiPipelineTask Task,
        GrayImage Template);

    private sealed record TaskExecutionResult(
        bool Succeeded,
        string Message,
        string? LastTask)
    {
        public static TaskExecutionResult Completed(string taskName) =>
            new(true, string.Empty, taskName);

        public static TaskExecutionResult Failed(string message) =>
            new(false, message, null);
    }
}

public sealed class HachimiPipelineRunOptions
{
    public const int MaximumPipelineDepth = 4;

    /// <summary>
    /// Runtime limits supplied by a caller, such as raceCount - 1 for the
    /// JSON multi-race ticket-plus loop. A present value of zero means do not execute the
    /// task and follow its exceededNext transition immediately.
    /// </summary>
    public IReadOnlyDictionary<string, int>? MaxTimesOverrides { get; init; }

    /// <summary>
    /// Runtime ROI adjustments supplied by a caller for settings-driven rows.
    /// The task definition keeps the default ROI for backwards compatibility.
    /// </summary>
    public IReadOnlyDictionary<string, int[]>? RoiOverrides { get; init; }

    public Func<
        LastVerifiedConnection,
        HachimiPipelineDefinition,
        string,
        HachimiPipelineTask,
        IGrassTaskLogSink?,
        CancellationToken,
        Task<HachimiCustomActionResult>>? CustomActionExecutor { get; init; }

    public int PipelineDepth { get; init; }
}

public sealed record HachimiCustomActionResult(
    bool Succeeded,
    string Message,
    TemplateMatchResult? Match = null)
{
    public static HachimiCustomActionResult Success(
        string message,
        TemplateMatchResult? match = null) =>
        new(true, message, match);

    public static HachimiCustomActionResult Failure(string message) => new(false, message);
}

public sealed record HachimiPipelineRunResult(
    bool Succeeded,
    string Message,
    int CompletedUnits,
    string? LastTask);
