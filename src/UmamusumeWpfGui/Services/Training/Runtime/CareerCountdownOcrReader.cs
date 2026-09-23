using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Runs OCR over the countdown card itself. The input is the current screen,
/// not a table of known career turns, so multi-digit values are handled by the
/// OCR engine without a turn-range assumption.
/// </summary>
internal static partial class CareerCountdownOcrReader
{
    private const int UpscaleFactor = 4;
    private const int ProcessTimeoutMilliseconds = 2500;

    public static async Task<int?> TryReadAsync(
        IReadOnlyList<GrayImage> frames,
        int[]? roi,
        int referenceWidth,
        int referenceHeight,
        CancellationToken cancellationToken)
    {
        if (frames.Count == 0 || roi is not { Length: >= 4 })
            return null;

        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = CropAndUpscale(
                frame,
                roi,
                referenceWidth,
                referenceHeight);
            if (image is null)
                continue;

            var result = await TryReadImageAsync(image, cancellationToken)
                .ConfigureAwait(false);
            if (result is not null)
                return result;
        }

        return null;
    }

    private static async Task<int?> TryReadImageAsync(
        GrayImage image,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"umamusume-career-countdown-{Guid.NewGuid():N}.png");
        try
        {
            var rgba = image.RgbaPixels is { Length: >= 4 }
                ? image.RgbaPixels
                : CreateRgbaPixels(image);
            var screenshot = new AdbScreenshotResult(
                AdbScreenshotMethod.Raw,
                [],
                TimeSpan.Zero,
                new AdbRawScreenshot(image.Width, image.Height, rgba));
            GrayImageCodec.SaveScreenshot(screenshot, path);

            var executable = ResolveTesseractExecutable();
            foreach (var pageSegmentationMode in new[] { "8", "13" })
            {
                var text = await RunTesseractAsync(
                        executable,
                        path,
                        pageSegmentationMode,
                        cancellationToken)
                    .ConfigureAwait(false);
                var value = ParseNumber(text);
                if (value is not null)
                    return value;
            }

            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // The temp file is best-effort cleanup only.
            }
        }
    }

    private static async Task<string?> RunTesseractAsync(
        string executable,
        string imagePath,
        string pageSegmentationMode,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(imagePath);
        startInfo.ArgumentList.Add("stdout");
        startInfo.ArgumentList.Add("--psm");
        startInfo.ArgumentList.Add(pageSegmentationMode);
        startInfo.ArgumentList.Add("-l");
        startInfo.ArgumentList.Add("eng");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProcessTimeoutMilliseconds);
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        _ = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return await outputTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process may have exited between the checks.
            }

            return null;
        }
    }

    private static int? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var direct = CareerGoalTextParser.ParseTurnsLeft(text);
        if (direct is not null)
            return direct;

        var match = NumberRegex().Match(text);
        return match.Success
            && int.TryParse(match.Value, out var value)
            ? value
            : null;
    }

    private static GrayImage? CropAndUpscale(
        GrayImage frame,
        int[] roi,
        int referenceWidth,
        int referenceHeight)
    {
        var x = Scale(roi[0], frame.Width, referenceWidth);
        var y = Scale(roi[1], frame.Height, referenceHeight);
        var width = Scale(roi[2], frame.Width, referenceWidth);
        var height = Scale(roi[3], frame.Height, referenceHeight);
        var crop = GrayImageCodec.Crop(frame, new Int32Rect(x, y, width, height));
        return crop is null ? null : Upscale(crop, UpscaleFactor);
    }

    private static GrayImage Upscale(GrayImage source, int factor)
    {
        var width = checked(source.Width * factor);
        var height = checked(source.Height * factor);
        var pixels = new byte[checked(width * height)];
        var rgba = source.RgbaPixels is { Length: >= 4 }
            ? new byte[checked(width * height * 4)]
            : null;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourceIndex = (y / factor) * source.Width + x / factor;
                var targetIndex = y * width + x;
                pixels[targetIndex] = source.Pixels[sourceIndex];
                if (rgba is not null)
                {
                    Buffer.BlockCopy(
                        source.RgbaPixels!,
                        sourceIndex * 4,
                        rgba,
                        targetIndex * 4,
                        4);
                }
            }
        }

        return new GrayImage(width, height, pixels, rgba);
    }

    private static byte[] CreateRgbaPixels(GrayImage image)
    {
        var rgba = new byte[checked(image.Width * image.Height * 4)];
        for (var index = 0; index < image.Pixels.Length; index++)
        {
            var offset = index * 4;
            rgba[offset] = image.Pixels[index];
            rgba[offset + 1] = image.Pixels[index];
            rgba[offset + 2] = image.Pixels[index];
            rgba[offset + 3] = 255;
        }

        return rgba;
    }

    private static string ResolveTesseractExecutable()
    {
        var pathEntries = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            ?? [];
        foreach (var directory in pathEntries)
        {
            var candidate = Path.Combine(directory, "tesseract.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        foreach (var candidate in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Tesseract-OCR",
                "tesseract.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Tesseract-OCR",
                "tesseract.exe"),
        })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return "tesseract.exe";
    }

    private static int Scale(int value, int actual, int reference) =>
        Math.Clamp(
            (int)Math.Round(value * actual / (double)Math.Max(1, reference)),
            0,
            Math.Max(0, actual - 1));

    [GeneratedRegex(@"(?<!\d)\d+(?!\d)")]
    private static partial Regex NumberRegex();
}
