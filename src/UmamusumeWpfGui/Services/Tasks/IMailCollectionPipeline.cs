using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

public interface IMailCollectionPipeline
{
    Task<MailCollectionPipelineResult> RunAsync(
        LastVerifiedConnection connection,
        string definitionPath,
        IGrassTaskLogSink? logSink = null,
        IHachimiTaskLogSink? taskLogSink = null,
        CancellationToken cancellationToken = default);

    Task<MailCollectionPipelineResult> StopAsync(
        LastVerifiedConnection connection,
        IGrassTaskLogSink? logSink = null,
        IHachimiTaskLogSink? taskLogSink = null,
        CancellationToken cancellationToken = default);
}

public sealed record MailCollectionPipelineResult(
    bool Succeeded,
    string Message);
