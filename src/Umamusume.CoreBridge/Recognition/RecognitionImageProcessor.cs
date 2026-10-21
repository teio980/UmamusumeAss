namespace Umamusume.CoreBridge;

public readonly record struct RecognitionImagePoint(int X, int Y);
public readonly record struct RecognitionImageDifference(ulong Sum, ulong Samples);

/// <summary>Native pixel processing for isolated glyphs and grayscale canvases.</summary>
public static class RecognitionImageProcessor
{
    public static RecognitionImage? PrepareNumericGlyphs(RecognitionImage image, bool warmGain,
        RecognitionColorRule seedRule) => UmaCoreBridgeNative.PrepareNumericGlyphs(image, warmGain, seedRule);

    public static RecognitionImage ComposeGray(RecognitionImage[] images, RecognitionImageRect[] placements,
        int width, int height) => UmaCoreBridgeNative.ComposeGray(images, placements, width, height);

    public static int CountInkGroups(RecognitionImage image, int threshold = 128) =>
        UmaCoreBridgeNative.CountInkGroups(image, threshold);

    public static RecognitionImageDifference SampleDifference(RecognitionImage before, RecognitionImage after,
        RecognitionImagePoint[] points) => UmaCoreBridgeNative.SampleDifference(before, after, points);
}
