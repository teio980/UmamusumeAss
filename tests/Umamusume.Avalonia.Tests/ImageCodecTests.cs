using SkiaSharp;
using Umamusume.AppCore.Services;
using Umamusume.Avalonia.Services;
using Xunit;

namespace Umamusume.Avalonia.Tests;

public sealed class ImageCodecTests
{
    [Fact]
    public void CropPreservesSourceCoordinatesAndPixels()
    {
        var screenshot = Fixture();
        var crop = DeveloperToolsImageCodec.CropScreenshot(screenshot, 1, 1, 2, 2);
        Assert.Equal(2, crop.Width);
        Assert.Equal(2, crop.Height);
        Assert.Equal(screenshot.CapturedAt, crop.CapturedAt);
        using var decoded = SKBitmap.Decode(crop.PngBytes);
        Assert.Equal(new SKColor(60, 60, 100), decoded.GetPixel(0, 0));
        Assert.Equal(new SKColor(120, 120, 100), decoded.GetPixel(1, 1));
    }

    [Fact]
    public void SystemReferenceCropIsEncodedAsLosslessWebp()
    {
        var bytes = DeveloperToolsImageCodec.EncodeScreenshotCrop(Fixture(), 1, 1, 2, 2, ".webp");
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        using var decoded = SKBitmap.Decode(bytes);
        Assert.Equal(new SKColor(60, 60, 100), decoded.GetPixel(0, 0));
        Assert.Equal(new SKColor(120, 120, 100), decoded.GetPixel(1, 1));
    }

    [Fact]
    public void PipelineCropUsesPngEncoding()
    {
        var bytes = DeveloperToolsImageCodec.EncodeScreenshotCrop(Fixture(), 0, 0, 3, 2, ".png");
        Assert.Equal(new byte[] {137, 80, 78, 71, 13, 10, 26, 10}, bytes[..8]);
        using var decoded = SKBitmap.Decode(bytes);
        Assert.Equal(3, decoded.Width);
        Assert.Equal(2, decoded.Height);
    }

    private static CapturedScreenshot Fixture()
    {
        using var bitmap = new SKBitmap(4, 4, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++) bitmap.SetPixel(x, y, new SKColor((byte)(x * 60), (byte)(y * 60), 100));
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return new(png.ToArray(), null, 4, 4, new DateTime(2026, 10, 20, 12, 0, 0));
    }
}
