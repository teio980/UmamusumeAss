using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public interface IUraTrainingPipeline
{
    Task<UraTrainingResult> RunAsync(
        LastVerifiedConnection connection,
        UraTrainingSettings settings,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken = default);

    Task<UraTrainingResult> StopAsync(
        LastVerifiedConnection connection,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default);
}

public sealed record UraTrainingSettings(
    string ManifestPath,
    int TraineeId,
    IReadOnlyList<int> SupportCardIds,
    string StrategyId,
    bool PauseOnUnknownOutcome,
    bool AllowOptionalRaces);

public sealed record UraTrainingResult(
    bool Succeeded,
    string Message,
    int ActionsCompleted,
    string LastScreenId);
