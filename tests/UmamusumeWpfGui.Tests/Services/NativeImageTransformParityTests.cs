using System.IO;
using System.Security.Cryptography;
using System.Windows;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class NativeImageTransformParityTests
{
    [Theory]
    [InlineData(3, 2, 7, 5)]
    [InlineData(47, 31, 19, 11)]
    [InlineData(257, 131, 128, 128)]
    [InlineData(13, 19, 160, 90)]
    [InlineData(128, 128, 128, 128)]
    [InlineData(83, 91, 128, 160)]
    public void NativeBicubicResizeKeepsLegacyPremultipliedPixels(int w, int h, int tw, int th)
    {
        var rgba = new byte[w * h * 4];
        new Random(1042).NextBytes(rgba);
        for (int i = 0; i < rgba.Length; i += 28) rgba[i + 3] = 0;
        using var reference = Image.LoadPixelData<Rgba32>(rgba, w, h);
        reference.Mutate(context => context.Resize(tw, th));
        var expected = new byte[tw * th * 4];
        reference.CopyPixelDataTo(expected);
        var actual = RecognitionImageDecoder.Transform(new RecognitionImage(w, h, null, rgba),
            new RecognitionImageTransform(TargetWidth: tw, TargetHeight: th));
        Assert.Equal(expected, actual.RgbaPixels);
    }

    [Theory]
    [InlineData("resource/uma/assets/images/global/trainees/100601.webp", TemplatePreparation.MaskedHead)]
    [InlineData("resource/uma/assets/images/global/trainees/100601.webp", TemplatePreparation.Portrait)]
    [InlineData("resource/uma/system_reference/100101.webp", TemplatePreparation.Portrait)]
    public void PreparedTemplatesKeepLegacyCropAndAlphaPixels(string relativePath, TemplatePreparation recipe)
    {
        var path = Path.Combine(CareerTestResourceResolver.FindWorkspaceRoot(), relativePath);
        using var reference = Image.Load<Rgba32>(path);
        var rgba = new byte[reference.Width * reference.Height * 4];
        reference.CopyPixelDataTo(rgba);
        var xs = new List<int>(); var ys = new List<int>(); int opaque = 0;
        for (int y = 0; y < reference.Height; y++)
            for (int x = 0; x < reference.Width; x++)
            {
                byte alpha = rgba[(y * reference.Width + x) * 4 + 3];
                if (alpha >= 240) opaque++;
                if (alpha >= 24) { xs.Add(x); ys.Add(y); }
            }
        if (recipe != TemplatePreparation.Portrait || opaque < reference.Width * reference.Height * .98)
        {
            int left = xs.Min(), right = xs.Max(), top = ys.Min(), bottom = ys.Max();
            if (recipe == TemplatePreparation.MaskedHead)
            {
                bottom = Math.Min(bottom, top + Math.Max(1, (int)Math.Round((bottom - top + 1) * .44)));
                var headXs = xs.Where((_, i) => ys[i] <= bottom).ToArray();
                left = headXs.Min(); right = headXs.Max();
            }
            int height = recipe == TemplatePreparation.MaskedHead
                ? bottom - top + 1 : Math.Max(1, (int)Math.Round((bottom - top + 1) * .78));
            reference.Mutate(context => context.Crop(new SixLabors.ImageSharp.Rectangle(left, top, right - left + 1, height))
                .Resize(128, recipe == TemplatePreparation.MaskedHead ? 128 : 160));
        }
        var expected = new byte[reference.Width * reference.Height * 4]; reference.CopyPixelDataTo(expected);
        var actual = Assert.IsType<GrayImage>(GrayImageCodec.LoadTemplate(path, recipe));
        Assert.Equal(reference.Width, actual.Width); Assert.Equal(reference.Height, actual.Height);
        Assert.Equal(expected, actual.RgbaPixels);
    }

    [Fact]
    public void CropResizeAndBinarizationPreserveCanonicalFrameCoordinates()
    {
        var metadata = new DeviceFrameMetadata(7, 11, 13, 40, 20, 0, 0,
            new DeviceDisplayTransform(40, 20, 0, 40, 20, 1, 0, 0, 40, 20));
        var image = new GrayImage(40, 20, new byte[800], Enumerable.Repeat((byte)255, 3200).ToArray())
            { SourceFrame = new FrameProvenance(metadata) };
        var crop = GrayImageCodec.CropAndResize(image, new Int32Rect(10, 4, 8, 6), 16, 12);
        var binary = GrayImageCodec.Binarize(crop, new RecognitionColorRule(MinimumRed: 250));
        Assert.Equal((12, 6), binary.SourceFrame!.MapToCanonical(4, 4));
        Assert.All(binary.Pixels, pixel => Assert.Equal(0, pixel));
    }

    [Fact]
    public void ColorBlobsUseEightNeighboursAndCountsExcludeTheInnerRectangle()
    {
        byte[] rgba = new byte[6 * 4 * 4];
        for (int i = 0; i < rgba.Length; i += 4) rgba[i + 3] = 255;
        foreach (int index in new[] { 1, 8, 17, 23 }) rgba[index * 4] = 255;
        var image = new RecognitionImage(6, 4, null, rgba);
        var rule = new RecognitionColorRule(MinimumRed: 250, MaximumGreen: 0, MaximumBlue: 0);
        var blobs = RecognitionColorAnalyzer.FindBlobs(image, rule, new(-10, -10, 50, 50));
        Assert.Equal(new RecognitionColorBlob(2, new(1, 0, 2, 2)), blobs[0]);
        Assert.Equal(new RecognitionColorBlob(2, new(5, 2, 1, 2)), blobs[1]);
        Assert.Equal(new RecognitionColorCount(2, 20), RecognitionColorAnalyzer.Count(image, rule,
            new(0, 0, 6, 4), new(1, 0, 2, 2)));
    }

    // Frozen outputs from the pre-migration C# preprocessor, decoded in the
    // same Windows environment. These guard glyph topology as well as bounds.
    [Theory]
    [InlineData("smart_runtime_20261006/wit-speed-five.png", 25, 965, 152, 65, true, 87, 61,
        "7632F3AC9C922259CBCB096E5AAA8EE22603E6061239F31018CE6FE5DAADE8FB")]
    [InlineData("smart_runtime_20261006/adb-guts-fifteen.png", 457, 965, 140, 65, true, 122, 61,
        "87C1D8EA56499A9B3C724930589EF0892A73F12A5F294A5A6807D5608FD7A21C")]
    [InlineData("smart_runtime_20261006/speed-1.png", 25, 965, 152, 65, true, 122, 62,
        "0A8CD1BF07A1983F63AA51AE0C1DAAD0A17D989039532B105337324CC79C3CD0")]
    [InlineData("smart_runtime_20261006/stamina-1.png", 25, 965, 152, 65, true, 172, 85,
        "E3E32265EE5671138ACD9F7F10B8C85D91E9FA75D99FBE948CEFE201FDAA0B3F")]
    [InlineData("training_selection_speed_failure28.png", 400, 1030, 110, 110, false, 136, 60,
        "5C5AB7A3B3DB5E6257ADCCD7641D56CFF3D74E21901E90C67E84EECDA37E0A76")]
    [InlineData("training_selection_stamina_summer_failure92.png", 400, 1030, 110, 110, false, 136, 60,
        "5C5AB7A3B3DB5E6257ADCCD7641D56CFF3D74E21901E90C67E84EECDA37E0A76")]
    public void NumericGlyphIsolationKeepsFrozenLegacyPixels(string file, int x, int y, int w, int h,
        bool gain, int expectedWidth, int expectedHeight, string expectedHash)
    {
        var path = CareerTestResourceResolver.FindUraCapture(CareerTestResourceResolver.FindWorkspaceRoot(), file);
        var frame = GrayImageCodec.FromFile(path)!;
        var crop = GrayImageCodec.Crop(frame, new Int32Rect(x, y, w, h))!;
        var actual = RecognitionImageProcessor.PrepareNumericGlyphs(
            new(crop.Width, crop.Height, crop.Pixels, crop.RgbaPixels), gain,
            new(MinimumRed: 240, MinimumGreen: 95, MaximumGreen: 225, MaximumBlue: 95, MinimumRedGreen: 25))!;
        Assert.Equal(expectedWidth, actual.Width); Assert.Equal(expectedHeight, actual.Height);
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(actual.GrayPixels!)));
    }
}
