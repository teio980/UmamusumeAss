using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Offline OCR contract used by the shared ADB visual layer.  It accepts the
/// already decoded screenshot rather than reaching into ADB, keeping raw ADB
/// transport limited to capture and input primitives.
/// </summary>
public interface IScreenTextRecognizer
{
    Task<ScreenTextRecognitionResult> RecognizeAsync(
        AdbRawScreenshot screenshot,
        string? language,
        CancellationToken cancellationToken = default);
}
