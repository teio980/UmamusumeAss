using System.Diagnostics;
using System.IO;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Shared screenshot, template, timing and tap primitives.
/// This class contains no pipeline-specific branching logic.
/// </summary>
public sealed class AdbVisualPipelineRuntime : IVisualPipelineRuntime
{
    private readonly IAdbRuntime _adbRuntime;
    private readonly IAsyncDelay _asyncDelay;

    public AdbVisualPipelineRuntime(
        IAdbRuntime adbRuntime,
        IAsyncDelay asyncDelay)
    {
        ArgumentNullException.ThrowIfNull(adbRuntime);
        ArgumentNullException.ThrowIfNull(asyncDelay);
        _adbRuntime = adbRuntime;
        _asyncDelay = asyncDelay;
    }

    public async Task<GrayImage?> CaptureGrayAsync(
        LastVerifiedConnection connection,
        CancellationToken cancellationToken = default)
    {
        var screenshot = await CaptureScreenshotAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (screenshot is null)
            return null;
        return GrayImageCodec.FromScreenshot(screenshot);
    }

    public Task<GrayImage?> LoadTemplateAsync(
        string? templatePath,
        string baseDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(templatePath))
            return Task.FromResult<GrayImage?>(null);

        var fullPath = Path.IsPathRooted(templatePath)
            ? templatePath
            : Path.Combine(baseDirectory, templatePath);
        return Task.Run(
            () => GrayImageCodec.FromFile(fullPath),
            cancellationToken);
    }

    public Task<TemplateMatchResult?> WaitForMatchAsync(
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
        CancellationToken cancellationToken = default) =>
        WaitForMatchCoreAsync(
            connection,
            templatePath,
            roi,
            threshold,
            referenceWidth,
            referenceHeight,
            timeoutMilliseconds,
            pollIntervalMilliseconds,
            taskName,
            baseDirectory,
            searchRois: null,
            minimumScoreGap: 0,
            cancellationToken);

    public Task<TemplateMatchResult?> WaitForMatchInRoisAsync(
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
        CancellationToken cancellationToken = default) =>
        WaitForMatchCoreAsync(
            connection,
            templatePath,
            roi: null,
            threshold,
            referenceWidth,
            referenceHeight,
            timeoutMilliseconds,
            pollIntervalMilliseconds,
            taskName,
            baseDirectory,
            searchRois,
            minimumScoreGap,
            cancellationToken);

    private async Task<TemplateMatchResult?> WaitForMatchCoreAsync(
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
        IReadOnlyList<int[]>? searchRois,
        double minimumScoreGap,
        CancellationToken cancellationToken = default)
    {
        var template = await LoadTemplateAsync(
                templatePath,
                baseDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        if (template is null)
            throw new InvalidOperationException(
                $"Template for '{taskName}' could not be loaded.");

        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(
            timeoutMilliseconds,
            0,
            10 * 60 * 1000));
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(
            pollIntervalMilliseconds,
            50,
            10_000));
        var started = Stopwatch.GetTimestamp();
        TemplateMatchResult? bestMatch = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var screen = await CaptureGrayAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (screen is not null)
            {
                var match = FindBestMatch(
                    screen,
                    template,
                    roi,
                    threshold,
                    referenceWidth,
                    referenceHeight,
                    searchRois,
                    minimumScoreGap);
                if (bestMatch is null || match.Score > bestMatch.Score)
                    bestMatch = match;
                if (match.Found)
                    return match;
            }

            if (Stopwatch.GetElapsedTime(started) >= timeout)
                return bestMatch;

            await DelayAsync((int)poll.TotalMilliseconds, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static TemplateMatchResult FindBestMatch(
        GrayImage screen,
        GrayImage template,
        int[]? roi,
        double threshold,
        int referenceWidth,
        int referenceHeight,
        IReadOnlyList<int[]>? searchRois,
        double minimumScoreGap)
    {
        if (searchRois is not { Count: > 0 })
        {
            return TemplateMatcher.Find(
                screen,
                template,
                roi,
                threshold,
                referenceWidth,
                referenceHeight);
        }

        var candidates = searchRois
            .Where(candidate => candidate is { Length: >= 4 })
            .Select(candidate => TemplateMatcher.Find(
                screen,
                template,
                candidate,
                threshold: 0,
                referenceWidth,
                referenceHeight))
            .OrderByDescending(candidate => candidate.Score)
            .ToArray();
        if (candidates.Length == 0)
        {
            return TemplateMatcher.Find(
                screen,
                template,
                roi,
                threshold,
                referenceWidth,
                referenceHeight);
        }

        var best = candidates[0];
        var secondScore = candidates.Length > 1
            ? candidates[1].Score
            : double.MinValue;
        var gap = candidates.Length > 1
            ? best.Score - secondScore
            : double.PositiveInfinity;
        var found = best.Score >= Math.Clamp(threshold, 0, 1)
            && gap >= Math.Max(0, minimumScoreGap);
        return best with { Found = found };
    }

    public async Task TapMatchAsync(
        LastVerifiedConnection connection,
        TemplateMatchResult match,
        string taskName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var result = await _adbRuntime.TapAsync(
                connection.AdbPath,
                connection.Serial,
                match.CenterX,
                match.CenterY,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (result.Error is not null || result.TimedOut || result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ADB template tap failed for '{taskName}': {result.Stderr}");
        }
    }

    public async Task TapAsync(
        LastVerifiedConnection connection,
        int x,
        int y,
        int referenceWidth,
        int referenceHeight,
        string taskName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var scaledX = ScaleCoordinate(x, Math.Max(1, referenceWidth), connection.Width);
        var scaledY = ScaleCoordinate(y, Math.Max(1, referenceHeight), connection.Height);
        var result = await _adbRuntime.TapAsync(
                connection.AdbPath,
                connection.Serial,
                scaledX,
                scaledY,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (result.Error is not null || result.TimedOut || result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ADB coordinate tap failed for '{taskName}': {result.Stderr}");
        }
    }

    public async Task SwipeAsync(
        LastVerifiedConnection connection,
        int[] coordinates,
        int referenceWidth,
        int referenceHeight,
        string taskName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (coordinates is null || coordinates.Length < 5)
        {
            throw new InvalidOperationException(
                $"JSON swipe task '{taskName}' requires [startX,startY,endX,endY,durationMs].");
        }

        var width = Math.Max(1, referenceWidth);
        var height = Math.Max(1, referenceHeight);
        var startX = ScaleCoordinate(coordinates[0], width, connection.Width);
        var startY = ScaleCoordinate(coordinates[1], height, connection.Height);
        var endX = ScaleCoordinate(coordinates[2], width, connection.Width);
        var endY = ScaleCoordinate(coordinates[3], height, connection.Height);
        var duration = Math.Clamp(coordinates[4], 100, 3_000);

        var result = await _adbRuntime.SwipeAsync(
                connection.AdbPath,
                connection.Serial,
                startX,
                startY,
                endX,
                endY,
                duration,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (result.Error is not null || result.TimedOut || result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ADB swipe failed for '{taskName}': {result.Stderr}");
        }
    }

    public async Task SaveScreenshotAsync(
        LastVerifiedConnection connection,
        string definitionPath,
        string name,
        CancellationToken cancellationToken = default)
    {
        var screenshot = await CaptureScreenshotAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (screenshot is null)
            return;

        var directory = Directory.Exists(definitionPath)
            ? definitionPath
            : Path.Combine(
                Path.GetDirectoryName(definitionPath) ?? AppContext.BaseDirectory,
                "debug");
        var path = Path.Combine(directory, $"{name}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await Task.Run(
            () => GrayImageCodec.SaveScreenshot(screenshot, path),
            cancellationToken).ConfigureAwait(false);
    }

    public Task DelayAsync(
        int milliseconds,
        CancellationToken cancellationToken = default) =>
        _asyncDelay.DelayAsync(
            TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)),
            cancellationToken);

    private async Task<AdbScreenshotResult?> CaptureScreenshotAsync(
        LastVerifiedConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var raw = await _adbRuntime.DecodeRawScreenshotAsync(
                connection.AdbPath,
                connection.Serial,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return raw.Value is { } decoded
            ? new AdbScreenshotResult(AdbScreenshotMethod.Raw, [], TimeSpan.Zero, decoded)
            : null;
    }

    private static int ScaleCoordinate(int value, int reference, int actual) =>
        Math.Clamp(
            (int)Math.Round(value * (double)Math.Max(1, actual) / reference),
            0,
            Math.Max(0, actual - 1));
}
