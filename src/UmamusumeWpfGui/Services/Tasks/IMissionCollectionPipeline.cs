using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

public interface IMissionCollectionPipeline
{
    Task<MissionCollectionPipelineResult> RunAsync(
        LastVerifiedConnection connection,
        string definitionPath,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default);

    Task<MissionCollectionPipelineResult> StopAsync(
        LastVerifiedConnection connection,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default);
}

public sealed record MissionCollectionPipelineResult(
    bool Succeeded,
    string Message);
