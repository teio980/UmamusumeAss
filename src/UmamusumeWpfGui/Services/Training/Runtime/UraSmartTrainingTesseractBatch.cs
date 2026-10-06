using System.Diagnostics;
using System.Globalization;
using System.Text;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>One process reads isolated image pages; TSV page numbers identify fields even on empty pages.</summary>
internal static class UraSmartTrainingTesseractBatch
{
    internal sealed record Reading(int? Value, string RawText, bool GlyphMismatch);

    public static async Task<IReadOnlyDictionary<string, Reading>> ReadAsync(
        KeyValuePair<string, GrayImage>[] fields, CancellationToken externalBudget,
        CancellationToken cancellationToken, Action? processStarted = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var readings = fields.ToDictionary(field => field.Key, _ => new Reading(null, "", false));
        if (fields.Length == 0 || externalBudget.IsCancellationRequested)
            return readings;
        var prefix = Path.Combine(Path.GetTempPath(), $"uma-smart-ocr-{Guid.NewGuid():N}");
        var paths = new List<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(externalBudget, cancellationToken);
        // Each verified frame has its own bounded budget. A cold first process must
        // not consume every later field's opportunity, or the second frame's retry.
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var images = new Dictionary<string, string>();
            foreach (var field in fields)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var image = CareerNumericOcrReader.Upscale(field.Value, 2);
                var rgba = new byte[checked(image.Pixels.Length * 4)];
                for (var pixel = 0; pixel < image.Pixels.Length; pixel++)
                {
                    rgba[pixel * 4] = rgba[pixel * 4 + 1] = rgba[pixel * 4 + 2] = image.Pixels[pixel];
                    rgba[pixel * 4 + 3] = 255;
                }
                var path = prefix + "-" + field.Key + ".png";
                paths.Add(path);
                GrayImageCodec.SaveScreenshot(new AdbScreenshotResult(AdbScreenshotMethod.Raw, [],
                    TimeSpan.Zero, new AdbRawScreenshot(image.Width, image.Height, rgba)), path);
                images[field.Key] = path;
            }
            var listPath = prefix + ".txt";
            paths.Add(listPath);
            foreach (var mode in new[] { "7", "8", "13" })
            {
                var pending = fields.Where(field => readings[field.Key].Value is null).ToArray();
                if (pending.Length == 0 || timeout.IsCancellationRequested)
                    break;
                await File.WriteAllLinesAsync(listPath, pending.Select(field => images[field.Key]),
                    new UTF8Encoding(false), timeout.Token).ConfigureAwait(false);
                var tsv = await RunAsync(listPath, mode, processStarted, timeout.Token, cancellationToken)
                    .ConfigureAwait(false);
                if (tsv is null)
                    continue;
                var pages = ParsePages(tsv, pending.Length);
                for (var page = 0; page < pending.Length; page++)
                {
                    var field = pending[page];
                    var raw = pages.GetValueOrDefault(page + 1, "");
                    var value = CareerOcrNumberParser.ParseTrainingNumber([raw],
                        field.Key == "failure_rate" ? 100 : 999);
                    var mismatch = field.Key != "failure_rate" && value is { } number
                        && !UraSmartTrainingGainGlyphValidator.Matches(field.Value, number);
                    var previous = readings[field.Key];
                    readings[field.Key] = new(mismatch ? null : value,
                        previous.RawText.Length == 0 ? raw : previous.RawText + " | " + raw,
                        previous.GlyphMismatch || mismatch);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Preserve known readings; unavailable numbers remain unknown.
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            // Optional local OCR can be unavailable; never substitute a guessed value.
        }
        finally
        {
            foreach (var path in paths)
                try { File.Delete(path); }
                catch (IOException) { }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return readings;
    }

    internal static Dictionary<int, string> ParsePages(string tsv, int pageCount)
    {
        var pages = new Dictionary<int, List<string>>();
        foreach (var line in tsv.Split('\n'))
        {
            var cells = line.TrimEnd('\r').Split('\t', 12);
            if (cells.Length != 12 || cells[0] != "5"
                || !int.TryParse(cells[1], NumberStyles.None, CultureInfo.InvariantCulture, out var page)
                || page < 1 || page > pageCount || string.IsNullOrWhiteSpace(cells[11]))
                continue;
            if (!pages.TryGetValue(page, out var words))
                pages[page] = words = [];
            words.Add(cells[11]);
        }
        return pages.ToDictionary(page => page.Key, page => string.Join(" ", page.Value));
    }

    private static async Task<string?> RunAsync(string listPath, string mode, Action? processStarted,
        CancellationToken timeout, CancellationToken caller)
    {
        var start = new ProcessStartInfo
        {
            FileName = ResolveExecutable(), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        // Prevent OpenMP workers competing with the emulator and UI for a tiny crop.
        // This setting belongs to this child process only, never the app environment.
        start.Environment["OMP_THREAD_LIMIT"] = "1";
        foreach (var argument in new[] { listPath, "stdout", "--psm", mode, "-l", "eng", "tsv" })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start())
            return null;
        processStarted?.Invoke();
        var output = process.StandardOutput.ReadToEndAsync(timeout);
        var errors = process.StandardError.ReadToEndAsync(timeout);
        try
        {
            await process.WaitForExitAsync(timeout).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            var result = await output.ConfigureAwait(false);
            return process.ExitCode == 0 ? result : null;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            // Observe redirected reads also when a process is canceled or killed.
            try { await Task.WhenAll(output, errors).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested) { }
        }
    }

    private static string ResolveExecutable()
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tesseract-OCR"),
            });
        return directories.Select(directory => Path.Combine(directory, "tesseract.exe"))
            .FirstOrDefault(File.Exists) ?? "tesseract.exe";
    }
}
