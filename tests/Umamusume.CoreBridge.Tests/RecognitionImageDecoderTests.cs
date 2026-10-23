using Umamusume.CoreBridge;

namespace Umamusume.CoreBridge.Tests;

public sealed class RecognitionImageDecoderTests
{
    [Fact]
    public void RgbaInputRejectsInvalidDimensionsAndPlaneLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RecognitionImageDecoder.FromRgba(0, 1, [0, 0, 0, 255]));
        Assert.Throws<ArgumentException>(
            () => RecognitionImageDecoder.FromRgba(1, 1, [0, 0, 0]));
    }

    [NativeFact]
    public void FileAndEncodedByteDecodingReturnIdenticalWicPixelPlanes()
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine(root, "resource", "uma", "assets", "images", "global",
            "trainees", "100601.webp");
        byte[] encoded = File.ReadAllBytes(path);

        RecognitionImage fromFile = Assert.IsType<RecognitionImage>(RecognitionImageDecoder.DecodeFile(path));
        RecognitionImage fromBytes = Assert.IsType<RecognitionImage>(RecognitionImageDecoder.DecodeBytes(encoded));

        Assert.Equal(fromFile.Width, fromBytes.Width);
        Assert.Equal(fromFile.Height, fromBytes.Height);
        Assert.Equal(fromFile.GrayPixels, fromBytes.GrayPixels);
        Assert.Equal(fromFile.RgbaPixels, fromBytes.RgbaPixels);
    }

    [NativeFact]
    public void RgbaInputKeepsOriginalChannelsAndUsesLegacyGrayFormula()
    {
        byte[] rgba = [255, 0, 0, 128, 0, 255, 0, 17, 0, 0, 255, 255];

        RecognitionImage image = RecognitionImageDecoder.FromRgba(3, 1, rgba);

        Assert.Equal(3, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Equal(new byte[] { 76, 149, 29 }, image.GrayPixels);
        Assert.Equal(rgba, image.RgbaPixels);
    }

    [NativeFact]
    public void InvalidFilesAndEncodedBytesReturnNull()
    {
        Assert.Null(RecognitionImageDecoder.DecodeFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png")));
        Assert.Null(RecognitionImageDecoder.DecodeBytes([0, 1, 2, 3, 4]));
    }

    [NativeFact]
    public void MacScreenshotOcrAcceptsBothColorAndGrayImagesThroughTheManagedBridge()
    {
        if (!OperatingSystem.IsMacOS()) return;
        string root = FindRepositoryRoot();
        string userDir = Path.Combine(Path.GetTempPath(), "uma-ocr-bridge-" + Guid.NewGuid());
        Directory.CreateDirectory(userDir);
        try
        {
            var native = new UmaCoreBridgeNative();
            Assert.Equal(0, native.SetUserDir(userDir));
            Assert.Equal(0, native.LoadResource(root));
            UmaApiCallback callback = (_, _, _) => { };
            using SafeUmaHandle handle = native.Create(callback, IntPtr.Zero);
            Assert.False(handle.IsInvalid);
            RecognitionImage image = Assert.IsType<RecognitionImage>(RecognitionImageDecoder.DecodeFile(
                Path.Combine(root, "testdata", "hachimi", "ura", "captures",
                    "career_skill_gourmand_selected_bluestacks_mac_20261023.png")));
            RecognitionImage crop = Assert.IsType<RecognitionImage>(RecognitionImageDecoder.Transform(image,
                new RecognitionImageTransform(625, 704, 220, 155)));
            foreach (RecognitionImage input in new[] { crop, crop with { RgbaPixels = null } })
            {
                TextRecognitionResult result = native.RecognizeText(handle, input, "en-US");
                Assert.Equal("eng", result.Language);
                Assert.Equal(input.Width, result.Width);
                Assert.Equal(input.Height, result.Height);
                Assert.Single(result.Detections, d => d.Text.Contains("Obtained", StringComparison.OrdinalIgnoreCase));
                Assert.All(result.Detections, d =>
                {
                    Assert.InRange(d.Bounds.X, 0, input.Width - d.Bounds.Width);
                    Assert.InRange(d.Bounds.Y, 0, input.Height - d.Bounds.Height);
                    Assert.InRange(d.Confidence, 0d, 1d);
                });
            }
            GC.KeepAlive(callback);
        }
        finally
        {
            Directory.Delete(userDir, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(Environment.CurrentDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CMakeLists.txt"))
                && Directory.Exists(Path.Combine(directory.FullName, "resource")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the UmamusumeAss repository root.");
    }
}
