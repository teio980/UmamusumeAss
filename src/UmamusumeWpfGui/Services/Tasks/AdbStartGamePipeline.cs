using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Services.Tasks;






public sealed class AdbStartGamePipeline : IStartGamePipeline
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IAdbRuntime _adbRuntime;
    private readonly IAsyncDelay _asyncDelay;
    private readonly string _definitionPath;

    public AdbStartGamePipeline(
        IAdbRuntime adbRuntime,
        IAsyncDelay asyncDelay)
        : this(
            adbRuntime,
            asyncDelay,
            Path.Combine(
                AppContext.BaseDirectory,
                "resource",
                "hachimi",
                "start_game.json"))
    {
    }

    internal AdbStartGamePipeline(
        IAdbRuntime adbRuntime,
        IAsyncDelay asyncDelay,
        string definitionPath)
    {
        ArgumentNullException.ThrowIfNull(adbRuntime);
        ArgumentNullException.ThrowIfNull(asyncDelay);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionPath);
        _adbRuntime = adbRuntime;
        _asyncDelay = asyncDelay;
        _definitionPath = definitionPath;
    }

    public async Task<StartGamePipelineResult> RunAsync(
        LastVerifiedConnection connection,
        string packageName,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);

        var definition = await LoadDefinitionAsync(cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            AddLog(
                logSink,
                "Start game pipeline",
                "No start_game.json was found; process launch is treated as ready.");
            return new StartGamePipelineResult(true, false, "No start game pipeline is configured.");
        }

        if (definition.Tasks.Count == 0)
        {
            AddLog(
                logSink,
                "Start game pipeline",
                "The pipeline has no navigation tasks; process launch is treated as ready.");
            return new StartGamePipelineResult(true, false, "The start game pipeline is empty.");
        }

        var current = ResolveStartTask(definition);
        var visited = 0;
        while (!string.IsNullOrWhiteSpace(current)
            && visited++ < Math.Max(100, definition.Tasks.Count * 4))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!definition.Tasks.TryGetValue(current, out var task))
            {
                return Fail(
                    logSink,
                    $"Pipeline task '{current}' is not defined.");
            }

            AddLog(logSink, current, "Running pipeline task.");
            var taskResult = await ExecuteTaskAsync(
                connection,
                packageName,
                current,
                definition,
                task,
                logSink,
                cancellationToken).ConfigureAwait(false);
            if (!taskResult.Succeeded)
            {
                if (taskResult.Fatal)
                    return Fail(logSink, taskResult.Message);

                var fallback = task.OnErrorNext.FirstOrDefault(
                    next => definition.Tasks.ContainsKey(next));
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    AddLog(
                        logSink,
                        current,
                        $"Task failed: {taskResult.Message}; taking fallback '{fallback}'.",
                        LogEntryKind.Failure);
                    current = fallback;
                    continue;
                }

                if (!task.Required)
                {
                    AddLog(logSink, current, "Optional task failed; continuing.", LogEntryKind.Failure);
                }
                else
                {
                    return Fail(logSink, taskResult.Message);
                }
            }

            if (task.Success)
            {
                AddLog(logSink, current, "Home screen detected.", LogEntryKind.Success);
                return new StartGamePipelineResult(true, true, "The game home screen was detected.");
            }

            current = task.Next.FirstOrDefault(
                next => definition.Tasks.ContainsKey(next));
        }

        if (visited >= Math.Max(100, definition.Tasks.Count * 4))
            return Fail(logSink, "The start game pipeline exceeded its task guard.");

        AddLog(logSink, "Start game pipeline", "Pipeline completed.", LogEntryKind.Success);
        return new StartGamePipelineResult(true, false, "The start game pipeline completed.");
    }

    private async Task<PipelineTaskResult> ExecuteTaskAsync(
        LastVerifiedConnection connection,
        string packageName,
        string taskName,
        StartGamePipelineDefinition definition,
        StartGamePipelineTask task,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        if (IsStartupMonitorTask(task))
        {
            return await ExecuteStartupMonitorAsync(
                connection,
                packageName,
                taskName,
                definition,
                task,
                logSink,
                cancellationToken).ConfigureAwait(false);
        }

        var template = IsTemplateTask(task)
            ? await LoadTemplateAsync(task.Template, cancellationToken).ConfigureAwait(false)
            : null;
        if (IsTemplateTask(task) && template is null)
        {
            return new PipelineTaskResult(
                false,
                $"Template for '{taskName}' was not found or could not be decoded.",
                true);
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(
            task.TimeoutMilliseconds,
            0,
            10 * 60 * 1000));
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(
            task.PollIntervalMilliseconds,
            50,
            10_000));
        var started = Stopwatch.GetTimestamp();
        var attempts = 0;
        TemplateMatchResult? lastMatch = null;
        GrayImage? lastScreen = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            TemplateMatchResult? match = null;
            GrayImage? screen = null;

            if (template is not null)
            {




                var screenshot = await CapturePipelineScreenshotAsync(
                    connection,
                    cancellationToken).ConfigureAwait(false);
                screen = screenshot is null ? null : GrayImageCodec.FromScreenshot(screenshot);
                if (screen is not null)
                {
                    lastScreen = screen;
                    match = TemplateMatcher.Find(
                        screen,
                        template,
                        task.Roi,
                        task.TemplateThreshold,
                        task.ReferenceWidth,
                        task.ReferenceHeight);
                    lastMatch = match;
                }
            }

            var recognized = template is null || match?.Found == true;
            if (recognized)
            {
                if (task.PreDelay > 0)
                {
                    await _asyncDelay.DelayAsync(
                        TimeSpan.FromMilliseconds(task.PreDelay),
                        cancellationToken).ConfigureAwait(false);
                }

                var actionResult = await ExecuteActionAsync(
                    connection,
                    task,
                    match,
                    screen,
                    cancellationToken).ConfigureAwait(false);
                if (actionResult is { Succeeded: false })
                    return actionResult;

                if (task.PostDelay > 0)
                {
                    await _asyncDelay.DelayAsync(
                        TimeSpan.FromMilliseconds(task.PostDelay),
                        cancellationToken).ConfigureAwait(false);
                }

                var score = match is null
                    ? string.Empty
                    : $" score={match.Score.ToString("0.000", CultureInfo.InvariantCulture)}";
                AddLog(
                    logSink,
                    taskName,
                    $"{actionResult?.Message ?? $"Action '{task.Action}' completed."}{score}",
                    LogEntryKind.Success);
                return actionResult ?? new PipelineTaskResult(true, "Task completed.");
            }

            if (Stopwatch.GetElapsedTime(started) >= timeout
                || (task.MaxTimes > 0 && attempts >= task.MaxTimes && timeout == TimeSpan.Zero))
            {
                var diagnostics = template is null
                    ? string.Empty
                    : lastMatch is null
                        ? $"lastScore=none; threshold={task.TemplateThreshold.ToString("0.000", CultureInfo.InvariantCulture)}; screen=none"
                        : $"lastScore={lastMatch.Score.ToString("0.000", CultureInfo.InvariantCulture)}; "
                          + $"threshold={task.TemplateThreshold.ToString("0.000", CultureInfo.InvariantCulture)}; "
                          + $"found={lastMatch.Found}; screen={lastScreen?.Width}x{lastScreen?.Height}";
                return new PipelineTaskResult(
                    false,
                    $"Timed out waiting for '{taskName}'. {diagnostics}".TrimEnd());
            }

            await _asyncDelay.DelayAsync(poll, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PipelineTaskResult> ExecuteStartupMonitorAsync(
        LastVerifiedConnection connection,
        string packageName,
        string taskName,
        StartGamePipelineDefinition definition,
        StartGamePipelineTask monitorTask,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var candidateNames = monitorTask.MonitorTasks
            .Concat(monitorTask.TriggerChain)
            .Append(monitorTask.TriggerTask ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var candidates = new List<StartupMonitorCandidate>(candidateNames.Length);
        foreach (var candidateName in candidateNames)
        {
            if (!definition.Tasks.TryGetValue(candidateName, out var candidateTask))
            {
                return new PipelineTaskResult(
                    false,
                    $"Startup monitor task '{candidateName}' is not defined.",
                    true);
            }

            var template = await LoadTemplateAsync(
                candidateTask.Template,
                cancellationToken).ConfigureAwait(false);
            if (template is null)
            {
                return new PipelineTaskResult(
                    false,
                    $"Template for startup monitor task '{candidateName}' was not found or could not be decoded.",
                    true);
            }

            candidates.Add(new StartupMonitorCandidate(candidateName, candidateTask, template));
        }

        var byName = candidates.ToDictionary(
            candidate => candidate.Name,
            StringComparer.OrdinalIgnoreCase);
        var monitorCandidates = monitorTask.MonitorTasks
            .Where(byName.ContainsKey)
            .Select(name => byName[name])
            .ToArray();
        var triggerCandidate = !string.IsNullOrWhiteSpace(monitorTask.TriggerTask)
            && byName.TryGetValue(monitorTask.TriggerTask, out var trigger)
                ? trigger
                : null;
        var triggerChain = monitorTask.TriggerChain
            .Where(byName.ContainsKey)
            .Select(name => byName[name])
            .ToArray();
        var successCandidates = monitorCandidates
            .Where(candidate => candidate.Task.Success)
            .ToArray();
        var successCandidateNames = successCandidates
            .Select(candidate => candidate.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (monitorCandidates.Length == 0)
            return new PipelineTaskResult(false, "Startup monitor has no check tasks.", true);
        if (successCandidates.Length == 0)
            return new PipelineTaskResult(
                false,
                "Startup monitor has no success task.",
                true);

        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(
            monitorTask.TimeoutMilliseconds,
            0,
            10 * 60 * 1000));
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(
            monitorTask.PollIntervalMilliseconds,
            50,
            10_000));
        var started = Stopwatch.GetTimestamp();
        var chainIndex = -1;
        Dictionary<string, TemplateMatchResult>? lastMatches = null;
        GrayImage? lastScreen = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var screenshot = await CapturePipelineScreenshotAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
            var screen = screenshot is null ? null : GrayImageCodec.FromScreenshot(screenshot);
            if (screen is not null)
            {
                lastScreen = screen;
                var activeCandidates = chainIndex >= 0
                    ? successCandidates
                        .Concat(triggerChain)
                        .ToArray()
                    : monitorCandidates;
                var matches = await FindStartupMonitorMatchesAsync(
                    screen,
                    activeCandidates,
                    cancellationToken).ConfigureAwait(false);
                lastMatches = matches;

                var successMatch = successCandidates
                    .Select(candidate => FindFoundMatch(matches, candidate.Name))
                    .FirstOrDefault(match => match is not null);
                if (successMatch is not null)
                {
                    AddLog(
                        logSink,
                        "StartupMonitor",
                        $"Startup success task '{successMatch.Value.Name}' detected; "
                        + $"waiting {Math.Max(0, monitorTask.SuccessConfirmationDelayMilliseconds)}ms before confirmation.");
                    await _asyncDelay.DelayAsync(
                        TimeSpan.FromMilliseconds(Math.Max(
                            0,
                            monitorTask.SuccessConfirmationDelayMilliseconds)),
                        cancellationToken).ConfigureAwait(false);

                    var confirmationScreenshot = await CapturePipelineScreenshotAsync(
                        connection,
                        cancellationToken).ConfigureAwait(false);
                    var confirmationScreen = confirmationScreenshot is null
                        ? null
                        : GrayImageCodec.FromScreenshot(confirmationScreenshot);
                    var confirmed = false;
                    if (confirmationScreen is not null)
                    {
                        var confirmationMatches = await FindStartupMonitorMatchesAsync(
                            confirmationScreen,
                            successCandidates,
                            cancellationToken).ConfigureAwait(false);
                        confirmed = successCandidates.Any(candidate =>
                            FindFoundMatch(confirmationMatches, candidate.Name) is not null);
                    }

                    if (confirmed)
                    {
                        AddLog(
                            logSink,
                            "StartupMonitor",
                            "Startup success task confirmed after the settle delay.",
                            LogEntryKind.Success);
                        return new PipelineTaskResult(true, "Startup success task confirmed.");
                    }

                    // The first Home match was transient. Reset any partially
                    // completed trigger chain, then keep the normal monitor
                    // loop running. Every fresh screenshot reruns the complete
                    // monitorTasks set in parallel until Home is confirmed.
                    chainIndex = -1;
                    AddLog(
                        logSink,
                        taskName,
                        "Home confirmation failed; continuing all startup monitor tasks until Home is confirmed.",
                        LogEntryKind.Failure);
                    continue;
                }

                if (chainIndex >= 0 && chainIndex < triggerChain.Length)
                {
                    var chainCandidate = triggerChain[chainIndex];
                    var chainMatch = FindFoundMatch(matches, chainCandidate.Name);
                    if (chainMatch is not null)
                    {
                        var actionResult = await RunMonitorActionAsync(
                            connection,
                            chainCandidate,
                            chainMatch.Value.Match,
                            screen,
                            logSink,
                            cancellationToken).ConfigureAwait(false);
                        if (!actionResult.Succeeded)
                            return actionResult;

                        chainIndex++;
                        if (chainIndex >= triggerChain.Length)
                        {
                            chainIndex = -1;
                            AddLog(
                                logSink,
                                taskName,
                                $"{monitorTask.TriggerTask ?? "Trigger"} follow-up chain completed.");
                        }

                        continue;
                    }

                    // Once the trigger has started the chain, wait for the
                    // current chain item instead of firing another startup action.
                }

                if (chainIndex < 0)
                {
                    // The trigger has priority over the other startup actions.
                    // This keeps the trigger -> chain sequence together when two
                    // templates happen to match one screenshot.
                    if (triggerCandidate is not null)
                    {
                        var triggerMatch = FindFoundMatch(matches, triggerCandidate.Name);
                        if (triggerMatch is not null)
                        {
                            var actionResult = await RunMonitorActionAsync(
                                connection,
                                triggerCandidate,
                                triggerMatch.Value.Match,
                                screen,
                                logSink,
                                cancellationToken).ConfigureAwait(false);
                            if (!actionResult.Succeeded)
                                return actionResult;

                            chainIndex = triggerChain.Length == 0 ? -1 : 0;
                            continue;
                        }
                    }

                    var independentNames = monitorTask.MonitorTasks
                        .Where(name => !successCandidateNames.Contains(name))
                        .Where(name => !string.Equals(name, monitorTask.TriggerTask, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    var independentMatch = independentNames
                        .Select(name => FindFoundMatch(matches, name))
                        .FirstOrDefault(match => match is not null);
                    if (independentMatch is not null)
                    {
                        var candidate = byName[independentMatch.Value.Name];
                        var actionResult = await RunMonitorActionAsync(
                            connection,
                            candidate,
                            independentMatch.Value.Match,
                            screen,
                            logSink,
                            cancellationToken).ConfigureAwait(false);
                        if (!actionResult.Succeeded)
                            return actionResult;

                        continue;
                    }
                }
            }

            if (Stopwatch.GetElapsedTime(started) >= timeout)
            {
                var matchSummary = lastMatches is null
                    ? "no decoded screenshot"
                    : string.Join(
                        ", ",
                        lastMatches.Select(pair =>
                        {
                            var threshold = byName.TryGetValue(pair.Key, out var candidate)
                                ? candidate.Task.TemplateThreshold.ToString("0.000", CultureInfo.InvariantCulture)
                                : "n/a";
                            return $"{pair.Key}=score {pair.Value.Score.ToString("0.000", CultureInfo.InvariantCulture)} / threshold {threshold}";
                        }));
                AddLog(
                    logSink,
                    taskName,
                    $"No startup match; screen={lastScreen?.Width}x{lastScreen?.Height}; {matchSummary}",
                    LogEntryKind.Failure);
                return new PipelineTaskResult(
                    false,
                    $"Timed out waiting for startup monitor '{taskName}'.");
            }

            await _asyncDelay.DelayAsync(poll, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<string, TemplateMatchResult>> FindStartupMonitorMatchesAsync(
        GrayImage screen,
        IReadOnlyList<StartupMonitorCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var matchTasks = candidates.Select(candidate => Task.Run(
            () => (candidate.Name, Match: TemplateMatcher.Find(
                screen,
                candidate.Template,
                candidate.Task.Roi,
                candidate.Task.TemplateThreshold,
                candidate.Task.ReferenceWidth,
                candidate.Task.ReferenceHeight)),
            cancellationToken));
        var results = await Task.WhenAll(matchTasks).ConfigureAwait(false);
        return results.ToDictionary(result => result.Name, result => result.Match, StringComparer.OrdinalIgnoreCase);
    }

    private static (string Name, TemplateMatchResult Match)? FindFoundMatch(
        Dictionary<string, TemplateMatchResult> matches,
        string name)
    {
        return matches.TryGetValue(name, out var match) && match.Found
            ? (name, match)
            : null;
    }

    private async Task<PipelineTaskResult> RunMonitorActionAsync(
        LastVerifiedConnection connection,
        StartupMonitorCandidate candidate,
        TemplateMatchResult match,
        GrayImage screen,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var task = candidate.Task;
        if (task.PreDelay > 0)
        {
            await _asyncDelay.DelayAsync(
                TimeSpan.FromMilliseconds(task.PreDelay),
                cancellationToken).ConfigureAwait(false);
        }

        var actionResult = await ExecuteActionAsync(
            connection,
            task,
            match,
            screen,
            cancellationToken).ConfigureAwait(false);
        if (actionResult is { Succeeded: false })
            return actionResult;

        if (task.PostDelay > 0)
        {
            await _asyncDelay.DelayAsync(
                TimeSpan.FromMilliseconds(task.PostDelay),
                cancellationToken).ConfigureAwait(false);
        }

        var score = $" score={match.Score.ToString("0.000", CultureInfo.InvariantCulture)}"
            + $" threshold={task.TemplateThreshold.ToString("0.000", CultureInfo.InvariantCulture)}";
        AddLog(
            logSink,
            candidate.Name,
            $"{actionResult?.Message ?? $"Action '{task.Action}' completed."}{score}",
            LogEntryKind.Success);
        return actionResult ?? new PipelineTaskResult(true, "Monitor action completed.");
    }

    private async Task<PipelineTaskResult?> ExecuteActionAsync(
        LastVerifiedConnection connection,
        StartGamePipelineTask task,
        TemplateMatchResult? match,
        GrayImage? screen,
        CancellationToken cancellationToken)
    {
        var action = task.Action.Trim();
        if (action.Equals("Screenshot", StringComparison.OrdinalIgnoreCase)
            || action.Equals("CaptureScreenshot", StringComparison.OrdinalIgnoreCase))
        {
            var screenshot = await CapturePipelineScreenshotAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
            if (screenshot is null)
                return new PipelineTaskResult(false, "ADB screenshot failed while running the pipeline.");

            var debugPath = Path.Combine(
                Path.GetDirectoryName(_definitionPath)!,
                "debug",
                "last_screenshot.png");
            await Task.Run(
                () => GrayImageCodec.SaveScreenshot(screenshot, debugPath),
                cancellationToken).ConfigureAwait(false);
            return new PipelineTaskResult(true, $"Screenshot saved to {debugPath}.");
        }

        if (action.Equals("DoNothing", StringComparison.OrdinalIgnoreCase)
            || action.Equals("Wait", StringComparison.OrdinalIgnoreCase))
        {
            var wait = task.WaitMilliseconds > 0
                ? task.WaitMilliseconds
                : action.Equals("Wait", StringComparison.OrdinalIgnoreCase)
                    ? task.PostDelay
                    : 0;
            if (wait > 0)
            {
                await _asyncDelay.DelayAsync(
                    TimeSpan.FromMilliseconds(wait),
                    cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        if (action.Equals("ClickSelf", StringComparison.OrdinalIgnoreCase))
        {
            if (match is not { Found: true })
                return new PipelineTaskResult(false, "ClickSelf requires a matched template.");

            var result = await _adbRuntime.TapAsync(
                connection.AdbPath,
                connection.Serial,
                match.CenterX,
                match.CenterY,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return ToActionResult(result, "ClickSelf");
        }

        if (action.Equals("ClickRect", StringComparison.OrdinalIgnoreCase))
        {
            var point = ResolvePoint(task, task.SpecificRect, screen, connection);
            if (point is null)
                return new PipelineTaskResult(false, "ClickRect requires specificRect.");

            var result = await _adbRuntime.TapAsync(
                connection.AdbPath,
                connection.Serial,
                point.Value.X,
                point.Value.Y,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return ToActionResult(result, "ClickRect");
        }

        if (action.Equals("TapToStart", StringComparison.OrdinalIgnoreCase))
        {
            var point = ResolvePoint(task, task.SpecificRect, screen, connection);
            if (point is null)
                return new PipelineTaskResult(false, "TapToStart requires specificRect.");

            var result = await _adbRuntime.TapAsync(
                connection.AdbPath,
                connection.Serial,
                point.Value.X,
                point.Value.Y,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return ToActionResult(result, "TapToStart");
        }

        if (action.Equals("Swipe", StringComparison.OrdinalIgnoreCase))
        {
            var start = ResolvePoint(task, task.SpecificRect, screen, connection);
            var end = ResolvePoint(task, task.RectMove, screen, connection);
            if (start is null || end is null)
                return new PipelineTaskResult(false, "Swipe requires specificRect and rectMove.");

            var result = await _adbRuntime.SwipeAsync(
                connection.AdbPath,
                connection.Serial,
                start.Value.X,
                start.Value.Y,
                end.Value.X,
                end.Value.Y,
                Math.Max(1, task.WaitMilliseconds),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return ToActionResult(result, "Swipe");
        }

        if (action.Equals("Back", StringComparison.OrdinalIgnoreCase))
        {
            var result = await _adbRuntime.BackAsync(
                connection.AdbPath,
                connection.Serial,
                cancellationToken).ConfigureAwait(false);
            return ToActionResult(result, "Back");
        }

        if (action.Equals("Input", StringComparison.OrdinalIgnoreCase))
        {
            var result = await _adbRuntime.InputTextAsync(
                connection.AdbPath,
                connection.Serial,
                task.InputText ?? string.Empty,
                cancellationToken).ConfigureAwait(false);
            return ToActionResult(result, "Input");
        }

        if (action.Equals("KeyEvent", StringComparison.OrdinalIgnoreCase))
        {
            var result = await _adbRuntime.KeyEventAsync(
                connection.AdbPath,
                connection.Serial,
                task.KeyCode ?? "4",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return ToActionResult(result, "KeyEvent");
        }

        return new PipelineTaskResult(false, $"Unsupported pipeline action '{task.Action}'.");
    }

    private async Task<StartGamePipelineDefinition?> LoadDefinitionAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_definitionPath))
            return null;

        try
        {
            await using var stream = File.OpenRead(_definitionPath);
            return await JsonSerializer.DeserializeAsync<StartGamePipelineDefinition>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task<GrayImage?> LoadTemplateAsync(
        string? templatePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(templatePath))
            return null;

        var fullPath = Path.IsPathRooted(templatePath)
            ? templatePath
            : Path.Combine(Path.GetDirectoryName(_definitionPath)!, templatePath);
        return await Task.Run(
            () => GrayImageCodec.FromFile(fullPath),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdbScreenshotResult?> CapturePipelineScreenshotAsync(
        LastVerifiedConnection connection,
        CancellationToken cancellationToken)
    {
        var raw = await _adbRuntime.DecodeRawScreenshotAsync(
            connection.AdbPath,
            connection.Serial,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return raw.Value is { } decoded
            ? new AdbScreenshotResult(
                AdbScreenshotMethod.Raw,
                [],
                TimeSpan.Zero,
                decoded)
            : null;
    }

    private static bool IsTemplateTask(StartGamePipelineTask task) =>
        !string.IsNullOrWhiteSpace(task.Template)
        && !task.Algorithm.Equals("JustReturn", StringComparison.OrdinalIgnoreCase);

    private static bool IsStartupMonitorTask(StartGamePipelineTask task) =>
        task.MonitorTasks.Count > 0
        || task.Action.Equals("StartupMonitor", StringComparison.OrdinalIgnoreCase);

    private static string ResolveStartTask(StartGamePipelineDefinition definition) =>
        !string.IsNullOrWhiteSpace(definition.Start)
            && definition.Tasks.ContainsKey(definition.Start)
            ? definition.Start
            : definition.Tasks.Keys.FirstOrDefault() ?? string.Empty;

    private static (int X, int Y)? ResolvePoint(
        StartGamePipelineTask task,
        int[]? rect,
        GrayImage? screen,
        LastVerifiedConnection connection)
    {
        if (rect is not { Length: >= 2 })
            return null;

        var width = screen?.Width ?? connection.Width;
        var height = screen?.Height ?? connection.Height;
        var x = Scale(rect[0], width, task.ReferenceWidth);
        var y = Scale(rect[1], height, task.ReferenceHeight);
        if (rect.Length >= 4)
        {
            x += Scale(rect[2], width, task.ReferenceWidth) / 2;
            y += Scale(rect[3], height, task.ReferenceHeight) / 2;
        }

        return (x, y);
    }

    private static int Scale(int value, int actual, int reference) =>
        (int)Math.Round(value * (double)Math.Max(1, actual) / Math.Max(1, reference));

    private static PipelineTaskResult ToActionResult(
        AdbCommandResult result,
        string action) =>
        result.Error is null && !result.TimedOut && result.ExitCode == 0
            ? new PipelineTaskResult(true, $"{action} completed.")
            : new PipelineTaskResult(false, $"{action} failed: {result.Stderr}");

    private static StartGamePipelineResult Fail(
        IGrassTaskLogSink? logSink,
        string message)
    {
        AddLog(logSink, "Start game pipeline", message, LogEntryKind.Failure);
        return new StartGamePipelineResult(false, false, message);
    }

    private static void AddLog(
        IGrassTaskLogSink? logSink,
        string type,
        string details,
        LogEntryKind kind = LogEntryKind.Info) =>
        logSink?.Add(type, details, kind);

    private sealed record PipelineTaskResult(
        bool Succeeded,
        string Message,
        bool Fatal = false);

    private sealed record StartupMonitorCandidate(
        string Name,
        StartGamePipelineTask Task,
        GrayImage Template);
}
