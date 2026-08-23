using System.Runtime.InteropServices.WindowsRuntime;
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
    public async Task<ScreenTextRecognitionResult> RecognizeAsync(
        AdbRawScreenshot screenshot,
        string? language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(screenshot);
        cancellationToken.ThrowIfCancellationRequested();

        var selectedLanguage = ResolveLanguage(language);
        var engine = OcrEngine.TryCreateFromLanguage(new Language(selectedLanguage))
            ?? throw new InvalidOperationException(
                $"Windows OCR language '{selectedLanguage}' is not installed.");

        using var bitmap = new SoftwareBitmap(
            BitmapPixelFormat.Rgba8,
            screenshot.Width,
            screenshot.Height,
            BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(screenshot.RgbaBytes.AsBuffer());
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken)
            .ConfigureAwait(false);

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
