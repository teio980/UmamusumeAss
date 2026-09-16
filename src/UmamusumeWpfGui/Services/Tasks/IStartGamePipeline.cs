using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;






public interface IStartGamePipeline
{
    Task<StartGamePipelineResult> RunAsync(
        LastVerifiedConnection connection,
        string packageName,
        IGrassTaskLogSink? logSink = null,
        IHachimiTaskLogSink? taskLogSink = null,
        CancellationToken cancellationToken = default);
}
