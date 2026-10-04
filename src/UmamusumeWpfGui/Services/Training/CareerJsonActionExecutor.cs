using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public enum CareerActionStatus
{
    Applied,
    AlreadySatisfied,
    AwaitingConfirmation,
    NotApplicable,
    Failed,
}

public sealed record CareerActionExecutionResult(
    bool Succeeded,
    string Message,
    string LastScreenId)
{
    public CareerActionStatus Status { get; init; } = Succeeded
        ? CareerActionStatus.Applied
        : CareerActionStatus.Failed;
    public HachimiFailureKind FailureKind { get; init; }
    public string? Outcome { get; init; }

    public static CareerActionExecutionResult Success(string screenId) =>
        new(true, string.Empty, screenId);
}

/// <summary>
/// Internal seam for behavior tests. Production callers continue to use the
/// public <see cref="CareerJsonActionExecutor"/> constructor path.
/// </summary>
internal interface ICareerActionExecutor
{
    Task<CareerActionExecutionResult> RunAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string screenId,
        string actionId,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken,
        HachimiPipelineRunOptions? options = null,
        bool allowVisualMiss = false);

    Task<CareerActionExecutionResult> RunTaskAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string taskName,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken,
        HachimiPipelineRunOptions? options = null);
}

internal interface ICareerTaskLogAware
{
    void SetTaskLogSink(IHachimiTaskLogSink? taskLogSink);
}

/// <summary>
/// The small JSON action adapter shared by the entry navigator and
/// Independent setup. It owns no career strategy or session state.
/// </summary>
public sealed class CareerJsonActionExecutor : ICareerActionExecutor, ICareerTaskLogAware
{
    private readonly HachimiJsonPipelineRunner _jsonRunner;
    private IHachimiTaskLogSink? _taskLogSink;

    public CareerJsonActionExecutor(HachimiJsonPipelineRunner jsonRunner)
    {
        _jsonRunner = jsonRunner ?? throw new ArgumentNullException(nameof(jsonRunner));
    }

    public void SetTaskLogSink(IHachimiTaskLogSink? taskLogSink) => _taskLogSink = taskLogSink;

    public async Task<CareerActionExecutionResult> RunAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string screenId,
        string actionId,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken,
        HachimiPipelineRunOptions? options = null,
        bool allowVisualMiss = false)
    {
        options ??= new HachimiPipelineRunOptions();
        options.TaskLogSink ??= _taskLogSink;
        options.SemanticProfile = HachimiTaskLogProfile.Career;
        var resolvedScreenId = screenId;
        var screen = pack.ScreenProfile.Find(resolvedScreenId);
        if (screen is null)
        {
            return new(false,
                $"Screen '{resolvedScreenId}' is missing from the Career screen fragments.",
                resolvedScreenId) { FailureKind = HachimiFailureKind.InvalidDefinition };
        }

        var action = screen.FindAction(actionId);
        if (action is null || string.IsNullOrWhiteSpace(action.Task))
        {
            return new(false,
                $"Screen action '{resolvedScreenId}.{actionId}' is missing from the Career screen fragments.",
                resolvedScreenId) { FailureKind = HachimiFailureKind.InvalidDefinition };
        }

        var result = await _jsonRunner.RunAsync(
                connection,
                pack.ExecutionDefinition,
                action.Task,
                options,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Succeeded)
            return CareerActionExecutionResult.Success(resolvedScreenId) with { Outcome = result.Outcome };

        if (allowVisualMiss && result.FailureKind == HachimiFailureKind.RecognitionTimeout)
            return CareerActionExecutionResult.Success(resolvedScreenId) with { Status = CareerActionStatus.NotApplicable };

        return new(false,
            $"Could not execute JSON task '{action.Task}' for '{resolvedScreenId}.{actionId}': "
                + result.Message,
            resolvedScreenId) { FailureKind = result.FailureKind };
    }

    public async Task<CareerActionExecutionResult> RunTaskAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string taskName,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken,
        HachimiPipelineRunOptions? options = null)
    {
        options ??= new HachimiPipelineRunOptions();
        options.TaskLogSink ??= _taskLogSink;
        options.SemanticProfile = HachimiTaskLogProfile.Career;
        var result = await _jsonRunner.RunAsync(
                connection,
                pack.ExecutionDefinition,
                taskName,
                options,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded
            ? CareerActionExecutionResult.Success(taskName) with { Outcome = result.Outcome }
            : new(false, result.Message, taskName) { FailureKind = result.FailureKind };
    }
}

