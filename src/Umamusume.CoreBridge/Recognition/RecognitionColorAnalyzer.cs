namespace Umamusume.CoreBridge;

public sealed record RecognitionColorRule(
    int MinimumRed = 0, int MinimumGreen = 0, int MinimumBlue = 0,
    int MaximumRed = 255, int MaximumGreen = 255, int MaximumBlue = 255,
    int MinimumRedGreen = -255, int MaximumRedGreen = 255,
    int MinimumGreenBlue = -255, int MaximumGreenBlue = 255,
    bool UseHsv = false, int SampleStep = 1,
    double HueMin = 0, double HueMax = 360, double SaturationMin = 0, double SaturationMax = 1,
    double ValueMin = 0, double ValueMax = 1);

public readonly record struct RecognitionImageRect(int X, int Y, int Width, int Height);
public readonly record struct RecognitionColorBlob(int Area, RecognitionImageRect Bounds);
public readonly record struct RecognitionColorCount(ulong Matching, ulong Sampled);

/// <summary>Pure native color analysis of owned pixels, without a device operation.</summary>
public static class RecognitionColorAnalyzer
{
    public static RecognitionImage Binarize(RecognitionImage image, RecognitionColorRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        int result = UmaCoreBridgeNative.BinarizeColor(image, rule, out var decoded);
        return RecognitionImageDecoder.CopyToManaged(result, decoded)
            ?? throw new ManagedBridgeException($"UmaBinarizeColor failed with native error code {result}.");
    }

    public static IReadOnlyList<RecognitionColorBlob> FindBlobs(RecognitionImage image,
        RecognitionColorRule rule, RecognitionImageRect region) =>
        UmaCoreBridgeNative.FindColorBlobs(image, rule, region);

    public static RecognitionColorCount Count(RecognitionImage image, RecognitionColorRule rule,
        RecognitionImageRect region, RecognitionImageRect? exclude = null) =>
        UmaCoreBridgeNative.CountColor(image, rule, region, exclude);
}
