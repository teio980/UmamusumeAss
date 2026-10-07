using System.Diagnostics;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

internal sealed class CareerBreakFlow(IVisualPipelineRuntime visual)
{
    private readonly Dictionary<string, GrayImage> _templates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<CareerTrainingResult?> RunAsync(CareerFlowContext context,
        string screenId, string actionId)
    {
        var recreation = actionId.StartsWith("recreation.", StringComparison.Ordinal) || actionId == "action.recreation";
        if (recreation ? context.State.AwaitingRecreationConfirmationGone : context.State.AwaitingRestConfirmationGone)
            return null;
        var started = Stopwatch.GetTimestamp();
        var pack = context.Pack;
        var confirmationId = recreation ? "recreation_confirmation" : screenId == "career_main"
            ? actionId == "action.summer_rest" ? "summer_rest_confirmation" : "rest_confirmation"
            : screenId;
        (string Screen, string Action)[] steps = recreation ? screenId switch
        {
            "career_main" => [(screenId, actionId), ("recreation_selection", "recreation.trainee"),
                (confirmationId, "recreation.confirm")],
            "recreation_selection" => [(screenId, actionId), (confirmationId, "recreation.confirm")],
            _ => [(confirmationId, "recreation.confirm")],
        } : screenId == "career_main"
            ? [(screenId, actionId), (confirmationId, "rest.confirm")]
            : [(confirmationId, "rest.confirm")];

        GrayImage? nextFrame = null;
        var label = recreation ? "Recreation" : "Rest";
        foreach (var (id, action) in steps)
        {
            var screen = pack.ScreenProfile.Find(id);
            var taskName = screen?.FindAction(action)?.Task;
            if (screen is null || taskName is null
                || !pack.ExecutionDefinition.TryGetTask(taskName, out var task)
                || task is null || string.IsNullOrWhiteSpace(task.Template))
                return CareerRuntimeResults.Failure($"{label} action resources are missing.", id);
            var path = pack.VisualResources is { } resources
                && resources.TryGetResolvedTaskTemplate(taskName, out var resolved)
                ? resolved! : UraScenarioResourceResolver.Resolve(pack, task.Template);
            var template = await LoadAsync(context, path).ConfigureAwait(false);
            var confirming = id == confirmationId;
            if (template is null)
                return CareerRuntimeResults.Failure($"{label} template could not be loaded.", id);

            var waiting = Stopwatch.GetTimestamp();
            while (true)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                GrayImage? frame;
                try
                {
                    frame = nextFrame ?? await visual.CaptureGrayAsync(context.Connection, context.CancellationToken)
                        .ConfigureAwait(false);
                    nextFrame = null;
                }
                catch (Exception exception) when (exception is not OperationCanceledException
                    and not DateChangedInterruptionException and not DateChangedRecoveryException)
                {
                    frame = null;
                }
                if (frame is not null && id == "career_main"
                    && await CareerMainScreenDetector.MatchAsync(frame, pack,
                            (path, _) => LoadAsync(context, path), context.CancellationToken)
                        .ConfigureAwait(false) is null)
                {
                    context.LogSink?.Add("Career Training",
                        $"{label}: Career header and Training button are not both visible; reobserving the screen.");
                    return null;
                }
                // Some Recreation entries go straight to OK. Reuse that frame rather than opening a picker.
                if (frame is not null && id == "recreation_selection"
                    && await MatchesScreenAsync(context, confirmationId, frame).ConfigureAwait(false))
                {
                    nextFrame = frame;
                    break;
                }
                if (frame is not null && (id == "career_main"
                    || await MatchesScreenAsync(context, id, frame).ConfigureAwait(false)))
                {
                    var colorText = task.Algorithm.Equals("MatchTemplateColorText", StringComparison.OrdinalIgnoreCase);
                    var match = colorText || task.Algorithm.Equals("MatchTemplateColor", StringComparison.OrdinalIgnoreCase)
                        ? TemplateMatcher.FindColor(frame, template, task.Roi, task.TemplateThreshold,
                            pack.ExecutionDefinition.ReferenceWidth, pack.ExecutionDefinition.ReferenceHeight,
                            requireTextContrast: colorText)
                        : TemplateMatcher.Find(frame, template, task.Roi, task.TemplateThreshold,
                            pack.ExecutionDefinition.ReferenceWidth, pack.ExecutionDefinition.ReferenceHeight);
                    if (match.Found)
                    {
                        if (confirming)
                        {
                            context.State.LastAction = recreation ? UraPlannedAction.Recreation : UraPlannedAction.Rest;
                            CareerTurnFlow.ArmPendingGoalProbe(context.State);
                            // Arm before input so an interrupted tap cannot send OK twice.
                            if (recreation) context.State.AwaitingRecreationConfirmationGone = true;
                            else context.State.AwaitingRestConfirmationGone = true;
                        }
                        await visual.TapMatchAsync(context.Connection, frame, match, taskName, context.CancellationToken)
                            .ConfigureAwait(false);
                        if (confirming) context.State.LastScreenId = id;
                        break;
                    }
                }
                if (Stopwatch.GetElapsedTime(waiting).TotalMilliseconds >= Math.Max(1, task.TimeoutMilliseconds))
                    return CareerRuntimeResults.Failure($"{label} button or dialog did not appear.", id);
                await visual.DelayAsync(100, context.CancellationToken).ConfigureAwait(false);
            }
        }
        context.LogSink?.Add("Career Training",
            $"{label} → OK completed in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms.");
        return null;
    }

    private async Task<bool> MatchesScreenAsync(CareerFlowContext context, string id, GrayImage frame)
    {
        var profile = context.Pack.ScreenProfile;
        var screen = profile.Find(id);
        if (screen is null) return false;
        foreach (var path in screen.Templates)
        {
            var title = await LoadAsync(context, UraScenarioResourceResolver.Resolve(context.Pack, screen, path))
                .ConfigureAwait(false);
            // The full Rest dialog resembles Recreation and Infirmary; its lettering distinguishes it.
            if (id == "rest_confirmation" && title is { Width: 900, Height: 700 })
                title = GrayImageCodec.Crop(title, new System.Windows.Int32Rect(410, 35, 85, 40));
            if (title is null) continue;
            var match = id == "rest_confirmation"
                ? TemplateMatcher.FindColor(frame, title, [390, 490, 120, 50], .92,
                    profile.ReferenceWidth, profile.ReferenceHeight, requireTextContrast: true)
                : screen.Recognition.MatchColorText
                    ? TemplateMatcher.FindColor(frame, title, screen.Recognition.Roi, screen.Recognition.TemplateThreshold,
                        profile.ReferenceWidth, profile.ReferenceHeight, requireTextContrast: true)
                    : TemplateMatcher.Find(frame, title, screen.Recognition.Roi, screen.Recognition.TemplateThreshold,
                        profile.ReferenceWidth, profile.ReferenceHeight);
            if (match.Found) return true;
        }
        return false;
    }

    private async Task<GrayImage?> LoadAsync(CareerFlowContext context, string path)
    {
        if (_templates.TryGetValue(path, out var cached)) return cached;
        var template = await visual.LoadTemplateAsync(path, string.Empty, context.CancellationToken).ConfigureAwait(false);
        if (template is not null) _templates.Add(path, template);
        return template;
    }
}
