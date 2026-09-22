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

        definitionPath = ResourcePathRuntime.Resolve(definitionPath);

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

            var taskCount = state.GetTaskCount(current, task);
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

            state.IncrementTaskCount(current, task);
            var effectiveSearchRois = ResolveSearchRois(current, task, state.Options);
            var hasSemanticStep = HachimiTaskLogSemantics.TryDescribe(
                state.Options.SemanticProfile,
                current,
                out var semanticStep);
            if (hasSemanticStep)
            {
                AddSemanticLog(
                    state.Options.TaskLogSink,
                    "Action",
                    semanticStep.StartMessage,
                    HachimiTaskLogEventKind.Action);
            }
            AddTaskLog(
                logSink,
                current,
                $"Run #{taskCount + 1}: algorithm={task.Algorithm}, action={task.Action}, "
                + $"template={task.Template ?? "none"}, roi={FormatArray(task.Roi)}, "
                + $"searchRois={effectiveSearchRois.Count}, minScoreGap={task.MinimumScoreGap:0.000}, "
                + $"threshold={task.TemplateThreshold:0.000}, "
                + $"timeout={ResolveTaskTimeoutMilliseconds(current, task, state.Options)}ms, "
                + $"preDelay={task.PreDelay}ms, wait={task.WaitMilliseconds}ms, postDelay={task.PostDelay}ms.");

            var retryLimit = ResolveRetryLimit(current, task, state.Options);
            var retryAttempt = 0;
            TaskExecutionResult execution;
            while (true)
            {
                try
                {
                    execution = await ExecuteTaskAsync(
                            connection,
                            definition,
                            current,
                            task,
                            state.Options,
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // A broken screenshot/OCR/template adapter must be
                    // treated as a task failure so JSON recovery branches can
                    // run.  Letting it escape here aborts the whole Career
                    // area before onErrorNext can be considered.
                    var detail = string.IsNullOrWhiteSpace(exception.Message)
                        ? exception.GetType().Name
                        : exception.Message;
                    AddLog(
                        logSink,
                        $"Task '{current}' threw {exception.GetType().Name}: {detail}",
                        LogEntryKind.Failure);
                    execution = TaskExecutionResult.Failed(
                        $"JSON task '{current}' failed unexpectedly: {detail}");
                }

                if (execution.Succeeded
                    || !execution.Retryable
                    || retryAttempt >= retryLimit)
                {
                    break;
                }

                retryAttempt++;
                AddLog(
                    logSink,
                    $"Task '{current}' had a retry-safe visual miss; retrying "
                    + $"({retryAttempt}/{retryLimit}) after "
                    + $"{ResolveRetryDelayMilliseconds(task, state.Options)}ms.",
                    LogEntryKind.Info);
                await _visualRuntime.DelayAsync(
                        ResolveRetryDelayMilliseconds(task, state.Options),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            if (!execution.Succeeded)
            {
                if (hasSemanticStep)
                {
                    if (!task.Required && semanticStep.SkippedMessage is not null)
                    {
                        AddSemanticLog(
                            state.Options.TaskLogSink,
                            "Branch",
                            semanticStep.SkippedMessage,
                            HachimiTaskLogEventKind.Info);
                    }
                    else
                    {
                        AddSemanticLog(
                            state.Options.TaskLogSink,
                            "Result",
                            semanticStep.FailedMessage,
                            HachimiTaskLogEventKind.Failure);
                    }
                }
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

            if (hasSemanticStep)
            {
                AddSemanticLog(
                    state.Options.TaskLogSink,
                    "Result",
                    semanticStep.CompletedMessage,
                    HachimiTaskLogEventKind.Success);
            }

            if (task.Success)
                return Succeed(state, current);

            current = execution.TransitionTask is { Length: > 0 } transitionTask
                && definition.TryGetTask(transitionTask, out _)
                ? transitionTask
                : FirstExisting(definition, task.Next)
                    ?? string.Empty;
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
                    new RunState(new HachimiPipelineRunOptions
                    {
                        TaskLogSink = runOptions.TaskLogSink,
                    }),
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!subResult.Succeeded)
                return TaskExecutionResult.Failed(subResult.Message);
        }

        TemplateMatchResult? match = null;
        ScreenTextQueryResult? textMatch = null;
        var action = Normalize(task.Action);
        var algorithm = Normalize(task.Algorithm);
        var effectiveTimeoutMilliseconds = ResolveTaskTimeoutMilliseconds(
            taskName,
            task,
            runOptions);

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

        var isOcrTask = algorithm is "ocrtext" or "ocr" or "screentext";
        if (isOcrTask)
        {
            var targetText = ResolveTargetText(taskName, task, runOptions);
            var roi = task.Roi;
            if (runOptions.RoiOverrides is not null
                && runOptions.RoiOverrides.TryGetValue(taskName, out var roiOverride))
            {
                roi = roiOverride;
            }

            try
            {
                if (action is "detecttext" or "detect")
                {
                    var detected = await _visualRuntime.DetectTextAsync(
                            connection,
                            roi,
                            definition.ReferenceWidth,
                            definition.ReferenceHeight,
                            task.OcrLanguage,
                            taskName,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (detected is null)
                    {
                        return TaskExecutionResult.RetryableFailure(
                            $"OCR screenshot could not be captured for '{taskName}'.");
                    }

                    AddTaskLog(
                        logSink,
                        taskName,
                        $"OCR language={detected.Language}, roi={FormatArray(roi)}, "
                        + $"recognized={FormatDetections(detected.Detections)}",
                        LogEntryKind.Success);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(targetText))
                    {
                        return TaskExecutionResult.Failed(
                            $"OCR task '{taskName}' requires targetText or a runtime target override.");
                    }

                    var pollInterval = task.PollIntervalMilliseconds > 0
                        ? task.PollIntervalMilliseconds
                        : definition.Timing.PollIntervalMilliseconds;
                    textMatch = await WaitForTextWithScrollAsync(
                            connection,
                            definition,
                            taskName,
                            task,
                            targetText,
                            roi,
                            effectiveTimeoutMilliseconds,
                            pollInterval,
                            runOptions,
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (textMatch is null)
                    {
                        return TaskExecutionResult.RetryableFailure(
                            $"OCR screenshot could not be captured for '{taskName}'.");
                    }

                    AddTaskLog(
                        logSink,
                        taskName,
                        $"OCR target='{targetText}', roi={FormatArray(roi)}, "
                        + $"matchMode={task.OcrMatchMode ?? "line"}, "
                        + $"recognized={textMatch.RecognizedSummary}",
                        textMatch.Found ? LogEntryKind.Success : LogEntryKind.Info);
                    if (textMatch.Match is { } ocrMatch
                        && task.OcrMatchMode?.Equals(
                            "tokenCoverage",
                            StringComparison.OrdinalIgnoreCase) == true)
                    {
                        AddTaskLog(
                            logSink,
                            taskName,
                            $"OCR grouped match='{ocrMatch.Text}' at {ocrMatch.Bounds}, "
                            + $"tokenScore={ocrMatch.Similarity:0.000}, "
                            + $"confidence={ocrMatch.Confidence:0.000}.",
                            LogEntryKind.Success);
                    }
                    if (!textMatch.Found)
                    {
                        return TaskExecutionResult.RetryableFailure(
                            textMatch.Ambiguous
                                ? textMatch.Error
                                    ?? $"OCR target '{targetText}' was ambiguous."
                                : $"OCR target '{targetText}' was not found before timeout.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return TaskExecutionResult.RetryableFailure(
                    $"OCR task '{taskName}' failed: {exception.Message}");
            }
        }

        var templatePath = task.Template;
        if (runOptions.TemplateOverrides is not null
            && runOptions.TemplateOverrides.TryGetValue(taskName, out var templateOverride)
            && !string.IsNullOrWhiteSpace(templateOverride))
        {
            templatePath = templateOverride;
        }

        if (!string.IsNullOrWhiteSpace(templatePath))
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
                $"Waiting for template '{templatePath}' in ROI {FormatArray(roi)} "
                + $"(threshold {task.TemplateThreshold:0.000}, timeout {effectiveTimeoutMilliseconds}ms, "
                + $"poll {pollInterval}ms).");

            var scaleCandidates = task.ScaleCandidates
                .Where(double.IsFinite)
                .Where(candidate => candidate > 0)
                .ToArray();
            var useScaledTemplate = algorithm is "matchtemplatescaled";
            if (useScaledTemplate && scaleCandidates.Length == 0)
            {
                scaleCandidates = [0.80d, 0.85d, 0.90d, 0.95d, 1.00d, 1.05d, 1.10d];
            }

            try
            {
                match = await WaitForTemplateWithScrollAsync(
                        connection,
                        definition,
                        taskName,
                        task,
                        templatePath,
                        roi,
                        effectiveTimeoutMilliseconds,
                        useScaledTemplate,
                        scaleCandidates,
                        pollInterval,
                        runOptions,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return TaskExecutionResult.RetryableFailure(
                    $"Visual task '{taskName}' failed: {exception.Message}");
            }

            if (match is null || !match.Found)
            {
                var bestScore = match is null
                    ? "none"
                    : match.Score.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
                return TaskExecutionResult.RetryableFailure(
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
            case "clicktext":
                if (textMatch?.Match is not { } textCandidate)
                {
                    return TaskExecutionResult.Failed(
                        $"JSON task '{taskName}' uses ClickText but has no OCR match.");
                }

                if (task.ClickAnchor is { Length: >= 1 })
                {
                    // TapAsync accepts reference-space coordinates and does
                    // the single conversion to the actual ADB surface.  Do
                    // not pass already-scaled values here (that would scale
                    // twice on non-900x1600 devices).  A missing Y anchor is
                    // derived from the OCR rectangle's actual pixel center,
                    // then converted back to reference space once.
                    var anchorXReference = task.ClickAnchor[0];
                    var anchorYReference = task.ClickAnchor.Length >= 2
                        ? task.ClickAnchor[1]
                        : (int)Math.Round(
                            textCandidate.Bounds.CenterY
                            * (double)Math.Max(1, definition.ReferenceHeight)
                            / Math.Max(1, connection.Height));
                    var anchorXActual = ScaleReferenceCoordinate(
                        anchorXReference,
                        definition.ReferenceWidth,
                        connection.Width);
                    var anchorYActual = ScaleReferenceCoordinate(
                        anchorYReference,
                        definition.ReferenceHeight,
                        connection.Height);
                    var anchoredClickResult = await ExecuteAnchoredOcrClickAsync(
                            connection,
                            definition,
                            taskName,
                            task,
                            textCandidate,
                            anchorXReference,
                            anchorYReference,
                            anchorXActual,
                            anchorYActual,
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!anchoredClickResult.Succeeded)
                        return anchoredClickResult;
                }
                else
                {
                    await _visualRuntime.TapTextAsync(
                            connection,
                            textCandidate,
                            task.ClickOffset,
                            task.RowExpansion,
                            taskName,
                            cancellationToken)
                        .ConfigureAwait(false);
                    AddTaskLog(
                        logSink,
                        taskName,
                        $"Clicked OCR text '{textCandidate.Text}' at {textCandidate.Bounds}, "
                        + $"similarity={textCandidate.Similarity:0.000}, "
                        + $"confidence={textCandidate.Confidence:0.000}.",
                        LogEntryKind.Success);
                }
                break;

            case "findtext":
            case "waitfortext":
            case "scrollfindtext":
                if (textMatch?.Match is null)
                {
                    return TaskExecutionResult.Failed(
                        $"JSON task '{taskName}' completed without an OCR match.");
                }

                AddTaskLog(
                    logSink,
                    taskName,
                    $"OCR text verified at {textMatch.Match.Bounds}, "
                    + $"similarity={textMatch.Match.Similarity:0.000}.",
                    LogEntryKind.Success);
                break;

            case "detecttext":
            case "detect":
                // Detection and structured OCR logging were completed before
                // dispatch.  There is intentionally no tap for this action.
                break;

            case "input":
                var inputText = task.InputText;
                if (runOptions.InputTextOverrides is not null
                    && runOptions.InputTextOverrides.TryGetValue(taskName, out var inputOverride))
                {
                    inputText = inputOverride;
                }

                if (string.IsNullOrWhiteSpace(inputText))
                {
                    return TaskExecutionResult.Failed(
                        $"JSON task '{taskName}' uses Input but has no text value.");
                }

                var inputResult = await _adbRuntime.InputTextAsync(
                        connection.AdbPath,
                        connection.Serial,
                        inputText,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (inputResult.Error is not null
                    || inputResult.TimedOut
                    || inputResult.ExitCode != 0)
                {
                    return TaskExecutionResult.Failed(
                        $"ADB Input failed for '{taskName}': {inputResult.Stderr}");
                }

                AddTaskLog(
                    logSink,
                    taskName,
                    $"Entered JSON-provided text ({inputText.Length} character(s)).",
                    LogEntryKind.Success);
                break;

            case "keyevent":
                if (string.IsNullOrWhiteSpace(task.KeyCode))
                {
                    return TaskExecutionResult.Failed(
                        $"JSON task '{taskName}' uses KeyEvent but has no keyCode.");
                }

                var keyEventResult = await _adbRuntime.KeyEventAsync(
                        connection.AdbPath,
                        connection.Serial,
                        task.KeyCode,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (keyEventResult.Error is not null
                    || keyEventResult.TimedOut
                    || keyEventResult.ExitCode != 0)
                {
                    return TaskExecutionResult.Failed(
                        $"ADB KeyEvent failed for '{taskName}': {keyEventResult.Stderr}");
                }

                AddTaskLog(
                    logSink,
                    taskName,
                    $"Sent JSON key event '{task.KeyCode}'.",
                    LogEntryKind.Success);
                break;

            case "selectdailyracerunner":
            case "selecturatrainee":
            case "selecturalegacy":
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

            case "clickselfuntiltransition":
                if (match is null)
                {
                    return TaskExecutionResult.Failed(
                        $"JSON task '{taskName}' uses ClickSelfUntilTransition but has no template match.");
                }

                if (task.TransitionTemplates.Count == 0)
                {
                    return TaskExecutionResult.Failed(
                        $"JSON task '{taskName}' requires transitionTemplates.");
                }

                if (task.ClickUntilGone)
                {
                    var goneResult = await ClickUntilTemplateGoneAsync(
                            connection,
                            definition,
                            taskName,
                            task,
                            templatePath!,
                            match,
                            runOptions,
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!goneResult)
                    {
                        return TaskExecutionResult.Failed(
                            $"Task '{taskName}' kept finding its current template after "
                            + $"{task.MaxClickAttempts} click attempt(s).");
                    }

                    var goneTransition = await WaitForAnyTransitionTemplateAsync(
                            connection,
                            definition,
                            task,
                            taskName,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (goneTransition is null)
                    {
                        return TaskExecutionResult.Failed(
                            $"Task '{taskName}' disappeared, but its transition state was not detected.");
                    }

                    AddTaskLog(
                        logSink,
                        taskName,
                        $"Current template disappeared; transition verified with "
                        + $"'{goneTransition.Value.TemplatePath}' "
                        + $"(score {goneTransition.Value.Match.Score:0.000}).",
                        LogEntryKind.Success);
                    break;
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
                    $"Clicked '{taskName}' at ({match.CenterX},{match.CenterY}) for the first attempt.",
                    LogEntryKind.Success);

                var transition = await WaitForAnyTransitionTemplateAsync(
                        connection,
                        definition,
                        task,
                        taskName,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (transition is null)
                {
                    if (task.FallbackRoi is not { Length: >= 4 })
                    {
                        return TaskExecutionResult.Failed(
                            $"Task '{taskName}' did not reach the next screen after the first click, "
                            + "and no fallbackRoi was configured for a second attempt.");
                    }

                    var fallbackMatch = await WaitForTemplateAttemptAsync(
                            connection,
                            definition,
                            taskName,
                            task,
                            templatePath!,
                            task.FallbackRoi,
                            useScaledTemplate: false,
                            scaleCandidates: Array.Empty<double>(),
                            ResolveSearchRois(taskName, task, runOptions),
                            Math.Clamp(
                                task.TransitionTimeoutMilliseconds,
                                250,
                                10 * 60 * 1000),
                            Math.Max(
                                50,
                                task.TransitionPollIntervalMilliseconds),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (fallbackMatch is null || !fallbackMatch.Found)
                    {
                        return TaskExecutionResult.Failed(
                            $"Task '{taskName}' stayed on the selection screen after the first click, "
                            + "but its second-click state was not detected.");
                    }

                    await _visualRuntime.TapMatchAsync(
                            connection,
                            fallbackMatch,
                            taskName,
                            cancellationToken)
                        .ConfigureAwait(false);
                    AddTaskLog(
                        logSink,
                        taskName,
                        $"The first click did not enter the next screen; clicked the verified fallback state "
                        + $"at ({fallbackMatch.CenterX},{fallbackMatch.CenterY}) for the second attempt.",
                        LogEntryKind.Info);

                    transition = await WaitForAnyTransitionTemplateAsync(
                            connection,
                            definition,
                            task,
                            taskName,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (transition is null)
                    {
                        return TaskExecutionResult.Failed(
                            $"Task '{taskName}' did not reach the next screen after the second click.");
                    }
                }

                AddTaskLog(
                    logSink,
                    taskName,
                    $"Training transition verified with '{transition.Value.TemplatePath}' "
                    + $"(score {transition.Value.Match.Score:0.000}); no extra click was issued.",
                    LogEntryKind.Success);
                break;

            case "justreturn":
                // A template can be used as a read-only state probe.  The
                // support-card picker uses this to detect an already selected
                // slot without issuing a tap.
                if (match is not null)
                {
                    AddTaskLog(
                        logSink,
                        taskName,
                        $"Detected '{taskName}' without clicking (score {match.Score:0.000}).",
                        LogEntryKind.Success);
                }
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

    private async Task<TaskExecutionResult> ExecuteAnchoredOcrClickAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        string taskName,
        HachimiPipelineTask task,
        ScreenTextCandidate textCandidate,
        int anchorXReference,
        int anchorYReference,
        int anchorXActual,
        int anchorYActual,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var verification = task.ClickVerification;
        if (verification is null)
        {
            await _visualRuntime.TapAsync(
                    connection,
                    anchorXReference,
                    anchorYReference,
                    definition.ReferenceWidth,
                    definition.ReferenceHeight,
                    taskName,
                    cancellationToken)
                .ConfigureAwait(false);
            AddTaskLog(
                logSink,
                taskName,
                $"Clicked OCR row anchor ref=({anchorXReference},{anchorYReference}) "
                + $"actual=({anchorXActual},{anchorYActual}) for '{textCandidate.Text}' "
                + $"at {textCandidate.Bounds}, "
                + $"similarity={textCandidate.Similarity:0.000}, "
                + $"confidence={textCandidate.Confidence:0.000}.",
                LogEntryKind.Success);
            return TaskExecutionResult.Completed(taskName);
        }

        var attempts = 1 + Math.Clamp(verification.MaxRetries, 0, 5);
        HsvColorProbeResult? latestProbe = null;
        if (verification.PreCheck)
        {
            latestProbe = await ProbeClickStateAsync(
                    connection,
                    definition,
                    taskName,
                    anchorXReference,
                    anchorYReference,
                    verification,
                    timeoutMilliseconds: 0,
                    cancellationToken)
                .ConfigureAwait(false);
            AddTaskLog(
                logSink,
                taskName,
                $"Click verification precheck ref=({anchorXReference},{anchorYReference}) "
                + $"actual=({anchorXActual},{anchorYActual}) "
                + FormatProbe(latestProbe) + ".",
                latestProbe?.Matched == true
                    ? LogEntryKind.Success
                    : LogEntryKind.Info);
            if (latestProbe?.Matched == true)
            {
                AddTaskLog(
                    logSink,
                    taskName,
                    $"OCR row already selected at ref=({anchorXReference},{anchorYReference}); "
                    + "skipped tap to avoid toggling it off.",
                    LogEntryKind.Success);
                return TaskExecutionResult.Completed(taskName);
            }
        }

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1)
            {
                await _visualRuntime.DelayAsync(
                        Math.Clamp(verification.RetryDelayMilliseconds, 0, 10_000),
                        cancellationToken)
                    .ConfigureAwait(false);
                latestProbe = await ProbeClickStateAsync(
                        connection,
                        definition,
                        taskName,
                        anchorXReference,
                        anchorYReference,
                        verification,
                        timeoutMilliseconds: 0,
                        cancellationToken)
                    .ConfigureAwait(false);
                AddTaskLog(
                    logSink,
                    taskName,
                    $"Click verification retry precheck attempt={attempt} "
                    + $"ref=({anchorXReference},{anchorYReference}) "
                    + $"actual=({anchorXActual},{anchorYActual}) "
                    + FormatProbe(latestProbe) + ".",
                    latestProbe?.Matched == true
                        ? LogEntryKind.Success
                        : LogEntryKind.Info);
                if (latestProbe?.Matched == true)
                {
                    AddTaskLog(
                        logSink,
                        taskName,
                        $"OCR row became selected before retry at "
                        + $"ref=({anchorXReference},{anchorYReference}); skipped tap.",
                        LogEntryKind.Success);
                    return TaskExecutionResult.Completed(taskName);
                }
            }

            await _visualRuntime.TapAsync(
                    connection,
                    anchorXReference,
                    anchorYReference,
                    definition.ReferenceWidth,
                    definition.ReferenceHeight,
                    taskName,
                    cancellationToken)
                .ConfigureAwait(false);
            AddTaskLog(
                logSink,
                taskName,
                $"Clicked OCR row anchor attempt={attempt}/{attempts} "
                + $"ref=({anchorXReference},{anchorYReference}) "
                + $"actual=({anchorXActual},{anchorYActual}) for '{textCandidate.Text}' "
                + $"at {textCandidate.Bounds}, "
                + $"similarity={textCandidate.Similarity:0.000}, "
                + $"confidence={textCandidate.Confidence:0.000}.",
                LogEntryKind.Info);

            if (verification.SettleDelayMilliseconds > 0)
            {
                await _visualRuntime.DelayAsync(
                        Math.Clamp(verification.SettleDelayMilliseconds, 0, 10_000),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            latestProbe = await ProbeClickStateAsync(
                    connection,
                    definition,
                    taskName,
                    anchorXReference,
                    anchorYReference,
                    verification,
                    verification.VerifyTimeoutMilliseconds,
                    cancellationToken)
                .ConfigureAwait(false);
            AddTaskLog(
                logSink,
                taskName,
                $"Click verification postcheck attempt={attempt}/{attempts} "
                + $"ref=({anchorXReference},{anchorYReference}) "
                + $"actual=({anchorXActual},{anchorYActual}) "
                + FormatProbe(latestProbe) + ".",
                latestProbe?.Matched == true
                    ? LogEntryKind.Success
                    : LogEntryKind.Info);
            if (latestProbe?.Matched == true)
            {
                return TaskExecutionResult.Completed(taskName);
            }
        }

        return TaskExecutionResult.Failed(
            $"OCR row click state was not verified for '{textCandidate.Text}' "
            + $"after {attempts} attempt(s) at ref=({anchorXReference},{anchorYReference}); "
            + FormatProbe(latestProbe) + ".");
    }

    private Task<HsvColorProbeResult?> ProbeClickStateAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        string taskName,
        int anchorXReference,
        int anchorYReference,
        HachimiClickVerification verification,
        int timeoutMilliseconds,
        CancellationToken cancellationToken) =>
        _visualRuntime.ProbeHsvAsync(
            connection,
            anchorXReference,
            anchorYReference,
            verification.ProbeOffset,
            verification.ProbeRadius,
            definition.ReferenceWidth,
            definition.ReferenceHeight,
            verification.HueMin,
            verification.HueMax,
            verification.SaturationMin,
            verification.SaturationMax,
            verification.ValueMin,
            verification.ValueMax,
            verification.MinimumMatchRatio,
            timeoutMilliseconds,
            verification.VerifyPollIntervalMilliseconds,
            taskName,
            cancellationToken);

    private static string FormatProbe(HsvColorProbeResult? probe) =>
        probe is null
            ? "probe=unavailable"
            : $"probeRatio={probe.MatchRatio:0.000} "
                + $"pixels={probe.MatchingPixels}/{probe.SampledPixels} "
                + $"matched={probe.Matched} "
                + $"probeActual=({probe.CenterX},{probe.CenterY}) "
                + $"radius={probe.Radius}";

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
        var successNames = new[] { monitorTask.SuccessTask }
            .Concat(monitorTask.SuccessTasks)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (successNames.Length == 0)
        {
            return TaskExecutionResult.Failed(
                $"Parallel monitor '{taskName}' has no successTask or successTasks.");
        }

        var candidateNames = monitorNames
            .Concat(successNames)
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
        var successCandidates = successNames
            .Where(byName.ContainsKey)
            .Select(name => byName[name])
            .ToArray();
        if (monitorCandidates.Length == 0)
        {
            return TaskExecutionResult.Failed(
                $"Parallel monitor '{taskName}' has no monitorTasks.");
        }

        if (successCandidates.Length == 0)
        {
            return TaskExecutionResult.Failed(
                $"Parallel monitor '{taskName}' references no defined success task.");
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
            + $"success=[{string.Join(", ", successCandidates.Select(candidate => candidate.Name))}], "
            + $"timeout={timeout.TotalMilliseconds:0}ms, "
            + $"poll={poll.TotalMilliseconds:0}ms.");
        var started = Stopwatch.GetTimestamp();
        Dictionary<string, TemplateMatchResult>? lastMatches = null;
        GrayImage? lastScreen = null;
        var successMatchStarted = new Dictionary<string, long>(
            StringComparer.OrdinalIgnoreCase);

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

                // A matched stop condition always suppresses stale button
                // actions in the same screenshot. Some end markers are only
                // transitional (Replay can flash before Trophy Won), so they
                // may require a settling window before ending the monitor.
                ParallelMonitorCandidate? successCandidate = null;
                var hasPendingSuccessMatch = false;
                foreach (var candidate in successCandidates)
                {
                    if (!matches.TryGetValue(candidate.Name, out var successMatch)
                        || !successMatch.Found)
                    {
                        successMatchStarted.Remove(candidate.Name);
                        continue;
                    }

                    hasPendingSuccessMatch = true;
                    var requiredStableMilliseconds = Math.Max(
                        0,
                        candidate.Task.MonitorStableMilliseconds);
                    if (requiredStableMilliseconds == 0)
                    {
                        successCandidate = candidate;
                        break;
                    }

                    if (!successMatchStarted.TryGetValue(candidate.Name, out var matchedAt))
                    {
                        successMatchStarted[candidate.Name] = Stopwatch.GetTimestamp();
                        continue;
                    }

                    if (Stopwatch.GetElapsedTime(matchedAt)
                        >= TimeSpan.FromMilliseconds(requiredStableMilliseconds))
                    {
                        successCandidate = candidate;
                        break;
                    }
                }

                if (successCandidate is not null)
                {
                    AddTaskLog(
                        logSink,
                        taskName,
                        $"{taskName}: success task '{successCandidate.Name}' detected; leaving parallel monitor.",
                        LogEntryKind.Success);
                    return TaskExecutionResult.Completed(
                        taskName,
                        transitionTask: successCandidate.Name);
                }

                if (!hasPendingSuccessMatch)
                {
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

                        // Execute at most one action from a screenshot, then
                        // take a fresh screenshot after the UI has settled.
                        break;
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
            () =>
            {
                var useColor = Normalize(candidate.Task.Algorithm) is
                    "matchtemplatecolor" or "matchtemplatecol";
                var match = useColor
                    ? TemplateMatcher.FindColor(
                        screen,
                        candidate.Template,
                        candidate.Task.Roi,
                        candidate.Task.TemplateThreshold,
                        definition.ReferenceWidth,
                        definition.ReferenceHeight)
                    : TemplateMatcher.Find(
                        screen,
                        candidate.Template,
                        candidate.Task.Roi,
                        candidate.Task.TemplateThreshold,
                        definition.ReferenceWidth,
                        definition.ReferenceHeight);
                return (candidate.Name, Match: match);
            },
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
            TaskLogSink = parentOptions.TaskLogSink,
            SemanticProfile = isShopPipeline
                ? HachimiTaskLogProfile.Shop
                : parentOptions.SemanticProfile,
            MaxTimesOverrides = isShopPipeline
                ? CreateShopOverrides(_settingsService.Load().Hachimi.Shop)
                : null,
        };

        AddLog(
            logSink,
            $"Calling nested JSON pipeline '{nestedPath}' from '{taskName}'.");
        if (isShopPipeline)
        {
            AddSemanticLog(
                parentOptions.TaskLogSink,
                "Branch",
                "Opening the optional shop flow.",
                HachimiTaskLogEventKind.Branch);
        }
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
        if (isShopPipeline)
        {
            AddSemanticLog(
                parentOptions.TaskLogSink,
                "Branch",
                "Optional shop flow completed.",
                HachimiTaskLogEventKind.Success);
        }
        return TaskExecutionResult.Completed(taskName);
    }

    private static void AddSemanticLog(
        IHachimiTaskLogSink? logSink,
        string step,
        string message,
        HachimiTaskLogEventKind kind) =>
        logSink?.Add(step, message, kind);

    private static bool IsShopPipeline(string path) =>
        string.Equals(
            Path.GetFileName(path),
            "shop.json",
            StringComparison.OrdinalIgnoreCase);

    private static int ResolveRetryLimit(
        string taskName,
        HachimiPipelineTask task,
        HachimiPipelineRunOptions options)
    {
        var configured = Math.Max(0, task.RetryTimes);
        if (configured == 0
            && options.DefaultTaskRetryTimes > 0)
        {
            configured = options.DefaultTaskRetryTimes;
        }

        // Career recognition is especially sensitive to one slow emulator
        // frame. Give visual misses a small, bounded retry budget even for
        // older JSON packs that predate retryTimes. This applies only to the
        // retry-safe failure returned by the recognition phase.
        if (configured == 0
            && options.SemanticProfile == HachimiTaskLogProfile.Career
            && IsCareerTask(taskName))
        {
            configured = 2;
        }

        return Math.Clamp(configured, 0, 5);
    }

    private static int ResolveRetryDelayMilliseconds(
        HachimiPipelineTask task,
        HachimiPipelineRunOptions options)
    {
        var delay = task.RetryDelayMilliseconds > 0
            ? task.RetryDelayMilliseconds
            : options.DefaultTaskRetryDelayMilliseconds;
        return Math.Clamp(delay, 50, 10_000);
    }

    private static int ResolveTaskTimeoutMilliseconds(
        string taskName,
        HachimiPipelineTask task,
        HachimiPipelineRunOptions options)
    {
        var timeout = Math.Max(0, task.TimeoutMilliseconds);
        if (options.SemanticProfile != HachimiTaskLogProfile.Career
            || !IsCareerTask(taskName))
        {
            return timeout;
        }

        // Home and Career Main are state gates. A short timeout here turns a
        // single slow screenshot into a full-area failure, so keep a bounded
        // floor for legacy definitions while preserving longer task values.
        var floor = taskName.Equals("home", StringComparison.OrdinalIgnoreCase)
            || taskName.Equals("homeAlt", StringComparison.OrdinalIgnoreCase)
            ? 10_000
            : 15_000;
        return Math.Max(timeout, floor);
    }

    private static bool IsCareerTask(string taskName) =>
        taskName.Equals("home", StringComparison.OrdinalIgnoreCase)
        || taskName.Equals("homeAlt", StringComparison.OrdinalIgnoreCase)
        || taskName.StartsWith("home_home_career", StringComparison.OrdinalIgnoreCase)
        || taskName.StartsWith("career_main", StringComparison.OrdinalIgnoreCase);

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

        if (task.CountKey is { Length: > 0 } countKey
            && options.MaxTimesOverrides is not null
            && options.MaxTimesOverrides.TryGetValue(countKey, out overrideLimit))
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

    private async Task<TemplateMatchResult?> WaitForTemplateWithScrollAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        string taskName,
        HachimiPipelineTask task,
        string templatePath,
        int[]? roi,
        int timeoutMilliseconds,
        bool useScaledTemplate,
        IReadOnlyList<double> scaleCandidates,
        int pollInterval,
        HachimiPipelineRunOptions runOptions,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(
            timeoutMilliseconds,
            0,
            10 * 60 * 1000));
        var started = Stopwatch.GetTimestamp();
        var scrolls = 0;
        var maxScrolls = Math.Clamp(task.MaxScrolls, 0, 100);
        var canScroll = task.Swipe is { Length: >= 5 } && maxScrolls > 0;
        var searchRois = ResolveSearchRois(taskName, task, runOptions);
        TemplateMatchResult? latest = null;

        while (true)
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            var remainingMilliseconds = (int)Math.Clamp(
                (timeout - elapsed).TotalMilliseconds,
                0,
                10 * 60 * 1000);
            if (remainingMilliseconds <= 0)
                return latest;

            // A scrolling template search needs short attempts so it can
            // advance to the next page before the overall task timeout ends.
            // Once all allowed pages have been visited, let the runtime use
            // the remaining timeout on the current page.
            var attemptTimeout = canScroll && scrolls < maxScrolls
                ? Math.Min(
                    remainingMilliseconds,
                    Math.Clamp(pollInterval * 2, 250, 2_000))
                : remainingMilliseconds;
            latest = await WaitForTemplateAttemptAsync(
                    connection,
                    definition,
                    taskName,
                    task,
                    templatePath,
                    roi,
                    useScaledTemplate,
                    scaleCandidates,
                    searchRois,
                    attemptTimeout,
                    pollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
            if (latest?.Found == true)
                return latest;

            if (Stopwatch.GetElapsedTime(started) >= timeout)
                return latest;

            if (canScroll && scrolls < maxScrolls)
            {
                await _visualRuntime.SwipeAsync(
                        connection,
                        task.Swipe!,
                        definition.ReferenceWidth,
                        definition.ReferenceHeight,
                        taskName,
                        cancellationToken)
                    .ConfigureAwait(false);
                scrolls++;
                AddTaskLog(
                    logSink,
                    taskName,
                    $"Template not visible; scrolled page {scrolls}/{maxScrolls}.",
                    LogEntryKind.Info);
                await _visualRuntime.DelayAsync(
                        Math.Clamp(pollInterval, 50, 10_000),
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            return latest;
        }
    }

    private async Task<(string TemplatePath, TemplateMatchResult Match)?>
        WaitForAnyTransitionTemplateAsync(
            LastVerifiedConnection connection,
            HachimiPipelineDefinition definition,
            HachimiPipelineTask task,
            string taskName,
            CancellationToken cancellationToken)
    {
        var timeoutMilliseconds = Math.Clamp(
            task.TransitionTimeoutMilliseconds,
            250,
            10 * 60 * 1000);
        var pollInterval = Math.Max(50, task.TransitionPollIntervalMilliseconds);

        foreach (var templatePath in task.TransitionTemplates.Where(
                     path => !string.IsNullOrWhiteSpace(path)))
        {
            var match = await _visualRuntime.WaitForMatchAsync(
                    connection,
                    templatePath,
                    task.TransitionRoi,
                    task.TransitionThreshold,
                    definition.ReferenceWidth,
                    definition.ReferenceHeight,
                    timeoutMilliseconds,
                    pollInterval,
                    taskName,
                    definition.BaseDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            if (match?.Found == true)
                return (templatePath, match);
        }

        return null;
    }

    private async Task<TemplateMatchResult?> WaitForTemplateAttemptAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        string taskName,
        HachimiPipelineTask task,
        string templatePath,
        int[]? roi,
        bool useScaledTemplate,
        IReadOnlyList<double> scaleCandidates,
        IReadOnlyList<int[]> searchRois,
        int timeoutMilliseconds,
        int pollInterval,
        CancellationToken cancellationToken)
    {
        if (useScaledTemplate)
        {
            return await _visualRuntime.WaitForMatchScaledAsync(
                    connection,
                    templatePath,
                    roi,
                    task.TemplateThreshold,
                    definition.ReferenceWidth,
                    definition.ReferenceHeight,
                    timeoutMilliseconds,
                    pollInterval,
                    taskName,
                    definition.BaseDirectory,
                    scaleCandidates,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (Normalize(task.Algorithm) is "matchtemplatecolor" or "matchtemplatecol")
        {
            return await _visualRuntime.WaitForColorMatchAsync(
                    connection,
                    templatePath,
                    roi,
                    task.TemplateThreshold,
                    definition.ReferenceWidth,
                    definition.ReferenceHeight,
                    timeoutMilliseconds,
                    pollInterval,
                    taskName,
                    definition.BaseDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (searchRois.Count > 0)
        {
            return await _visualRuntime.WaitForMatchInRoisAsync(
                    connection,
                    templatePath,
                    task.TemplateThreshold,
                    definition.ReferenceWidth,
                    definition.ReferenceHeight,
                    timeoutMilliseconds,
                    pollInterval,
                    taskName,
                    definition.BaseDirectory,
                    searchRois,
                    task.MinimumScoreGap,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await _visualRuntime.WaitForMatchAsync(
                connection,
                templatePath,
                roi,
                task.TemplateThreshold,
                definition.ReferenceWidth,
                definition.ReferenceHeight,
                timeoutMilliseconds,
                pollInterval,
                taskName,
                definition.BaseDirectory,
            cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> ClickUntilTemplateGoneAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        string taskName,
        HachimiPipelineTask task,
        string templatePath,
        TemplateMatchResult initialMatch,
        HachimiPipelineRunOptions runOptions,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var maxClickAttempts = task.MaxClickAttempts > 0
            ? task.MaxClickAttempts
            : int.MaxValue;
        var attemptLimitText = task.MaxClickAttempts > 0
            ? $"/{task.MaxClickAttempts}"
            : string.Empty;
        var pollInterval = Math.Max(50, task.PollIntervalMilliseconds);
        var goneConfirmationSamples = Math.Max(2, task.GoneConfirmationSamples);
        var currentMatch = initialMatch;

        for (var clickAttempt = 1; clickAttempt <= maxClickAttempts; clickAttempt++)
        {
            await _visualRuntime.TapMatchAsync(
                    connection,
                    currentMatch,
                    taskName,
                    cancellationToken)
                .ConfigureAwait(false);
            AddTaskLog(
                logSink,
                taskName,
                $"Clicked '{taskName}' at ({currentMatch.CenterX},{currentMatch.CenterY}) "
                + $"(attempt {clickAttempt}{attemptLimitText}); checking that it disappeared.",
                LogEntryKind.Success);

            await _visualRuntime.DelayAsync(pollInterval, cancellationToken)
                .ConfigureAwait(false);

            var missingSamples = 0;
            while (missingSamples < goneConfirmationSamples)
            {
                var stillVisible = await WaitForTemplateAttemptAsync(
                        connection,
                        definition,
                        taskName,
                        task,
                        templatePath,
                        task.Roi,
                        useScaledTemplate: false,
                        scaleCandidates: Array.Empty<double>(),
                        ResolveSearchRois(taskName, task, runOptions),
                        Math.Clamp(pollInterval * 2, 100, 1_000),
                        pollInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (stillVisible is { Found: true })
                {
                    currentMatch = stillVisible;
                    AddTaskLog(
                        logSink,
                        taskName,
                        $"'{taskName}' is still visible after attempt {clickAttempt}; clicking again.",
                        LogEntryKind.Info);
                    break;
                }

                missingSamples++;
                AddTaskLog(
                    logSink,
                    taskName,
                    $"'{taskName}' missing confirmation sample "
                    + $"{missingSamples}/{goneConfirmationSamples}; checking again.",
                    LogEntryKind.Info);
                if (missingSamples >= goneConfirmationSamples)
                {
                    AddTaskLog(
                        logSink,
                        taskName,
                        $"Confirmed '{taskName}' disappeared after {clickAttempt} click attempt(s).",
                        LogEntryKind.Success);
                    return true;
                }

                await _visualRuntime.DelayAsync(pollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return false;
    }

    private static string Normalize(string? value) =>
        value?.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant()
        ?? string.Empty;

    private static string ResolveTargetText(
        string taskName,
        HachimiPipelineTask task,
        HachimiPipelineRunOptions options)
    {
        if (options.TargetTextOverrides is not null
            && options.TargetTextOverrides.TryGetValue(taskName, out var overrideText)
            && !string.IsNullOrWhiteSpace(overrideText))
        {
            return overrideText.Trim();
        }

        return task.TargetText?.Trim() ?? string.Empty;
    }

    private async Task<ScreenTextQueryResult?> WaitForTextWithScrollAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        string taskName,
        HachimiPipelineTask task,
        string targetText,
        int[]? roi,
        int timeoutMilliseconds,
        int pollInterval,
        HachimiPipelineRunOptions runOptions,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(
            timeoutMilliseconds,
            0,
            10 * 60 * 1000));
        var started = Stopwatch.GetTimestamp();
        var scrolls = 0;
        ScreenTextQueryResult? latest = null;
        while (true)
        {
            latest = await _visualRuntime.FindTextAsync(
                    connection,
                    targetText,
                    roi,
                    task.FuzzyThreshold,
                    task.Unique,
                    definition.ReferenceWidth,
                    definition.ReferenceHeight,
                    task.OcrLanguage,
                    taskName,
                    task.OcrMatchMode,
                    task.OcrGroupRowHeight,
                    task.OcrRowGap,
                    task.OcrRequireAllTokens,
                    cancellationToken)
                .ConfigureAwait(false);
            if (latest?.Found == true || latest?.Ambiguous == true)
                return latest;

            if (latest is not null && latest.Candidates.Count > 0)
            {
                AddTaskLog(
                    logSink,
                    taskName,
                    $"OCR retry recognized={latest.RecognizedSummary}",
                    LogEntryKind.Info);
            }

            if (Stopwatch.GetElapsedTime(started) >= timeout)
                return latest;

            var hasSwipe = task.Swipe is { Length: >= 5 };
            if (hasSwipe && scrolls < Math.Clamp(task.MaxScrolls, 0, 100))
            {
                await _visualRuntime.SwipeAsync(
                        connection,
                        task.Swipe!,
                        definition.ReferenceWidth,
                        definition.ReferenceHeight,
                        taskName,
                        cancellationToken)
                    .ConfigureAwait(false);
                scrolls++;
                AddTaskLog(
                    logSink,
                    taskName,
                    $"OCR target not visible; scrolled page {scrolls}/{Math.Clamp(task.MaxScrolls, 0, 100)}.",
                    LogEntryKind.Info);
            }

            await _visualRuntime.DelayAsync(
                    Math.Clamp(pollInterval, 50, 10_000),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string FormatDetections(IReadOnlyList<ScreenTextDetection> detections) =>
        detections.Count == 0
            ? "none"
            : string.Join(
                "; ",
                detections.Select(detection =>
                    $"{detection.Text}@{detection.Bounds} "
                    + $"conf={detection.Confidence:0.000}"));

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

    private static int ScaleReferenceCoordinate(
        int value,
        int reference,
        int actual) =>
        Math.Clamp(
            (int)Math.Round(value * (double)Math.Max(1, actual)
                / Math.Max(1, reference)),
            0,
            Math.Max(0, actual - 1));

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

    private static IReadOnlyList<int[]> ResolveSearchRois(
        string taskName,
        HachimiPipelineTask task,
        HachimiPipelineRunOptions options)
    {
        if (options.SearchRoiOverrides is not null
            && options.SearchRoiOverrides.TryGetValue(taskName, out var searchRois))
        {
            return searchRois;
        }

        return task.SearchRois;
    }

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

        public int GetTaskCount(string taskName, HachimiPipelineTask task) =>
            _taskCounts.TryGetValue(GetCountKey(taskName, task), out var count)
                ? count
                : 0;

        public void IncrementTaskCount(string taskName, HachimiPipelineTask task)
        {
            var countKey = GetCountKey(taskName, task);
            _taskCounts[countKey] = _taskCounts.TryGetValue(countKey, out var count)
                ? count + 1
                : 1;
        }

        private static string GetCountKey(string taskName, HachimiPipelineTask task) =>
            task.CountKey is { Length: > 0 } countKey
                ? countKey
                : taskName;
    }

    private sealed record ParallelMonitorCandidate(
        string Name,
        HachimiPipelineTask Task,
        GrayImage Template);

    private sealed record TaskExecutionResult(
        bool Succeeded,
        string Message,
        string? LastTask,
        string? TransitionTask,
        bool Retryable)
    {
        public static TaskExecutionResult Completed(
            string taskName,
            string? transitionTask = null) =>
            new(true, string.Empty, taskName, transitionTask, false);

        public static TaskExecutionResult Failed(
            string message,
            bool retryable = false) =>
            new(false, message, null, null, retryable);

        public static TaskExecutionResult RetryableFailure(string message) =>
            new(false, message, null, null, true);
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

    /// <summary>
    /// Runtime search-ROI subsets for data-driven layouts. The candidate
    /// regions remain declared by the JSON task; callers may restrict which
    /// of those regions are eligible for this invocation.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int[]>>? SearchRoiOverrides { get; init; }

    /// <summary>
    /// Runtime template paths supplied by a caller for data-driven cards.
    /// The JSON task still owns the algorithm and click action.
    /// </summary>
    public IReadOnlyDictionary<string, string>? TemplateOverrides { get; init; }

    /// <summary>
    /// Runtime text values supplied by a caller for data-driven JSON Input
    /// tasks. The task definition still owns the input action and all page
    /// navigation; callers only choose the semantic value to enter.
    /// </summary>
    public IReadOnlyDictionary<string, string>? InputTextOverrides { get; init; }

    /// <summary>
    /// Runtime OCR values for data-driven semantic tasks.  The JSON task
    /// still owns ROI, matching policy, scroll and click behavior; callers
    /// only supply the race/card/skill text to find.
    /// </summary>
    public IReadOnlyDictionary<string, string>? TargetTextOverrides { get; init; }

    /// <summary>
    /// Concise semantic events for the Hachimi workspace. This is intentionally
    /// separate from the diagnostic sink used by the legacy global log.
    /// </summary>
    public IHachimiTaskLogSink? TaskLogSink { get; set; }

    /// <summary>
    /// Only explicitly described steps for this profile are shown in the
    /// Hachimi workspace log. Unmapped JSON implementation nodes stay in the
    /// legacy diagnostic log only.
    /// </summary>
    public HachimiTaskLogProfile SemanticProfile { get; set; }

    public Func<
        LastVerifiedConnection,
        HachimiPipelineDefinition,
        string,
        HachimiPipelineTask,
        IGrassTaskLogSink?,
        CancellationToken,
        Task<HachimiCustomActionResult>>? CustomActionExecutor { get; init; }

    public int PipelineDepth { get; init; }

    /// <summary>
    /// Default number of additional attempts for retry-safe visual misses.
    /// Task-level retryTimes takes precedence when it is greater than zero.
    /// </summary>
    public int DefaultTaskRetryTimes { get; init; }

    /// <summary>Delay used when a task does not define retryDelayMs.</summary>
    public int DefaultTaskRetryDelayMilliseconds { get; init; } = 750;
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
