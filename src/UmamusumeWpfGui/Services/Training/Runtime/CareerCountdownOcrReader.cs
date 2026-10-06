using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Runs OCR over the countdown card itself. The input is the current screen,
/// not a table of known career turns, so multi-digit values are handled by the
/// OCR engine without a turn-range assumption.
/// </summary>
internal static partial class CareerCountdownOcrReader
{
    private const int UpscaleFactor = 4;
    private static readonly TimeSpan FallbackTimeout = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan SuccessfulCacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly object CacheLock = new();
    private static CachedCountdown? _cachedCountdown;

    private sealed record CachedCountdown(
        string Fingerprint,
        int? TurnsToGoal,
        long CachedAtTimestamp);

    public static async Task<int?> TryReadAsync(
        IReadOnlyList<GrayImage> frames,
        int[]? roi,
        int referenceWidth,
        int referenceHeight,
        CancellationToken cancellationToken)
    {
        if (frames.Count == 0 || roi is not { Length: >= 4 })
            return null;

        var started = Stopwatch.GetTimestamp();
        using var fallbackTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        fallbackTimeout.CancelAfter(FallbackTimeout);

        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fallbackTimeout.IsCancellationRequested)
                break;

            var crop = CareerNumericOcrReader.Crop(
                frame,
                roi,
                referenceWidth,
                referenceHeight);
            if (crop is null)
                continue;

            var fingerprint = Fingerprint(crop);
            if (TryGetCachedResult(fingerprint, out var cachedResult))
            {
                Trace.WriteLine(
                    $"Career countdown OCR cache hit; elapsed="
                    + $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms, "
                    + $"result={cachedResult?.ToString(CultureInfo.InvariantCulture) ?? "unreadable"}.");
                return cachedResult;
            }

            var image = CareerNumericOcrReader.Upscale(crop, UpscaleFactor);
            var result = await CareerNumericOcrReader.TryReadImageAsync(
                    image,
                    ParseNumber,
                    fallbackTimeout.Token,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is not null)
                CacheResult(fingerprint, result);
            Trace.WriteLine(
                $"Career countdown OCR fallback completed; elapsed="
                + $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms, "
                + $"result={result?.ToString(CultureInfo.InvariantCulture) ?? "unreadable"}.");
            if (result is not null)
                return result;
        }

        return null;
    }

    private static int? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var direct = CareerGoalTextParser.ParseTurnsLeft(text);
        if (direct is not null)
            return direct;

        var match = NumberRegex().Match(text);
        return match.Success
            && int.TryParse(match.Value, out var value)
            ? value
            : null;
    }

    private static string Fingerprint(GrayImage image)
    {
        var pixels = image.RgbaPixels is { Length: >= 4 } rgba
            ? rgba
            : image.Pixels;
        return $"{image.Width}x{image.Height}:{Convert.ToHexString(SHA256.HashData(pixels))}";
    }

    private static bool TryGetCachedResult(string fingerprint, out int? result)
    {
        lock (CacheLock)
        {
            if (_cachedCountdown is { } cached
                && Stopwatch.GetElapsedTime(cached.CachedAtTimestamp) <= SuccessfulCacheLifetime
                && string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                result = cached.TurnsToGoal;
                return true;
            }
        }

        result = null;
        return false;
    }

    private static void CacheResult(string fingerprint, int? result)
    {
        lock (CacheLock)
        {
            _cachedCountdown = new CachedCountdown(
                fingerprint,
                result,
                Stopwatch.GetTimestamp());
        }
    }

    [GeneratedRegex(@"(?<!\d)\d+(?!\d)")]
    private static partial Regex NumberRegex();
}
