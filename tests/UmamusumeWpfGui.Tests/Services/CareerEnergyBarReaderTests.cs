using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Training;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerEnergyBarReaderTests
{
    [Theory]
    [InlineData(25, 25)]
    [InlineData(50, 50)]
    [InlineData(80, 80)]
    public void Measures_filled_energy_width_as_percentage(
        int filledPercent,
        int expectedPercent)
    {
        var frame = CreateEnergyFrame(filledPercent);
        var definition = new UraEnergyBarObservation
        {
            Roi = [0, 0, 200, 40],
            InnerInset = [0, 0, 200, 40],
        };

        var measurement = CareerEnergyBarReader.TryMeasure(frame, definition, 200, 40);

        Assert.NotNull(measurement);
        Assert.InRange(measurement!.Percent, expectedPercent - 2, expectedPercent + 2);
        Assert.True(measurement.Confidence >= 0.70);
    }

    [Fact]
    public void Returns_zero_for_a_stable_empty_bar()
    {
        var frame = CreateEnergyFrame(0);
        var definition = new UraEnergyBarObservation
        {
            Roi = [0, 0, 200, 40],
            InnerInset = [0, 0, 200, 40],
        };

        var measurement = CareerEnergyBarReader.TryMeasure(frame, definition, 200, 40);

        Assert.NotNull(measurement);
        Assert.Equal(0, measurement!.Percent);
    }

    [Fact]
    public void Reads_the_real_adb_rest_captures_before_and_after_recovery()
    {
        var root = FindWorkspaceRoot();
        var definition = new UraEnergyBarObservation
        {
            Roi = [280, 176, 365, 50],
            InnerInset = [7, 10, 349, 28],
        };

        var before = CareerEnergyBarReader.TryMeasure(
            LoadPng(Path.Combine(root, "resource", "hachimi", "ura", "screens", "captures", "rest_before_low_energy.png")),
            definition,
            900,
            1600);
        var result = CareerEnergyBarReader.TryMeasure(
            LoadPng(Path.Combine(root, "resource", "hachimi", "ura", "screens", "captures", "rest_result.png")),
            definition,
            900,
            1600);
        var after = CareerEnergyBarReader.TryMeasure(
            LoadPng(Path.Combine(root, "resource", "hachimi", "ura", "screens", "captures", "career_main_after_rest.png")),
            definition,
            900,
            1600);

        Assert.NotNull(before);
        Assert.NotNull(result);
        Assert.NotNull(after);
        Assert.True(before!.Percent < 50, $"before={before.Percent}%");
        Assert.True(result!.Percent > 50, $"result={result.Percent}%");
        Assert.True(after!.Percent > 50, $"after={after.Percent}%");
    }

    private static GrayImage CreateEnergyFrame(int filledPercent)
    {
        const int width = 200;
        const int height = 40;
        var rgba = new byte[width * height * 4];
        var filledWidth = width * filledPercent / 100;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var filled = x < filledWidth;
                rgba[offset] = filled ? (byte)40 : (byte)128;
                rgba[offset + 1] = filled ? (byte)210 : (byte)128;
                rgba[offset + 2] = filled ? (byte)240 : (byte)128;
                rgba[offset + 3] = 255;
            }
        }

        return new GrayImage(width, height, new byte[width * height], rgba);
    }

    private static GrayImage LoadPng(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(
            decoder.Frames[0],
            PixelFormats.Bgra32,
            null,
            0);
        var bgra = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(bgra, converted.PixelWidth * 4, 0);
        var rgba = new byte[bgra.Length];
        for (var offset = 0; offset < bgra.Length; offset += 4)
        {
            rgba[offset] = bgra[offset + 2];
            rgba[offset + 1] = bgra[offset + 1];
            rgba[offset + 2] = bgra[offset];
            rgba[offset + 3] = bgra[offset + 3];
        }
        return new GrayImage(
            converted.PixelWidth,
            converted.PixelHeight,
            new byte[converted.PixelWidth * converted.PixelHeight],
            rgba);
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "resource",
                    "hachimi",
                    "ura",
                    "manifest.json")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository workspace.");
    }
}
