using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Observes an already submitted confirmation without issuing another tap.
/// Screenshot failures are inconclusive; only a captured frame can prove disappearance.
/// </summary>
internal sealed class CareerActionCompletionObserver
{
    private readonly IVisualPipelineRuntime _visual;
    private readonly int _retryLimit;
    private readonly Dictionary<string, GrayImage> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public CareerActionCompletionObserver(IVisualPipelineRuntime visual, int retryLimit)
    {
        _visual = visual ?? throw new ArgumentNullException(nameof(visual));
        _retryLimit = retryLimit;
    }

    public async Task<CareerActionExecutionResult?> ObserveAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        UraCareerSessionState state,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var pending = state.Runtime.TurnActionTransition;
        var screenId = pending is { AwaitingConfirmation: true }
            ? pending.ConfirmationScreenId
            : state.AwaitingRecreationConfirmationGone
            ? "recreation_confirmation"
            : state.AwaitingRestConfirmationGone ? "rest_confirmation" : null;
        if (screenId is null)
            return null;

        var semanticId = pending?.ConfirmationActionId
            ?? (screenId == "recreation_confirmation" ? "recreation.confirm" : "rest.confirm");
        var binding = pack.ScreenProfile.Find(screenId)?.FindAction(semanticId);
        if (binding is null
            || !pack.ExecutionDefinition.TryGetTask(binding.Task, out var task)
            || task is null || string.IsNullOrWhiteSpace(task.Template))
        {
            return new(false, $"Confirmation visual resource '{screenId}.{semanticId}' is missing.", screenId)
            { FailureKind = HachimiFailureKind.InvalidDefinition };
        }

        if (!_templates.TryGetValue(screenId, out var template))
        {
            template = await _visual.LoadTemplateAsync(task.Template,
                    pack.ExecutionDefinition.BaseDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (template is null)
                return new(false, $"Confirmation template '{screenId}' could not be loaded.", screenId)
                { FailureKind = HachimiFailureKind.InvalidDefinition };
            _templates.Add(screenId, template);
        }

        GrayImage? frame;
        try
        {
            frame = await _visual.CaptureGrayAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not DateChangedInterruptionException and not DateChangedRecoveryException)
        {
            frame = null;
        }

        var disappeared = frame is not null && (screenId is "recreation_confirmation" or "infirmary_confirmation"
            ? !TemplateMatcher.FindColor(frame, template, task.Roi, task.TemplateThreshold,
                pack.ExecutionDefinition.ReferenceWidth, pack.ExecutionDefinition.ReferenceHeight,
                requireTextContrast: task.Algorithm.Equals("MatchTemplateColorText", StringComparison.OrdinalIgnoreCase)
                    || screenId == "recreation_confirmation").Found
            : CareerRestConfirmationGate.HasOkDisappeared(frame, template, task,
                pack.ExecutionDefinition.ReferenceWidth, pack.ExecutionDefinition.ReferenceHeight));
        if (disappeared)
        {
            if (screenId == "recreation_confirmation")
                state.AwaitingRecreationConfirmationGone = false;
            else if (screenId is "rest_confirmation" or "summer_rest_confirmation")
                state.AwaitingRestConfirmationGone = false;
            pending?.ConfirmationDisappeared(frame!);
            _attempts.Remove(screenId);
            logSink?.Add("Career Training", $"{screenId}: confirmation disappeared; observing the next screen.");
            return CareerActionExecutionResult.Success(screenId) with { Status = CareerActionStatus.AlreadySatisfied };
        }

        var attempts = _attempts.GetValueOrDefault(screenId) + 1;
        _attempts[screenId] = attempts;
        if (attempts >= _retryLimit)
            return new(false, $"Confirmation '{screenId}' did not disappear; automation paused safely.", screenId)
            { FailureKind = HachimiFailureKind.RecognitionTimeout };

        return CareerActionExecutionResult.Success(screenId) with { Status = CareerActionStatus.AwaitingConfirmation };
    }
}
