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
            scaleCandidates,
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
                    minimumScoreGap,
                    scaleCandidates);
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
        double minimumScoreGap,
        IReadOnlyList<double>? scaleCandidates)
    {
        if (searchRois is not { Count: > 0 })
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
                    referenceHeight);
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
                    referenceHeight))
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

        var recognized = await _textRecognizer.RecognizeAsync(
                screenshot,
                language,
                cancellationToken)
            .ConfigureAwait(false);
        var actualRoi = ScaleRoi(
            roi,
            referenceWidth,
            referenceHeight,
            screenshot.Width,
            screenshot.Height);
        if (actualRoi is null)
            return recognized;

        var filtered = recognized.Detections
            .Where(detection => IsInside(detection.Bounds, actualRoi))
            .ToArray();
        return recognized with { Detections = filtered };
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
            if (char.IsLetterOrDigit(character))
                builder.Append(character);
            else if (char.IsWhiteSpace(character))
                builder.Append(' ');
        }

        return string.Join(
            ' ',
            builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int ScaleCoordinate(int value, int reference, int actual) =>
        Math.Clamp(
            (int)Math.Round(value * (double)Math.Max(1, actual) / reference),
            0,
            Math.Max(0, actual - 1));
}
