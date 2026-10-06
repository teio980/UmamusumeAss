using System.Diagnostics;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

internal sealed class CareerRestFlow(IVisualPipelineRuntime visual)
{
    private readonly Dictionary<string, GrayImage> _templates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<CareerTrainingResult?> RunAsync(CareerFlowContext context,
        string screenId, string actionId)
    {
        if (context.State.AwaitingRestConfirmationGone)
            return null;
        var started = Stopwatch.GetTimestamp();
        var pack = context.Pack;
        var confirmationId = screenId == "career_main"
            ? actionId == "action.summer_rest" ? "summer_rest_confirmation" : "rest_confirmation"
            : screenId;
        (string Screen, string Action)[] steps = screenId == "career_main"
            ? [(screenId, actionId), (confirmationId, "rest.confirm")]
            : [(confirmationId, "rest.confirm")];

        foreach (var (id, action) in steps)
        {
            var screen = pack.ScreenProfile.Find(id);
            var taskName = screen?.FindAction(action)?.Task;
            if (screen is null || taskName is null
                || !pack.ExecutionDefinition.TryGetTask(taskName, out var task)
                || task is null || string.IsNullOrWhiteSpace(task.Template))
                return CareerRuntimeResults.Failure("Rest action resources are missing.", id);
            var path = pack.VisualResources is { } resources
                && resources.TryGetResolvedTaskTemplate(taskName, out var resolved)
                ? resolved! : UraScenarioResourceResolver.Resolve(pack, task.Template);
            var template = await LoadAsync(context, path).ConfigureAwait(false);
            var confirming = action == "rest.confirm";
            var titles = new List<GrayImage>();
            if (confirming)
                foreach (var titlePath in screen.Templates)
                {
                    var title = await LoadAsync(context, UraScenarioResourceResolver.Resolve(pack, screen, titlePath))
                        .ConfigureAwait(false);
                    // Match the Rest lettering; the full dialog also resembles Recreation and Infirmary.
                    if (id == "rest_confirmation" && title is { Width: 900, Height: 700 })
                        title = GrayImageCodec.Crop(title, new System.Windows.Int32Rect(410, 35, 85, 40));
                    if (title is not null) titles.Add(title);
                }
            if (template is null || (confirming && titles.Count == 0))
                return CareerRuntimeResults.Failure("Rest templates could not be loaded.", id);

            var waiting = Stopwatch.GetTimestamp();
            while (true)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                GrayImage? frame;
                try
                {
                    frame = await visual.CaptureGrayAsync(context.Connection, context.CancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException
                    and not DateChangedInterruptionException and not DateChangedRecoveryException)
                {
                    frame = null;
                }
                if (frame is not null && (!confirming || titles.Any(title => (id == "rest_confirmation"
                        ? TemplateMatcher.FindColor(frame, title, [390, 490, 120, 50], .92,
                            pack.ScreenProfile.ReferenceWidth, pack.ScreenProfile.ReferenceHeight, requireTextContrast: true)
                        : TemplateMatcher.Find(frame, title, screen.Recognition.Roi, screen.Recognition.TemplateThreshold,
                            pack.ScreenProfile.ReferenceWidth, pack.ScreenProfile.ReferenceHeight)).Found)))
                {
                    var match = task.Algorithm.Equals("MatchTemplateColor", StringComparison.OrdinalIgnoreCase)
                        ? TemplateMatcher.FindColor(frame, template, task.Roi, task.TemplateThreshold,
                            pack.ExecutionDefinition.ReferenceWidth, pack.ExecutionDefinition.ReferenceHeight)
                        : TemplateMatcher.Find(frame, template, task.Roi, task.TemplateThreshold,
                            pack.ExecutionDefinition.ReferenceWidth, pack.ExecutionDefinition.ReferenceHeight);
                    if (match.Found)
                    {
                        if (confirming)
                        {
                            context.State.LastAction = UraPlannedAction.Rest;
                            CareerTurnFlow.ArmPendingGoalProbe(context.State);
                            // Arm before input so an interrupted tap cannot send OK twice.
                            context.State.AwaitingRestConfirmationGone = true;
                        }
                        await visual.TapMatchAsync(context.Connection, frame, match, taskName, context.CancellationToken)
                            .ConfigureAwait(false);
                        if (confirming) context.State.LastScreenId = id;
                        break;
                    }
                }
                if (Stopwatch.GetElapsedTime(waiting).TotalMilliseconds >= Math.Max(1, task.TimeoutMilliseconds))
                    return CareerRuntimeResults.Failure("Rest button or dialog did not appear.", id);
                await visual.DelayAsync(100, context.CancellationToken).ConfigureAwait(false);
            }
        }
        context.LogSink?.Add("Career Training",
            $"Rest → OK completed in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms.");
        return null;
    }

    private async Task<GrayImage?> LoadAsync(CareerFlowContext context, string path)
    {
        if (_templates.TryGetValue(path, out var cached)) return cached;
        var template = await visual.LoadTemplateAsync(path, string.Empty, context.CancellationToken).ConfigureAwait(false);
        if (template is not null) _templates.Add(path, template);
        return template;
    }
}
