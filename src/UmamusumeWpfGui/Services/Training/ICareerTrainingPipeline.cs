using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public interface ICareerTrainingPipeline
{
    Task<CareerTrainingResult> RunAsync(
        LastVerifiedConnection connection,
        CareerTrainingSettings settings,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken = default);

    Task<CareerTrainingResult> StopAsync(
        LastVerifiedConnection connection,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default);
}

public sealed record CareerTrainingSettings(
    string ManifestPath,
    int TraineeId,
    IReadOnlyList<int> SupportCardIds,
    string SupportDeckMode,
    string SupportDeckPreset,
    string StrategyId,
    bool PauseOnUnknownOutcome,
    bool AllowOptionalRaces,
    string LegacySelectionMode,
    bool UseLegacyGuest,
    IReadOnlyList<string> LegacyAttributeSparks,
    IReadOnlyList<string> LegacyAptitudeSparks);

public sealed record CareerTrainingResult(
    bool Succeeded,
    string Message,
    int ActionsCompleted,
    string LastScreenId);
