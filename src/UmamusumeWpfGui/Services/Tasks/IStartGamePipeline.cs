using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Runs the game-specific post-launch navigation graph. The launcher only
/// starts the package; this pipeline is responsible for reaching the home
/// screen through screenshots and input actions.
/// </summary>
public interface IStartGamePipeline
{
    Task<StartGamePipelineResult> RunAsync(
        LastVerifiedConnection connection,
        string packageName,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default);
}
