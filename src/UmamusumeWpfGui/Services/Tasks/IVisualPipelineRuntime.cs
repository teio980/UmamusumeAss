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

    Task<TemplateMatchResult?> WaitForMatchScaledAsync(
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
        IReadOnlyList<double> scaleCandidates,
        CancellationToken cancellationToken = default);

    Task<TemplateMatchResult?> WaitForMatchInRoisAsync(
        LastVerifiedConnection connection,
        string? templatePath,
        double threshold,
        int referenceWidth,
        int referenceHeight,
        int timeoutMilliseconds,
        int pollIntervalMilliseconds,
        string taskName,
        string baseDirectory,
        IReadOnlyList<int[]> searchRois,
        double minimumScoreGap,
        CancellationToken cancellationToken = default);

    Task<ScreenTextRecognitionResult?> DetectTextAsync(
        LastVerifiedConnection connection,
        int[]? roi,
        int referenceWidth,
        int referenceHeight,
        string? language,
        string taskName,
        CancellationToken cancellationToken = default);

    Task<ScreenTextQueryResult?> FindTextAsync(
        LastVerifiedConnection connection,
        string targetText,
        int[]? roi,
        double fuzzyThreshold,
        bool unique,
        int referenceWidth,
        int referenceHeight,
        string? language,
        string taskName,
        string? matchMode = null,
        int groupRowHeight = 0,
        int rowGap = 0,
        bool requireAllTokens = true,
        CancellationToken cancellationToken = default);

    Task<ScreenTextQueryResult?> WaitForTextAsync(
        LastVerifiedConnection connection,
        string targetText,
        int[]? roi,
        double fuzzyThreshold,
        bool unique,
        int referenceWidth,
        int referenceHeight,
        int timeoutMilliseconds,
        int pollIntervalMilliseconds,
        string? language,
        string taskName,
        string? matchMode = null,
        int groupRowHeight = 0,
        int rowGap = 0,
        bool requireAllTokens = true,
        CancellationToken cancellationToken = default);

    Task TapTextAsync(
        LastVerifiedConnection connection,
        ScreenTextCandidate match,
        int[]? clickOffset,
        int[]? rowExpansion,
        string taskName,
        CancellationToken cancellationToken = default);

    Task TapMatchAsync(
        LastVerifiedConnection connection,
        TemplateMatchResult match,
        string taskName,
        CancellationToken cancellationToken = default);

    Task TapAsync(
        LastVerifiedConnection connection,
        int x,
        int y,
        int referenceWidth,
        int referenceHeight,
        string taskName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Samples a reference-space square until its HSV match ratio reaches the
    /// requested threshold or the timeout expires.  The runtime only provides
    /// generic pixel evidence; pipeline JSON owns the state-specific bounds.
    /// </summary>
    Task<HsvColorProbeResult?> ProbeHsvAsync(
        LastVerifiedConnection connection,
        int centerXReference,
        int centerYReference,
        int[]? offsetReference,
        int radiusReference,
        int referenceWidth,
        int referenceHeight,
        double hueMin,
        double hueMax,
        double saturationMin,
        double saturationMax,
        double valueMin,
        double valueMax,
        double minimumMatchRatio,
        int timeoutMilliseconds,
        int pollIntervalMilliseconds,
        string taskName,
        CancellationToken cancellationToken = default);

    Task SwipeAsync(
        LastVerifiedConnection connection,
        int[] coordinates,
        int referenceWidth,
        int referenceHeight,
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
