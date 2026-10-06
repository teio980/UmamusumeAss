using System.Diagnostics;
using System.Windows;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>Image preprocessing and Tesseract fallback extracted from the countdown reader.</summary>
internal static class CareerNumericOcrReader
{
    private const int ProcessTimeoutMilliseconds = 2500;

    public static Task<int?> TryReadAsync(
        GrayImage frame, int[] roi, int referenceWidth, int referenceHeight,
        int maximum, CancellationToken fallbackToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (fallbackToken.IsCancellationRequested)
            return Task.FromResult<int?>(null);
        var crop = Crop(frame, roi, referenceWidth, referenceHeight);
        return crop is null ? Task.FromResult<int?>(null)
            : TryReadImageAsync(Upscale(crop, 2), text =>
            {
                return CareerOcrNumberParser.ParseTrainingNumber([text ?? string.Empty], maximum);
            }, fallbackToken, cancellationToken, ["7", "8", "13"]);
    }

    public static async Task<int?> TryReadImageAsync(
        GrayImage image,
        Func<string?, int?> parseNumber,
        CancellationToken fallbackToken,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? pageSegmentationModes = null)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"umamusume-career-number-{Guid.NewGuid():N}.png");
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
            foreach (var pageSegmentationMode in pageSegmentationModes ?? ["8", "13"])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (fallbackToken.IsCancellationRequested)
                    return null;

                var text = await RunTesseractAsync(
                        executable,
                        path,
                        pageSegmentationMode,
                        fallbackToken,
                        cancellationToken)
                    .ConfigureAwait(false);
                var value = parseNumber(text);
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
        CancellationToken fallbackToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (fallbackToken.IsCancellationRequested)
            return null;

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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            fallbackToken,
            cancellationToken);
        timeout.CancelAfter(ProcessTimeoutMilliseconds);
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
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
        finally
        {
            // Also stop the subprocess on caller cancellation; it must not outlive the scan.
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The subprocess can exit between the check and Kill.
            }
        }
    }

    public static GrayImage? Crop(
        GrayImage frame,
        int[] roi,
        int referenceWidth,
        int referenceHeight)
    {
        var x = Scale(roi[0], frame.Width, referenceWidth);
        var y = Scale(roi[1], frame.Height, referenceHeight);
        // Lengths may equal the whole image; coordinate clamping to actual - 1
        // used to remove the last padding row/column from prepared numeric crops.
        var width = Math.Clamp((int)Math.Round(roi[2] * frame.Width
            / (double)Math.Max(1, referenceWidth)), 0, frame.Width - x);
        var height = Math.Clamp((int)Math.Round(roi[3] * frame.Height
            / (double)Math.Max(1, referenceHeight)), 0, frame.Height - y);
        return GrayImageCodec.Crop(frame, new Int32Rect(x, y, width, height));
    }

    public static GrayImage Upscale(GrayImage source, int factor)
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

}
