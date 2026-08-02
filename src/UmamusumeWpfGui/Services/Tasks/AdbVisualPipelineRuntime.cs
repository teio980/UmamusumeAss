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

    public async Task<TemplateMatchResult?> WaitForMatchAsync(
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

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var screen = await CaptureGrayAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (screen is not null)
            {
                var match = TemplateMatcher.Find(
                    screen,
                    template,
                    roi,
                    threshold,
                    referenceWidth,
                    referenceHeight);
                if (match.Found)
                    return match;
            }

            if (Stopwatch.GetElapsedTime(started) >= timeout)
                return null;

            await DelayAsync((int)poll.TotalMilliseconds, cancellationToken)
                .ConfigureAwait(false);
        }
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
}
