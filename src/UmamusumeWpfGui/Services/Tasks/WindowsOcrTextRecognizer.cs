using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using UmamusumeWpfGui.Models;
using Windows.Graphics.Imaging;
using Windows.Globalization;
using Windows.Media.Ocr;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Windows' offline OCR engine.  The recognizer is intentionally kept next
/// to the template matcher in the ADB visual layer, and never asks Android
/// for an accessibility/UI hierarchy.  English (or the first installed OCR
/// language) is selected at runtime so a self-contained release does not need
/// a separate language process or network service.
/// </summary>
public sealed class WindowsOcrTextRecognizer : IScreenTextRecognizer
{
    private readonly ConcurrentDictionary<string, Lazy<OcrEngine>> _engines = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _recognitionLocks = new(
        StringComparer.OrdinalIgnoreCase);

    public async Task<ScreenTextRecognitionResult> RecognizeAsync(
        AdbRawScreenshot screenshot,
        string? language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(screenshot);
        cancellationToken.ThrowIfCancellationRequested();

        var selectedLanguage = ResolveLanguage(language);
        var engine = _engines.GetOrAdd(
            selectedLanguage,
            static language => new Lazy<OcrEngine>(
                () => OcrEngine.TryCreateFromLanguage(new Language(language))
                    ?? throw new InvalidOperationException(
                        $"Windows OCR language '{language}' is not installed."),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        var recognitionLock = _recognitionLocks.GetOrAdd(
            selectedLanguage,
            static _ => new SemaphoreSlim(1, 1));

        await recognitionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        OcrResult result;
        try
        {
            using var bitmap = new SoftwareBitmap(
                BitmapPixelFormat.Rgba8,
                screenshot.Width,
                screenshot.Height,
                BitmapAlphaMode.Ignore);
            bitmap.CopyFromBuffer(screenshot.RgbaBytes.AsBuffer());
            result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            recognitionLock.Release();
        }

        var detections = new List<ScreenTextDetection>();
        foreach (var line in result.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = line.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text) || line.Words.Count == 0)
                continue;

            var words = line.Words;
            var left = words.Min(word => (int)Math.Floor(word.BoundingRect.X));
            var top = words.Min(word => (int)Math.Floor(word.BoundingRect.Y));
            var right = words.Max(word =>
                (int)Math.Ceiling(word.BoundingRect.X + word.BoundingRect.Width));
            var bottom = words.Max(word =>
                (int)Math.Ceiling(word.BoundingRect.Y + word.BoundingRect.Height));
            var confidence = 1d;
            detections.Add(new ScreenTextDetection(
                text,
                new ScreenTextRect(
                    Math.Clamp(left, 0, screenshot.Width),
                    Math.Clamp(top, 0, screenshot.Height),
                    Math.Clamp(right - left, 0, screenshot.Width),
                    Math.Clamp(bottom - top, 0, screenshot.Height)),
                confidence));
        }

        return new ScreenTextRecognitionResult(
            detections,
            selectedLanguage,
            screenshot.Width,
            screenshot.Height);
    }

    private static string ResolveLanguage(string? requested)
    {
        var available = OcrEngine.AvailableRecognizerLanguages;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var requestedTag = requested.Trim();
            var exact = available.FirstOrDefault(language =>
                language.LanguageTag.Equals(requestedTag, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact.LanguageTag;

            var requestedBase = requestedTag.Split('-', StringSplitOptions.RemoveEmptyEntries)[0];
            var sameBase = available.FirstOrDefault(language =>
                language.LanguageTag.Split('-', StringSplitOptions.RemoveEmptyEntries)[0]
                    .Equals(requestedBase, StringComparison.OrdinalIgnoreCase));
            if (sameBase is not null)
                return sameBase.LanguageTag;
        }

        var english = available.FirstOrDefault(language =>
            language.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        return english?.LanguageTag
            ?? (available.Count > 0 ? available[0].LanguageTag : null)
            ?? requested?.Trim()
            ?? "en-US";
    }
}
