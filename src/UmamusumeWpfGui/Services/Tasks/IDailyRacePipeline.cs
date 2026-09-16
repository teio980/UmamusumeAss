using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

public interface IDailyRacePipeline
{
    Task<DailyRacePipelineResult> RunAsync(
        LastVerifiedConnection connection,
        string definitionPath,
        string mode,
        string difficulty,
        int raceCount,
        IGrassTaskLogSink? logSink = null,
        IHachimiTaskLogSink? taskLogSink = null,
        CancellationToken cancellationToken = default);

    Task<DailyRacePipelineResult> RunWithTraineeAsync(
        LastVerifiedConnection connection,
        string definitionPath,
        string mode,
        string difficulty,
        int raceCount,
        int? traineeId,
        IGrassTaskLogSink? logSink = null,
        IHachimiTaskLogSink? taskLogSink = null,
        CancellationToken cancellationToken = default);

    Task<DailyRacePipelineResult> StopAsync(
        LastVerifiedConnection connection,
        IGrassTaskLogSink? logSink = null,
        IHachimiTaskLogSink? taskLogSink = null,
        CancellationToken cancellationToken = default);
}

public sealed record DailyRacePipelineResult(
    bool Succeeded,
    int RacesCompleted,
    string Message);
