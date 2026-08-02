using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Shared low-level visual operations for ordinary Hachimi pipelines.
/// It deliberately does not decide task priority or state transitions.
/// </summary>
public interface IVisualPipelineRuntime
{
    Task<GrayImage?> CaptureGrayAsync(
        LastVerifiedConnection connection,
        CancellationToken cancellationToken = default);

    Task<GrayImage?> LoadTemplateAsync(
        string? templatePath,
        string baseDirectory,
        CancellationToken cancellationToken = default);

    Task<TemplateMatchResult?> WaitForMatchAsync(
        LastVerifiedConnection connection,
        string? templatePath,
        int[]? roi,
        double threshold,
        int referenceWidth,
        int referenceHeight,
        int timeoutMilliseconds,
        int pollIntervalMilliseconds,
        string taskName,
        string baseDirectory,
        CancellationToken cancellationToken = default);

    Task TapMatchAsync(
        LastVerifiedConnection connection,
        TemplateMatchResult match,
        string taskName,
        CancellationToken cancellationToken = default);

    Task SaveScreenshotAsync(
        LastVerifiedConnection connection,
        string definitionPath,
        string name,
        CancellationToken cancellationToken = default);

    Task DelayAsync(
        int milliseconds,
        CancellationToken cancellationToken = default);
}
