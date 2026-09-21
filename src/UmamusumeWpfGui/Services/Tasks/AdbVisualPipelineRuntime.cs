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
    private readonly IScreenTextRecognizer _textRecognizer;

    public AdbVisualPipelineRuntime(
        IAdbRuntime adbRuntime,
        IAsyncDelay asyncDelay,
        IScreenTextRecognizer textRecognizer)
    {
        ArgumentNullException.ThrowIfNull(adbRuntime);
        ArgumentNullException.ThrowIfNull(asyncDelay);
        ArgumentNullException.ThrowIfNull(textRecognizer);
        _adbRuntime = adbRuntime;
        _asyncDelay = asyncDelay;
        _textRecognizer = textRecognizer;
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
            cancellationToken: cancellationToken);

    public Task<TemplateMatchResult?> WaitForColorMatchAsync(
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
            scaleCandidates: null,
            useColorTemplate: true,
            cancellationToken: cancellationToken);

    public Task<TemplateMatchResult?> WaitForMatchScaledAsync(
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
            scaleCandidates: scaleCandidates,
            cancellationToken: cancellationToken);

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
            cancellationToken: cancellationToken);

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
        IReadOnlyList<double>? scaleCandidates = null,
        bool useColorTemplate = false,
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
                    taskName,
                    searchRois,
                    minimumScoreGap,
                    scaleCandidates,
                    useButtonTemplate: IsStructuralButtonTask(taskName),
                    useColorTemplate: useColorTemplate);
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

    private static bool IsStructuralButtonTask(string taskName) =>
        taskName.Equals(
            "independent_skills_reset_probe",
            StringComparison.OrdinalIgnoreCase)
        || taskName.Equals(
            "independent_skills_main_reset",
            StringComparison.OrdinalIgnoreCase)
        || taskName.Equals(
            "independent_skills_open",
            StringComparison.OrdinalIgnoreCase)
        || taskName.Equals(
            "independent_skills_post_confirm",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsMissionTabTask(string taskName) =>
        taskName.Equals("dailySelected", StringComparison.OrdinalIgnoreCase)
        || taskName.Equals("dailyUnselected", StringComparison.OrdinalIgnoreCase)
        || taskName.Equals("mainTab", StringComparison.OrdinalIgnoreCase)
        || taskName.Equals("titlesTab", StringComparison.OrdinalIgnoreCase)
        || taskName.Equals("specialTab", StringComparison.OrdinalIgnoreCase);

    private static TemplateMatchResult FindBestMatch(
        GrayImage screen,
        GrayImage template,
        int[]? roi,
        double threshold,
        int referenceWidth,
        int referenceHeight,
        string taskName,
        IReadOnlyList<int[]>? searchRois,
        double minimumScoreGap,
        IReadOnlyList<double>? scaleCandidates,
        bool useButtonTemplate = false,
        bool useColorTemplate = false)
    {
        if (searchRois is not { Count: > 0 })
        {
            if (useColorTemplate && scaleCandidates is not { Count: > 0 })
            {
                return TemplateMatcher.FindColor(
                    screen,
                    template,
                    roi,
                    threshold,
                    referenceWidth,
                    referenceHeight);
            }

            if (useButtonTemplate && scaleCandidates is not { Count: > 0 })
            {
                return TemplateMatcher.FindButton(
                    screen,
                    template,
                    roi,
                    threshold,
                    referenceWidth,
                    referenceHeight);
            }

            return scaleCandidates is { Count: > 0 }
                ? TemplateMatcher.FindScaled(
                    screen,
                    template,
                    roi,
                    threshold,
                    referenceWidth,
                    referenceHeight,
                    scaleCandidates)
                : TemplateMatcher.Find(
                    screen,
                    template,
                    roi,
                    threshold,
                    referenceWidth,
                    referenceHeight,
                    candidateStepOverride: IsMissionTabTask(taskName) ? 1 : null);
        }

        var candidates = searchRois
            .Where(candidate => candidate is { Length: >= 4 })
            .Select(candidate => scaleCandidates is { Count: > 0 }
                ? TemplateMatcher.FindScaled(
                    screen,
                    template,
                    candidate,
                    threshold: 0,
                    referenceWidth,
                    referenceHeight,
                    scaleCandidates)
                : TemplateMatcher.Find(
                    screen,
                    template,
                    candidate,
                    threshold: 0,
                    referenceWidth,
                    referenceHeight,
                    candidateStepOverride: IsMissionTabTask(taskName) ? 1 : null))
            .OrderByDescending(candidate => candidate.Score)
            .ToArray();
        if (candidates.Length == 0)
        {
            return scaleCandidates is { Count: > 0 }
                ? TemplateMatcher.FindScaled(
                    screen,
                    template,
                    roi,
                    threshold,
                    referenceWidth,
                    referenceHeight,
                    scaleCandidates)
                : TemplateMatcher.Find(
                    screen,
                    template,
                    roi,
                    threshold,
                    referenceWidth,
                    referenceHeight,
                    candidateStepOverride: IsMissionTabTask(taskName) ? 1 : null);
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

    public async Task<ScreenTextRecognitionResult?> DetectTextAsync(
        LastVerifiedConnection connection,
        int[]? roi,
        int referenceWidth,
        int referenceHeight,
        string? language,
        string taskName,
        CancellationToken cancellationToken = default)
    {
        var screenshot = await CaptureRawScreenshotAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (screenshot is null)
            return null;

        var actualRoi = ScaleRoi(
            roi,
            referenceWidth,
            referenceHeight,
            screenshot.Width,
            screenshot.Height);
        // Keep the existing full-screen OCR behavior for Hachimi tasks. The
        // Career turn label is the one deliberately isolated OCR signal whose
        // background is known to vary, so only that task gets ROI cropping
        // before recognition.
        var cropToRoi = actualRoi is not null
            && taskName.Equals("career_main.turn_position", StringComparison.OrdinalIgnoreCase);
        var ocrScreenshot = cropToRoi
            ? CropScreenshot(screenshot, actualRoi!, padding: 8)
            : screenshot;
        var recognized = await _textRecognizer.RecognizeAsync(
                ocrScreenshot,
                language,
                cancellationToken)
            .ConfigureAwait(false);
        if (actualRoi is null)
            return recognized;

        if (!cropToRoi)
        {
            var filteredInRoi = recognized.Detections
                .Where(detection => IsInside(detection.Bounds, actualRoi))
                .ToArray();
            return recognized with { Detections = filteredInRoi };
        }

        var cropOffset = GetCropOffset(screenshot, actualRoi, padding: 8);
        var restored = recognized.Detections
            .Select(detection => detection with
            {
                Bounds = new ScreenTextRect(
                    detection.Bounds.X + cropOffset.X,
                    detection.Bounds.Y + cropOffset.Y,
                    detection.Bounds.Width,
                    detection.Bounds.Height),
            });
        var filtered = restored
            .Where(detection => IsInside(detection.Bounds, actualRoi))
            .ToArray();
        return recognized with
        {
            Detections = filtered,
            Width = screenshot.Width,
            Height = screenshot.Height,
        };
    }

    public async Task<ScreenTextQueryResult?> FindTextAsync(
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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetText))
        {
            return new ScreenTextQueryResult(
                false,
                targetText,
                [],
                null,
                false,
                "OCR targetText is empty.");
        }

        var recognized = await DetectTextAsync(
                connection,
                roi,
                referenceWidth,
                referenceHeight,
                language,
                taskName,
                cancellationToken)
            .ConfigureAwait(false);
        if (recognized is null)
            return null;

        var threshold = Math.Clamp(
            double.IsFinite(fuzzyThreshold) ? fuzzyThreshold : 0.86,
            0,
            1);
        var useTokenCoverage = matchMode?.Trim().Equals(
            "tokenCoverage",
            StringComparison.OrdinalIgnoreCase) == true;
        var candidates = useTokenCoverage
            ? GroupDetections(
                recognized,
                referenceHeight,
                groupRowHeight,
                rowGap)
            : recognized.Detections.Select(detection =>
                new OcrMatchText(
                    detection.Text,
                    detection.Bounds,
                    Math.Clamp(detection.Confidence, 0, 1)));
        var allCandidates = candidates
            .Select(candidate => new ScreenTextCandidate(
                candidate.Text,
                candidate.Bounds,
                candidate.Confidence,
                useTokenCoverage
                    ? TokenCoverageSimilarity(
                        targetText,
                        candidate.Text,
                        requireAllTokens)
                    : TextSimilarity(targetText, candidate.Text)))
            .OrderByDescending(candidate => candidate.Similarity)
            .ThenBy(candidate => candidate.Bounds.Y)
            .ThenBy(candidate => candidate.Bounds.X)
            .ToArray();
        var matchingCandidates = allCandidates
            .Where(candidate => candidate.Similarity >= threshold)
            .ToArray();
        var ambiguous = unique && matchingCandidates.Length > 1;
        var match = !ambiguous ? matchingCandidates.FirstOrDefault() : null;
        return new ScreenTextQueryResult(
            match is not null,
            targetText,
            allCandidates,
            match,
            ambiguous,
            ambiguous
                ? $"OCR target '{targetText}' matched {matchingCandidates.Length} candidates."
                : null);
    }

    public async Task<ScreenTextQueryResult?> WaitForTextAsync(
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
        CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(
            timeoutMilliseconds,
            0,
            10 * 60 * 1000));
        var poll = Math.Clamp(pollIntervalMilliseconds, 50, 10_000);
        var started = Stopwatch.GetTimestamp();
        ScreenTextQueryResult? latest = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            latest = await FindTextAsync(
                    connection,
                    targetText,
                    roi,
                    fuzzyThreshold,
                    unique,
                    referenceWidth,
                    referenceHeight,
                    language,
                    taskName,
                    matchMode,
                    groupRowHeight,
                    rowGap,
                    requireAllTokens,
                    cancellationToken)
                .ConfigureAwait(false);
            if (latest?.Found == true || latest?.Ambiguous == true)
                return latest;
            if (Stopwatch.GetElapsedTime(started) >= timeout)
                return latest;
            await DelayAsync(poll, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task TapTextAsync(
        LastVerifiedConnection connection,
        ScreenTextCandidate match,
        int[]? clickOffset,
        int[]? rowExpansion,
        string taskName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(match);
        var bounds = match.Bounds.Expand(rowExpansion);
        var offsetX = clickOffset is { Length: >= 2 } ? clickOffset[0] : 0;
        var offsetY = clickOffset is { Length: >= 2 } ? clickOffset[1] : 0;
        var x = Math.Clamp(bounds.CenterX + offsetX, 0, Math.Max(0, connection.Width - 1));
        var y = Math.Clamp(bounds.CenterY + offsetY, 0, Math.Max(0, connection.Height - 1));
        var result = await _adbRuntime.TapAsync(
                connection.AdbPath,
                connection.Serial,
                x,
                y,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (result.Error is not null || result.TimedOut || result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ADB OCR tap failed for '{taskName}': {result.Stderr}");
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

    public async Task<HsvColorProbeResult?> ProbeHsvAsync(
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var offsetX = offsetReference is { Length: >= 1 }
            ? offsetReference[0]
            : 0;
        var offsetY = offsetReference is { Length: >= 2 }
            ? offsetReference[1]
            : 0;
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(
            timeoutMilliseconds,
            0,
            60_000));
        var poll = Math.Clamp(pollIntervalMilliseconds, 50, 5_000);
        var minimumRatio = Math.Clamp(
            double.IsFinite(minimumMatchRatio) ? minimumMatchRatio : 0,
            0,
            1);
        var normalizedHueMin = NormalizeHue(hueMin);
        var normalizedHueMax = NormalizeHue(hueMax);
        var normalizedSaturationMin = Math.Clamp(
            double.IsFinite(saturationMin) ? saturationMin : 0,
            0,
            1);
        var normalizedSaturationMax = Math.Clamp(
            double.IsFinite(saturationMax) ? saturationMax : 1,
            0,
            1);
        var normalizedValueMin = Math.Clamp(
            double.IsFinite(valueMin) ? valueMin : 0,
            0,
            1);
        var normalizedValueMax = Math.Clamp(
            double.IsFinite(valueMax) ? valueMax : 1,
            0,
            1);
        var started = Stopwatch.GetTimestamp();
        HsvColorProbeResult? latest = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var screenshot = await CaptureRawScreenshotAsync(
                    connection,
                    cancellationToken)
                .ConfigureAwait(false);
            if (screenshot is not null)
            {
                latest = MeasureHsvRegion(
                    screenshot,
                    centerXReference + offsetX,
                    centerYReference + offsetY,
                    radiusReference,
                    referenceWidth,
                    referenceHeight,
                    normalizedHueMin,
                    normalizedHueMax,
                    normalizedSaturationMin,
                    normalizedSaturationMax,
                    normalizedValueMin,
                    normalizedValueMax,
                    minimumRatio);
            }

            if (latest?.Matched == true
                || timeout <= TimeSpan.Zero
                || Stopwatch.GetElapsedTime(started) >= timeout)
            {
                return latest;
            }

            await DelayAsync(poll, cancellationToken).ConfigureAwait(false);
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
            : HachimiResourcePaths.GetDebugDirectory("pipeline");
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

    private async Task<AdbRawScreenshot?> CaptureRawScreenshotAsync(
        LastVerifiedConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var raw = await _adbRuntime.DecodeRawScreenshotAsync(
                connection.AdbPath,
                connection.Serial,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return raw.Value;
    }

    private static HsvColorProbeResult MeasureHsvRegion(
        AdbRawScreenshot screenshot,
        int centerXReference,
        int centerYReference,
        int radiusReference,
        int referenceWidth,
        int referenceHeight,
        double hueMin,
        double hueMax,
        double saturationMin,
        double saturationMax,
        double valueMin,
        double valueMax,
        double minimumMatchRatio)
    {
        var centerX = ScaleCoordinate(
            centerXReference,
            Math.Max(1, referenceWidth),
            screenshot.Width);
        var centerY = ScaleCoordinate(
            centerYReference,
            Math.Max(1, referenceHeight),
            screenshot.Height);
        var radiusX = ScaleLength(
            Math.Max(0, radiusReference),
            Math.Max(1, referenceWidth),
            screenshot.Width);
        var radiusY = ScaleLength(
            Math.Max(0, radiusReference),
            Math.Max(1, referenceHeight),
            screenshot.Height);
        var left = Math.Max(0, centerX - radiusX);
        var right = Math.Min(
            Math.Max(0, screenshot.Width - 1),
            centerX + radiusX);
        var top = Math.Max(0, centerY - radiusY);
        var bottom = Math.Min(
            Math.Max(0, screenshot.Height - 1),
            centerY + radiusY);
        var sampledPixels = 0;
        var matchingPixels = 0;
        var bytes = screenshot.RgbaBytes;
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var index = ((long)y * screenshot.Width + x) * 4;
                if (index < 0 || index + 2 >= bytes.Length)
                    continue;

                sampledPixels++;
                var red = bytes[(int)index] / 255d;
                var green = bytes[(int)index + 1] / 255d;
                var blue = bytes[(int)index + 2] / 255d;
                RgbToHsv(red, green, blue, out var hue, out var saturation, out var value);
                if (IsHueInRange(hue, hueMin, hueMax)
                    && saturation >= saturationMin
                    && saturation <= saturationMax
                    && value >= valueMin
                    && value <= valueMax)
                {
                    matchingPixels++;
                }
            }
        }

        var ratio = sampledPixels == 0
            ? 0d
            : matchingPixels / (double)sampledPixels;
        return new HsvColorProbeResult(
            ratio >= minimumMatchRatio,
            ratio,
            matchingPixels,
            sampledPixels,
            centerX,
            centerY,
            Math.Max(radiusX, radiusY));
    }

    private static void RgbToHsv(
        double red,
        double green,
        double blue,
        out double hue,
        out double saturation,
        out double value)
    {
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;
        value = maximum;
        saturation = maximum <= 0 ? 0 : delta / maximum;
        if (delta <= double.Epsilon)
        {
            hue = 0;
            return;
        }

        hue = maximum == red
            ? 60d * ((green - blue) / delta % 6d)
            : maximum == green
                ? 60d * ((blue - red) / delta + 2d)
                : 60d * ((red - green) / delta + 4d);
        hue = NormalizeHue(hue);
    }

    private static bool IsHueInRange(double hue, double minimum, double maximum) =>
        minimum <= maximum
            ? hue >= minimum && hue <= maximum
            : hue >= minimum || hue <= maximum;

    private static double NormalizeHue(double hue)
    {
        if (!double.IsFinite(hue))
            return 0;
        var normalized = hue % 360d;
        return normalized < 0 ? normalized + 360d : normalized;
    }

    private static int[]? ScaleRoi(
        int[]? roi,
        int referenceWidth,
        int referenceHeight,
        int actualWidth,
        int actualHeight)
    {
        if (roi is not { Length: >= 4 })
            return null;

        var referenceX = Math.Max(1, referenceWidth);
        var referenceY = Math.Max(1, referenceHeight);
        var left = ScaleCoordinate(roi[0], referenceX, actualWidth);
        var top = ScaleCoordinate(roi[1], referenceY, actualHeight);
        var right = ScaleCoordinate(roi[0] + Math.Max(0, roi[2]), referenceX, actualWidth);
        var bottom = ScaleCoordinate(roi[1] + Math.Max(0, roi[3]), referenceY, actualHeight);
        return [
            Math.Min(left, right),
            Math.Min(top, bottom),
            Math.Abs(right - left),
            Math.Abs(bottom - top)];
    }

    private static AdbRawScreenshot CropScreenshot(
        AdbRawScreenshot screenshot,
        int[] roi,
        int padding)
    {
        var offset = GetCropOffset(screenshot, roi, padding);
        var right = Math.Min(
            screenshot.Width,
            Math.Max(offset.X + 1, roi[0] + roi[2] + Math.Max(0, padding)));
        var bottom = Math.Min(
            screenshot.Height,
            Math.Max(offset.Y + 1, roi[1] + roi[3] + Math.Max(0, padding)));
        var width = Math.Max(1, right - offset.X);
        var height = Math.Max(1, bottom - offset.Y);
        var rgba = new byte[checked(width * height * 4)];

        for (var row = 0; row < height; row++)
        {
            var sourceOffset = checked(((offset.Y + row) * screenshot.Width + offset.X) * 4);
            var targetOffset = checked(row * width * 4);
            Buffer.BlockCopy(
                screenshot.RgbaBytes,
                sourceOffset,
                rgba,
                targetOffset,
                width * 4);
        }

        return new AdbRawScreenshot(width, height, rgba);
    }

    private static (int X, int Y) GetCropOffset(
        AdbRawScreenshot screenshot,
        int[] roi,
        int padding)
    {
        var left = Math.Max(0, roi[0] - Math.Max(0, padding));
        var top = Math.Max(0, roi[1] - Math.Max(0, padding));
        return (
            Math.Min(left, Math.Max(0, screenshot.Width - 1)),
            Math.Min(top, Math.Max(0, screenshot.Height - 1)));
    }

    private static bool IsInside(ScreenTextRect bounds, int[] roi) =>
        bounds.CenterX >= roi[0]
        && bounds.CenterX <= roi[0] + roi[2]
        && bounds.CenterY >= roi[1]
        && bounds.CenterY <= roi[1] + roi[3];

    private static OcrMatchText[] GroupDetections(
        ScreenTextRecognitionResult recognized,
        int referenceHeight,
        int groupRowHeight,
        int rowGap)
    {
        var detections = recognized.Detections
            .Where(detection => !string.IsNullOrWhiteSpace(detection.Text))
            .OrderBy(detection => detection.Bounds.Y)
            .ThenBy(detection => detection.Bounds.X)
            .ToArray();
        if (detections.Length == 0)
            return [];

        // JSON geometry is authored in the same reference coordinate system
        // as ROI/tap geometry. Scale it to the actual screenshot before
        // grouping, so the policy remains valid on non-900x1600 captures.
        var actualHeight = recognized.Height > 0
            ? recognized.Height
            : Math.Max(1, referenceHeight);
        var scaledRowHeight = ScaleLength(
            Math.Max(0, groupRowHeight),
            Math.Max(1, referenceHeight),
            actualHeight);
        var scaledRowGap = ScaleLength(
            Math.Max(0, rowGap),
            Math.Max(1, referenceHeight),
            actualHeight);
        if (scaledRowHeight <= 0)
            scaledRowHeight = Math.Max(1, scaledRowGap);
        if (scaledRowGap <= 0)
            scaledRowGap = scaledRowHeight;

        var groups = new List<List<ScreenTextDetection>>();
        foreach (var detection in detections)
        {
            var group = groups.LastOrDefault();
            if (group is null)
            {
                groups.Add([detection]);
                continue;
            }

            var groupTop = group.Min(item => item.Bounds.Y);
            var groupBottom = group.Max(item => item.Bounds.Bottom);
            var projectedHeight = Math.Max(groupBottom, detection.Bounds.Bottom) - groupTop;
            var verticalGap = detection.Bounds.Y > groupBottom
                ? detection.Bounds.Y - groupBottom
                : 0;
            if (projectedHeight <= scaledRowHeight
                && verticalGap <= scaledRowGap)
            {
                group.Add(detection);
            }
            else
            {
                groups.Add([detection]);
            }
        }

        return groups
            .Select(group =>
            {
                var ordered = group
                    .OrderBy(item => item.Bounds.Y)
                    .ThenBy(item => item.Bounds.X)
                    .ToArray();
                var left = ordered.Min(item => item.Bounds.X);
                var top = ordered.Min(item => item.Bounds.Y);
                var right = ordered.Max(item => item.Bounds.Right);
                var bottom = ordered.Max(item => item.Bounds.Bottom);
                return new OcrMatchText(
                    string.Join(
                        ' ',
                        ordered.Select(item => item.Text.Trim())
                            .Where(text => text.Length > 0)),
                    new ScreenTextRect(left, top, right - left, bottom - top),
                    ordered.Average(item => Math.Clamp(item.Confidence, 0, 1)));
            })
            .Where(group => group.Text.Length > 0)
            .ToArray();
    }

    private static int ScaleLength(int value, int reference, int actual) =>
        value <= 0
            ? 0
            : Math.Max(
                1,
                (int)Math.Round(value * (double)Math.Max(1, actual)
                    / Math.Max(1, reference)));

    private static double TokenCoverageSimilarity(
        string expected,
        string actual,
        bool requireAllTokens)
    {
        var expectedTokens = Tokenize(expected);
        var actualTokens = Tokenize(actual);
        if (expectedTokens.Length == 0 || actualTokens.Length == 0)
            return 0;

        var scores = expectedTokens
            .Select(expectedToken => actualTokens
                .Select(actualToken => TokenSimilarity(expectedToken, actualToken))
                .DefaultIfEmpty(0)
                .Max())
            .ToArray();
        if (requireAllTokens && scores.Any(score => score < 0.60d))
            return 0;
        return scores.Average();
    }

    private static string[] Tokenize(string value) =>
        NormalizeText(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static double TokenSimilarity(string expected, string actual)
    {
        if (expected.Equals(actual, StringComparison.Ordinal))
            return 1d;

        var previous = Enumerable.Range(0, actual.Length + 1).ToArray();
        for (var i = 1; i <= expected.Length; i++)
        {
            var current = new int[actual.Length + 1];
            current[0] = i;
            for (var j = 1; j <= actual.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1]
                        + (expected[i - 1] == actual[j - 1] ? 0 : 1));
            }

            previous = current;
        }

        return 1d - previous[^1]
            / (double)Math.Max(expected.Length, actual.Length);
    }

    private sealed record OcrMatchText(
        string Text,
        ScreenTextRect Bounds,
        double Confidence);

    private static double TextSimilarity(string expected, string actual)
    {
        var left = NormalizeText(expected);
        var right = NormalizeText(actual);
        if (left.Length == 0 || right.Length == 0)
            return 0;
        if (left.Equals(right, StringComparison.Ordinal))
            return 1;
        if (right.Contains(left, StringComparison.Ordinal)
            || left.Contains(right, StringComparison.Ordinal))
        {
            return Math.Min(0.99d, Math.Min(left.Length, right.Length)
                / (double)Math.Max(left.Length, right.Length));
        }

        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1];
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            }
            previous = current;
        }

        return 1d - previous[^1] / (double)Math.Max(left.Length, right.Length);
    }

    private static string NormalizeText(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            // Game skill names use circle/star/cross glyphs to distinguish
            // variants such as Right-Handed ◎ and Right-Handed ○.  Keeping
            // those markers as words prevents OCR from treating the variants
            // as the same candidate while still allowing ASCII adb queries.
            switch (character)
            {
                case '◎':
                    builder.Append(" doublecircle ");
                    break;
                case '○':
                case '◯':
                case '〇':
                    builder.Append(" circle ");
                    break;
                case '☆':
                case '★':
                    builder.Append(" star ");
                    break;
                case '×':
                case '✕':
                    builder.Append(" x ");
                    break;
                case '♡':
                case '♥':
                    builder.Append(" heart ");
                    break;
                default:
                    if (char.IsLetterOrDigit(character))
                        builder.Append(character);
                    else if (char.IsWhiteSpace(character))
                        builder.Append(' ');
                    break;
            }
        }

        var tokens = builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token is "o" or "0" ? "circle" : token);
        return string.Join(' ', tokens);
    }

    private static int ScaleCoordinate(int value, int reference, int actual) =>
        Math.Clamp(
            (int)Math.Round(value * (double)Math.Max(1, actual) / reference),
            0,
            Math.Max(0, actual - 1));
}
