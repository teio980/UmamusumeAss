using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class GrayImageCodecTests
{
    [Theory]
    [InlineData("png")]
    [InlineData("bmp")]
    [InlineData("jpeg")]
    [InlineData("tiff")]
    [InlineData("gif")]
    public void NativeDecoderKeepsLegacyWpfPixelPlanes(string format)
    {
        byte[] bgra = [0, 0, 255, 128, 0, 255, 0, 17, 255, 0, 0, 255,
            255, 255, 255, 0, 87, 63, 21, 200, 20, 80, 160, 255];
        var bitmap = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Bgra32, null, bgra, 12);
        BitmapEncoder encoder = format switch
        {
            "bmp" => new BmpBitmapEncoder(),
            "jpeg" => new JpegBitmapEncoder(),
            "tiff" => new TiffBitmapEncoder(),
            "gif" => new GifBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        AssertLegacyPixels(stream.ToArray());
    }

    [Theory]
    [InlineData("resource/hachimi/ura/screens/captures/rest_confirmation.png")]
    [InlineData("resource/hachimi/ura/screens/captures/support_autofill_prompt_ura.png")]
    [InlineData("resource/hachimi/ura/screens/captures/support_event_choice_ura.png")]
    [InlineData("resource/uma/assets/images/global/trainees/100601.webp")]
    public void NativeFileDecoderKeepsShippedResourcePixels(string relativePath)
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var path = relativePath.Contains("/captures/", StringComparison.Ordinal)
            ? CareerTestResourceResolver.FindUraCapture(root, Path.GetFileName(relativePath))
            : Path.Combine(root, relativePath);
        byte[] encoded = File.ReadAllBytes(path);
        AssertLegacyPixels(encoded);
        GrayImage image = Assert.IsType<GrayImage>(GrayImageCodec.FromFile(path));
        GrayImage memory = Assert.IsType<GrayImage>(GrayImageCodec.FromScreenshot(
            new AdbScreenshotResult(AdbScreenshotMethod.EncodedPng, encoded, TimeSpan.Zero, null)));
        Assert.Equal(memory.Pixels, image.Pixels);
        Assert.Equal(memory.RgbaPixels, image.RgbaPixels);
    }

    [Fact]
    public void IndexedPngKeepsPaletteColorsAndTransparency()
    {
        var palette = new BitmapPalette([
            Color.FromArgb(128, 255, 0, 0), Color.FromArgb(17, 0, 255, 0),
            Color.FromArgb(255, 0, 0, 255), Color.FromArgb(0, 255, 255, 255),
        ]);
        var bitmap = BitmapSource.Create(4, 1, 96, 96, PixelFormats.Indexed8,
            palette, new byte[] { 0, 1, 2, 3 }, 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        AssertLegacyPixels(stream.ToArray());
    }

    [Fact]
    public void RawPixelsRetainFrameProvenanceAndInvalidDataCannotBecomeAnImage()
    {
        var metadata = new DeviceFrameMetadata(7, 11, 13, 3, 1, 0, 0,
            new DeviceDisplayTransform(3, 1, 0, 3, 1, 1, 0, 0, 3, 1));
        var source = new FrameProvenance(metadata);
        byte[] rgba = [255, 0, 0, 128, 0, 255, 0, 17, 0, 0, 255, 255];
        var screenshot = new AdbScreenshotResult(AdbScreenshotMethod.Raw, [], TimeSpan.Zero,
            new AdbRawScreenshot(3, 1, rgba)) { SourceFrame = source };

        GrayImage image = Assert.IsType<GrayImage>(GrayImageCodec.FromScreenshot(screenshot));
        Assert.Equal(new byte[] { 76, 149, 29 }, image.Pixels);
        Assert.Equal(rgba, image.RgbaPixels);
        Assert.Same(source, image.SourceFrame);
        Assert.Null(GrayImageCodec.FromScreenshot(screenshot with
        {
            DecodedRaw = new AdbRawScreenshot(3, 1, [0]),
        }));
        Assert.Null(GrayImageCodec.FromScreenshot(new AdbScreenshotResult(
            AdbScreenshotMethod.EncodedPng, [0, 1, 2], TimeSpan.Zero, null)));
    }

    private static void AssertLegacyPixels(byte[] encoded)
    {
        using var stream = new MemoryStream(encoded, writable: false);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var bgra = new byte[checked(converted.PixelWidth * converted.PixelHeight * 4)];
        converted.CopyPixels(bgra, converted.PixelWidth * 4, 0);
        var expectedGray = new byte[bgra.Length / 4];
        var expectedRgba = new byte[bgra.Length];
        for (int index = 0; index < expectedGray.Length; index++)
        {
            int offset = index * 4;
            expectedRgba[offset] = bgra[offset + 2];
            expectedRgba[offset + 1] = bgra[offset + 1];
            expectedRgba[offset + 2] = bgra[offset];
            expectedRgba[offset + 3] = bgra[offset + 3];
            expectedGray[index] = (byte)((bgra[offset + 2] * 299 + bgra[offset + 1] * 587
                + bgra[offset] * 114) / 1000);
        }
        GrayImage image = Assert.IsType<GrayImage>(GrayImageCodec.FromScreenshot(
            new AdbScreenshotResult(AdbScreenshotMethod.EncodedPng, encoded, TimeSpan.Zero, null)));
        Assert.Equal(converted.PixelWidth, image.Width);
        Assert.Equal(converted.PixelHeight, image.Height);
        Assert.Equal(expectedGray, image.Pixels);
        Assert.Equal(expectedRgba, image.RgbaPixels);
    }
}
