namespace Umamusume.CoreBridge;

public sealed record RecognitionImage(
    int Width,
    int Height,
    byte[]? GrayPixels,
    byte[]? RgbaPixels = null,
    TemplatePreparation Preparation = TemplatePreparation.Original);

public enum TemplatePreparation { Original = 0, MaskedHead = 1, Portrait = 2 }

public sealed record RecognitionImageTransform(
    int CropX = 0, int CropY = 0, int CropWidth = 0, int CropHeight = 0,
    int TargetWidth = 0, int TargetHeight = 0, bool MeanGray = false, bool Nearest = false);

public enum TemplateMatchMethod : uint
{
    Gray = 0,
    Color = 1,
    Button = 2,
    Scaled = 3,
    ScaledMasked = 4,
    MaskedRegion = 5,
    ScreenshotCrop = 6,
    ColoredRegion = 7,
    ExhaustiveColor = 8,
}

public sealed record TemplateMatchOptions(
    TemplateMatchMethod Method,
    int[]? Roi,
    double Threshold,
    int ReferenceWidth,
    int ReferenceHeight,
    int CandidateStepOverride = 0,
    int CandidateStep = 8,
    int SampleWidth = 16,
    int SampleHeight = 16,
    bool FullSearchOnMiss = true,
    bool RequireTextContrast = false,
    IReadOnlyList<double>? Scales = null);

public sealed record TemplateMatchResult(
    bool Found,
    double Score,
    int X,
    int Y,
    int Width,
    int Height);

public sealed record TextRecognitionRect(int X, int Y, int Width, int Height);

public sealed record TextDetection(
    string Text,
    TextRecognitionRect Bounds,
    double Confidence = 1d);

public sealed record TextRecognitionResult(
    IReadOnlyList<TextDetection> Detections,
    string Language,
    int Width,
    int Height);
