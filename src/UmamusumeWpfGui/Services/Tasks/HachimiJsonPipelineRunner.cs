using System.Diagnostics;
using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.ViewModels.Tasks;

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
    private readonly ShopTaskSettingsViewModel _shopSettings;

    public HachimiJsonPipelineRunner(
        IAdbRuntime adbRuntime,
        IVisualPipelineRuntime visualRuntime,
        ShopTaskSettingsViewModel shopSettings)
    {
        ArgumentNullException.ThrowIfNull(adbRuntime);
        ArgumentNullException.ThrowIfNull(visualRuntime);
        ArgumentNullException.ThrowIfNull(shopSettings);
        _adbRuntime = adbRuntime;
        _visualRuntime = visualRuntime;
        _shopSettings = shopSettings;
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
            AddLog(logSink, $"Running JSON task '{current}'.");

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
            if (runOptions.RoiOverrides is not null
                && runOptions.RoiOverrides.TryGetValue(taskName, out var roiOverride))
            {
                roi = roiOverride;
            }

            match = await _visualRuntime.WaitForMatchAsync(
                    connection,
                    task.Template,
                    roi,
                    task.TemplateThreshold,
                    definition.ReferenceWidth,
                    definition.ReferenceHeight,
                    task.TimeoutMilliseconds,
                    task.PollIntervalMilliseconds > 0
                        ? task.PollIntervalMilliseconds
                        : definition.Timing.PollIntervalMilliseconds,
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
        }

        switch (action)
        {
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
                AddLog(
                    logSink,
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
                AddLog(logSink, $"Swiped for '{taskName}'.", LogEntryKind.Success);
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

                break;

            case "wait":
                if (match is not null)
                {
                    AddLog(
                        logSink,
                        $"Matched '{taskName}': score {match.Score:0.000} "
                        + $"/ threshold {task.TemplateThreshold:0.000}.",
                        LogEntryKind.Success);
                }
                break;

            case "donothing":
            case "screenshot":
            case "capturescreenshot":
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
                    AddLog(
                        logSink,
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

                        AddLog(
                            logSink,
                            $"{taskName}: optional monitor task '{candidate.Name}' failed; continuing to monitor.",
                            LogEntryKind.Info);
                    }
                    else
                    {
                        AddLog(
                            logSink,
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
            MaxTimesOverrides = IsShopPipeline(nestedPath)
                ? _shopSettings.ToOptions().ToMaxTimesOverrides()
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
    /// JSON race-again loop. A present value of zero means do not execute the
    /// task and follow its exceededNext transition immediately.
    /// </summary>
    public IReadOnlyDictionary<string, int>? MaxTimesOverrides { get; init; }

    /// <summary>
    /// Runtime ROI adjustments supplied by a caller for settings-driven rows.
    /// The task definition keeps the default ROI for backwards compatibility.
    /// </summary>
    public IReadOnlyDictionary<string, int[]>? RoiOverrides { get; init; }

    public int PipelineDepth { get; init; }
}

public sealed record HachimiPipelineRunResult(
    bool Succeeded,
    string Message,
    int CompletedUnits,
    string? LastTask);
