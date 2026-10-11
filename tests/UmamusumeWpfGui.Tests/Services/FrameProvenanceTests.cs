using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class FrameProvenanceTests
{
    [Fact]
    public void CropAndResizeComposeTheSourcePixelTransform()
    {
        var source = Provenance(100, 50, 100, 50, orientation: 0);

        var derived = source.Crop(10, 5).Resize(80, 40, 40, 20);

        Assert.Equal((16, 13), derived.MapToCanonical(3, 4));
    }

    [Fact]
    public void QuarterTurnMapsRawCapturePixelsIntoCanonicalCoordinates()
    {
        var source = Provenance(1920, 1080, 1080, 1920, orientation: 1);

        Assert.Equal((200, 1819), source.MapToCanonical(100, 200));
    }

    [Fact]
    public void InvalidDisplayScaleIsRejectedBeforeProducingAnInputPoint()
    {
        var source = Provenance(100, 50, 100, 50, orientation: 0, scale: 0);

        Assert.Throws<InvalidOperationException>(() => source.MapToCanonical(10, 10));
    }

    private static FrameProvenance Provenance(
        int width,
        int height,
        int canonicalWidth,
        int canonicalHeight,
        int orientation,
        double scale = 1)
    {
        var transform = new DeviceDisplayTransform(
            canonicalWidth,
            canonicalHeight,
            orientation * 90,
            width,
            height,
            scale,
            0,
            0,
            width,
            height);
        var metadata = new DeviceFrameMetadata(
            7,
            11,
            13,
            width,
            height,
            orientation,
            0,
            transform);
        return new FrameProvenance(metadata);
    }
}
