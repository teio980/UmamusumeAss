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
