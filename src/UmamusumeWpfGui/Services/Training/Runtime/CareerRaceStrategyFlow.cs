using System.Diagnostics;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>Changes race strategy only when necessary, using fresh frames at every transition.</summary>
internal sealed class CareerRaceStrategyFlow(IVisualPipelineRuntime visual, ICareerFlowActionRunner actions)
{
    private static readonly string[] Strategies = ["front", "pace", "late", "end"];
    private const int PollMilliseconds = 120;
    private readonly Dictionary<string, GrayImage> _templates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<CareerTrainingResult?> RunAsync(CareerFlowContext context, string strategy)
    {
        var started = Stopwatch.GetTimestamp();
        var screen = context.Pack.ScreenProfile.Find(CareerRaceRunnerCheckpointHandler.ScreenId);
        var chains = Strategies.Select(type => ReadChain(context.Pack, screen, type)).ToArray();
        // Customized task graphs retain the JSON runner's original semantics.
        if (chains.Any(chain => chain is null))
            return await actions.RunAsync(context, CareerRaceRunnerCheckpointHandler.ScreenId,
                $"strategy.apply.{strategy}").ConfigureAwait(false);

        var chain = chains.Single(item => item!.Strategy == strategy)!;
        var templates = new Dictionary<string, GrayImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in chains.SelectMany(item => new[] { item!.Probe, item.Selected })
                     .Concat([chain.Change, chain.Option, chain.Save]).DistinctBy(item => item.Name))
        {
            var path = context.Pack.VisualResources is { } resources
                && resources.TryGetResolvedTaskTemplate(definition.Name, out var resolved)
                    ? resolved! : Path.GetFullPath(definition.Task.Template!, context.Pack.ExecutionDefinition.BaseDirectory);
            var template = await LoadAsync(context, path).ConfigureAwait(false);
            if (template is null)
                return Failure($"Race strategy template '{definition.Name}' could not be loaded.");
            templates.Add(definition.Name, template);
        }
        var change = chain.Change;
        var option = chain.Option;
        var save = chain.Save;
        var probes = chains.Select(item => (item!.Strategy, item.Probe)).ToArray();
        var selections = chains.Select(item => (item!.Strategy, item.Selected)).ToArray();

        TemplateMatchResult Match(GrayImage frame, TaskDefinition target) =>
            target.Task.Algorithm == "MatchTemplateColor"
                ? TemplateMatcher.FindColor(frame, templates[target.Name], target.Task.Roi, target.Task.TemplateThreshold,
                    context.Pack.ExecutionDefinition.ReferenceWidth, context.Pack.ExecutionDefinition.ReferenceHeight)
                : TemplateMatcher.Find(frame, templates[target.Name], target.Task.Roi, target.Task.TemplateThreshold,
                    context.Pack.ExecutionDefinition.ReferenceWidth, context.Pack.ExecutionDefinition.ReferenceHeight);

        string? ReadSelected(GrayImage frame, (string Strategy, TaskDefinition Task)[] targets)
        {
            string? selected = null;
            foreach (var target in targets)
            {
                if (!Match(frame, target.Task).Found)
                    continue;
                if (selected is not null)
                    return null;
                selected = target.Strategy;
            }
            return selected;
        }

        DialogFrame? ReadDialog(GrayImage frame)
        {
            var optionMatch = Match(frame, option);
            if (!optionMatch.Found)
                return null;
            var saveMatch = Match(frame, save);
            return saveMatch.Found ? new(frame, optionMatch, saveMatch,
                ReadSelected(frame, selections)) : null;
        }

        DialogFrame? dialog = null;
        var opened = false;
        // Absence is not evidence of another strategy. Retry missing/ambiguous markers,
        // then inspect the settings dialog instead of treating them as a mismatch.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var frame = await CaptureAsync(context).ConfigureAwait(false);
            if (frame is not null)
            {
                dialog = ReadDialog(frame);
                if (dialog is not null)
                    break;
                var changeMatch = Match(frame, change);
                if (changeMatch.Found)
                {
                    var selected = ReadSelected(frame, probes);
                    if (selected == strategy)
                    {
                        Log(context, strategy, "already selected", started);
                        return null;
                    }
                    // The caller has observed race_runner. Recheck its Change
                    // control on this fresh frame before opening the dialog.
                    if (selected is not null || attempt == 2)
                    {
                        await TapAsync(context, frame, change, changeMatch).ConfigureAwait(false);
                        opened = true;
                        break;
                    }
                }
            }
            if (attempt < 2)
                await visual.DelayAsync(PollMilliseconds, context.CancellationToken).ConfigureAwait(false);
        }
        if (dialog is null && !opened)
            return Failure("Could not verify the race runner or strategy dialog; no strategy input was sent.");

        dialog ??= await WaitAsync(context, option.Task.TimeoutMilliseconds, ReadDialog).ConfigureAwait(false);
        if (dialog is null)
            return Failure("The race strategy dialog did not become ready after opening it.");

        var changed = dialog.SelectedStrategy != strategy;
        if (changed)
        {
            await TapAsync(context, dialog.Frame, option, dialog.OptionMatch).ConfigureAwait(false);
            dialog = await WaitAsync(context, chain.Selected.Task.TimeoutMilliseconds, frame =>
            {
                var reading = ReadDialog(frame);
                return reading?.SelectedStrategy == strategy ? reading : null;
            }).ConfigureAwait(false);
            if (dialog is null)
                return Failure("The requested race strategy was not uniquely selected; confirmation was not sent.");
        }

        // Selection evidence and the Confirm button come from the same captured frame.
        await TapAsync(context, dialog.Frame, save, dialog.SaveMatch).ConfigureAwait(false);
        var verified = await WaitAsync(context, save.Task.TimeoutMilliseconds, frame =>
            ReadDialog(frame) is null && Match(frame, change).Found
                && ReadSelected(frame, probes) == strategy ? frame : null).ConfigureAwait(false);
        if (verified is null)
            return Failure("Race strategy confirmation was sent once, but the saved strategy could not be verified.");

        Log(context, strategy, changed ? "changed and verified" : "dialog already selected; saved and verified", started);
        return null;
    }

    private static StrategyChain? ReadChain(UraScenarioPack pack, UraScreenDefinition? screen, string strategy)
    {
        TaskDefinition? Read(string? name, string action)
        {
            if (name is null || !pack.ExecutionDefinition.TryGetTask(name, out var task) || task is null
                || !task.Action.Equals(action, StringComparison.OrdinalIgnoreCase)
                || task.Algorithm is not ("MatchTemplate" or "MatchTemplateColor")
                || string.IsNullOrWhiteSpace(task.Template) || task.Roi is not { Length: 4 }
                || task.PreDelay != 0 || task.WaitMilliseconds != 0 || task.TimeoutMilliseconds < 0
                || !task.Required || task.RetryTimes != 0 || task.Outcome is not null
                || task.CountAs is not null || task.CountKey is not null
                || task.Pipeline is not null || task.Entry is not null
                || task.ScaleCandidates.Count != 0 || task.SearchRois.Count != 0
                || task.Sub.Count != 0 || task.MonitorTasks.Count != 0 || task.AlternativeTemplateTasks.Count != 0
                || task.MinimumScoreGap != 0 || task.ClickAnchor is not null || task.ClickVerification is not null
                || task.ClickOffset is not (null or { Length: 2 }) || task.MaxTimes != 0
                || task.ReuseLastMatchOnRetry || task.MaxClickAttempts != 0
                || task.FallbackRoi is not null || task.FallbackClickOffset is not null
                || task.ClickUntilGone || task.RepeatTapUntilTransition || task.TransitionTemplates.Count != 0
                || task.SuccessTask is not null || task.SuccessTasks.Count != 0 || task.ExceededNext.Count != 0)
                return null;
            return new(name, task);
        }

        var probe = Read(screen?.FindAction($"strategy.apply.{strategy}")?.Task, "JustReturn");
        var change = Read(probe?.Task.OnErrorNext.Count == 1 ? probe.Task.OnErrorNext[0] : null, "ClickSelf");
        var option = Read(change?.Task.Next.Count == 1 ? change.Task.Next[0] : null, "ClickSelf");
        var selected = Read(option?.Task.Next.Count == 1 ? option.Task.Next[0] : null, "JustReturn");
        var save = Read(selected?.Task.Next.Count == 1 ? selected.Task.Next[0] : null, "ClickSelf");
        if (probe is null || change is null || option is null || selected is null || save is null
            || probe.Task.Next.Count != 0 || probe.Task.PostDelay != 0
            || !probe.Task.Success || !save.Task.Success
            || change.Task.Success || option.Task.Success || selected.Task.Success
            || selected.Task.PostDelay != 0 || selected.Task.OnErrorNext.Count != 0
            || change.Task.PostDelay is not (0 or 600) || option.Task.PostDelay is not (0 or 350)
            || save.Task.PostDelay is not (0 or 700)
            || change.Task.OnErrorNext.Count != 0 || option.Task.OnErrorNext.Count != 0
            || save.Task.OnErrorNext.Count != 0 || save.Task.Next.Count != 0)
            return null;
        return new(strategy, probe, change, option, selected, save);
    }

    private Task TapAsync(CareerFlowContext context, GrayImage frame, TaskDefinition target, TemplateMatchResult match)
    {
        if (target.Task.ClickOffset is [var x, var y])
            match = match with
            {
                X = match.X + (int)Math.Round(x * frame.Width / (double)context.Pack.ExecutionDefinition.ReferenceWidth),
                Y = match.Y + (int)Math.Round(y * frame.Height / (double)context.Pack.ExecutionDefinition.ReferenceHeight),
            };
        return visual.TapMatchAsync(context.Connection, frame, match, target.Name, context.CancellationToken);
    }

    private async Task<T?> WaitAsync<T>(CareerFlowContext context, int timeoutMilliseconds,
        Func<GrayImage, T?> inspect) where T : class
    {
        var started = Stopwatch.GetTimestamp();
        var waited = 0;
        var timeout = Math.Clamp(timeoutMilliseconds, 1, 600_000);
        while (true)
        {
            var frame = await CaptureAsync(context).ConfigureAwait(false);
            if (frame is not null && inspect(frame) is { } result)
                return result;
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= timeout || waited >= timeout)
                return null;
            var delay = Math.Min(PollMilliseconds, timeout - waited);
            await visual.DelayAsync(delay, context.CancellationToken).ConfigureAwait(false);
            waited += delay;
        }
    }

    private async Task<GrayImage?> CaptureAsync(CareerFlowContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await visual.CaptureGrayAsync(context.Connection, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            and not DateChangedInterruptionException and not DateChangedRecoveryException)
        {
            return null;
        }
    }

    private async Task<GrayImage?> LoadAsync(CareerFlowContext context, string path)
    {
        if (_templates.TryGetValue(path, out var template))
            return template;
        template = await visual.LoadTemplateAsync(path, string.Empty, context.CancellationToken).ConfigureAwait(false);
        if (template is not null)
            _templates.Add(path, template);
        return template;
    }

    private static CareerTrainingResult Failure(string message) =>
        CareerRuntimeResults.Failure(message, CareerRaceRunnerCheckpointHandler.ScreenId);

    private static void Log(CareerFlowContext context, string strategy, string outcome, long started) =>
        context.LogSink?.Add("Career Training",
            $"Race strategy '{strategy}' {outcome} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms.");

    private sealed record TaskDefinition(string Name, HachimiPipelineTask Task);
    private sealed record StrategyChain(string Strategy, TaskDefinition Probe, TaskDefinition Change,
        TaskDefinition Option, TaskDefinition Selected, TaskDefinition Save);
    private sealed record DialogFrame(GrayImage Frame, TemplateMatchResult OptionMatch,
        TemplateMatchResult SaveMatch, string? SelectedStrategy);
}
