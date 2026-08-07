using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Selects a configured Daily Race runner after the normal Rating sort has
/// completed. The game does not expose runner IDs to the generic pipeline, so
/// this adapter combines the aptitude filter with matching against the local
/// trainee image while paging through the five-column runner grid.
/// </summary>
public sealed class DailyRaceRunnerSelector
{
    private const int DefaultReferenceWidth = 900;
    private const int DefaultReferenceHeight = 1600;
    private const int MaximumScrolls = 16;
    private const double MinimumImageMatchScore = 0.38;

    private static readonly int[] SortButtonRect = [500, 1185, 260, 125];
    private static readonly int[] FilterTabRect = [440, 105, 440, 70];
    private static readonly int[] SettingsConfirmRect = [460, 1400, 380, 130];
    private static readonly int[] HighestRunnerRect = [20, 800, 180, 220];
    private static readonly int[] RunnerSwipe = [760, 1150, 760, 850, 550];

    private static readonly RunnerCell[] RunnerCells =
    [
        new(20, 800, 160, 190),
        new(195, 800, 160, 190),
        new(370, 800, 160, 190),
        new(545, 800, 160, 190),
        new(720, 800, 160, 190),
        new(20, 1010, 160, 190),
        new(195, 1010, 160, 190),
        new(370, 1010, 160, 190),
        new(545, 1010, 160, 190),
        new(720, 1010, 160, 190),
        new(20, 1215, 160, 190),
        new(195, 1215, 160, 190),
        new(370, 1215, 160, 190),
        new(545, 1215, 160, 190),
        new(720, 1215, 160, 190),
    ];

    private readonly IAdbRuntime _adbRuntime;
    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly IUmaDatabaseService _umaDatabase;

    public DailyRaceRunnerSelector(
        IAdbRuntime adbRuntime,
        IVisualPipelineRuntime visualRuntime,
        IUmaDatabaseService umaDatabase)
    {
        ArgumentNullException.ThrowIfNull(adbRuntime);
        ArgumentNullException.ThrowIfNull(visualRuntime);
        ArgumentNullException.ThrowIfNull(umaDatabase);
        _adbRuntime = adbRuntime;
        _visualRuntime = visualRuntime;
        _umaDatabase = umaDatabase;
    }

    public async Task<HachimiCustomActionResult> SelectAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        string taskName,
        HachimiPipelineTask task,
        int? traineeId,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(task);

        if (traineeId is null)
        {
            var defaultTap = await TapRectAsync(
                    connection,
                    definition,
                    HighestRunnerRect,
                    taskName,
                    cancellationToken)
                .ConfigureAwait(false);
            return defaultTap
                ? HachimiCustomActionResult.Success(
                    "No runner was specified; selected the highest-rated runner.")
                : HachimiCustomActionResult.Failure(
                    "Could not select the highest-rated Daily Race runner.");
        }

        if (!_umaDatabase.TryGetTrainee(traineeId.Value, out var trainee)
            || trainee is null)
        {
            return HachimiCustomActionResult.Failure(
                $"The configured Daily Race runner ID {traineeId.Value.ToString(CultureInfo.InvariantCulture)} "
                + "was not found in the Uma database.");
        }

        var imagePath = _umaDatabase.GetTraineeImagePath(trainee.TraineeId);
        if (!File.Exists(imagePath))
        {
            return HachimiCustomActionResult.Failure(
                $"The image for {trainee.NameEn} ({trainee.TraineeId.ToString(CultureInfo.InvariantCulture)}) "
                + "is missing, so the runner cannot be located visually.");
        }

        if (!await TapRectAsync(
                connection,
                definition,
                SortButtonRect,
                "runnerFilterOpen",
                cancellationToken).ConfigureAwait(false))
        {
            return HachimiCustomActionResult.Failure(
                "Could not open the Daily Race runner display settings.");
        }

        await _visualRuntime.DelayAsync(350, cancellationToken).ConfigureAwait(false);
        var settingsMatch = await _visualRuntime.WaitForMatchAsync(
                connection,
                "templates/daily_race/runner_display_settings.png",
                [0, 0, DefaultReferenceWidth, 800],
                0.78,
                definition.ReferenceWidth,
                definition.ReferenceHeight,
                8_000,
                250,
                "runnerDisplaySettings",
                definition.BaseDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        if (settingsMatch is not { Found: true })
        {
            return HachimiCustomActionResult.Failure(
                "The Daily Race runner display settings did not appear.");
        }

        if (!await TapRectAsync(
                connection,
                definition,
                FilterTabRect,
                "runnerFilterTab",
                cancellationToken).ConfigureAwait(false))
        {
            return HachimiCustomActionResult.Failure(
                "Could not open the Daily Race runner filter tab.");
        }

        await _visualRuntime.DelayAsync(350, cancellationToken).ConfigureAwait(false);
        var filterResult = await ApplyAptitudeFiltersAsync(
                connection,
                definition,
                trainee,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (!filterResult.Succeeded)
            return filterResult;

        if (!await TapRectAsync(
                connection,
                definition,
                SettingsConfirmRect,
                "runnerFilterConfirm",
                cancellationToken).ConfigureAwait(false))
        {
            return HachimiCustomActionResult.Failure(
                "Could not confirm the Daily Race runner filters.");
        }

        await _visualRuntime.DelayAsync(700, cancellationToken).ConfigureAwait(false);
        var template = await LoadRunnerTemplateAsync(imagePath, cancellationToken)
            .ConfigureAwait(false);
        if (template is null)
        {
            return HachimiCustomActionResult.Failure(
                $"Could not decode the image for {trainee.NameEn}.");
        }

        for (var scroll = 0; scroll <= MaximumScrolls; scroll++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var screen = await _visualRuntime.CaptureGrayAsync(
                    connection,
                    cancellationToken)
                .ConfigureAwait(false);
            if (screen is not null)
            {
                var best = FindBestRunnerCell(screen, template, connection);
                if (best.Score >= MinimumImageMatchScore)
                {
                    var tapped = await TapRectAsync(
                            connection,
                            definition,
                            best.Cell.ToArray(),
                            "runnerSelection",
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!tapped)
                    {
                        return HachimiCustomActionResult.Failure(
                            $"Found {trainee.NameEn}, but the runner card could not be selected.");
                    }

                    return HachimiCustomActionResult.Success(
                        $"Filtered and selected {trainee.NameEn} "
                        + $"({trainee.TraineeId.ToString(CultureInfo.InvariantCulture)}) "
                        + $"at image score {best.Score:0.000}.");
                }
            }

            if (scroll == MaximumScrolls)
                break;

            await _visualRuntime.SwipeAsync(
                    connection,
                    RunnerSwipe,
                    definition.ReferenceWidth,
                    definition.ReferenceHeight,
                    "runnerListScroll",
                    cancellationToken)
                .ConfigureAwait(false);
            await _visualRuntime.DelayAsync(350, cancellationToken).ConfigureAwait(false);
        }

        return HachimiCustomActionResult.Failure(
            $"Filtered the runner list, but could not find {trainee.NameEn} "
            + $"({trainee.TraineeId.ToString(CultureInfo.InvariantCulture)}) after scrolling.");
    }

    private async Task<HachimiCustomActionResult> ApplyAptitudeFiltersAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        UmaTraineeRecord trainee,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var desiredLabels = GetDesiredFilterLabels(trainee).ToArray();
        var hierarchy = await ReadUiHierarchyAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        var clicked = 0;

        if (hierarchy is not null)
        {
            foreach (var label in desiredLabels)
            {
                var node = hierarchy.FirstOrDefault(item => item.Matches(label));
                if (node is null)
                    continue;

                if (await TapAsync(
                        connection,
                        ScalePoint(node.CenterX, node.CenterY, definition, connection),
                        "runnerFilterOption",
                        cancellationToken).ConfigureAwait(false))
                {
                    clicked++;
                }
            }
        }

        if (clicked == 0)
        {
            // Unity builds do not always expose their labels through
            // UIAutomator. These are the stable reference positions used by
            // the current display-settings layout; the dynamic path above is
            // preferred whenever the game exposes accessible text.
            foreach (var label in desiredLabels)
            {
                if (!FallbackFilterRects.TryGetValue(label, out var rect))
                    continue;
                if (await TapRectAsync(
                        connection,
                        definition,
                        rect,
                        "runnerFilterOption",
                        cancellationToken).ConfigureAwait(false))
                {
                    clicked++;
                }
            }
        }

        if (clicked == 0)
        {
            return HachimiCustomActionResult.Failure(
                "The runner filter options could not be located on the device.");
        }

        logSink?.Add(
            "Daily Race",
            $"Applied {clicked.ToString(CultureInfo.InvariantCulture)} aptitude filter option(s) "
            + $"for {trainee.NameEn}.",
            LogEntryKind.Info);
        return HachimiCustomActionResult.Success(string.Empty);
    }

    private async Task<UiNodeLabel[]?> ReadUiHierarchyAsync(
        LastVerifiedConnection connection,
        CancellationToken cancellationToken)
    {
        var dump = await _adbRuntime.ShellAsync(
                connection.AdbPath,
                connection.Serial,
                ["uiautomator", "dump", "/sdcard/umamusume-ass-window.xml"],
                cancellationToken)
            .ConfigureAwait(false);
        if (dump.Error is not null || dump.TimedOut || dump.ExitCode != 0)
            return null;

        var xml = await _adbRuntime.ShellAsync(
                connection.AdbPath,
                connection.Serial,
                ["cat", "/sdcard/umamusume-ass-window.xml"],
                cancellationToken)
            .ConfigureAwait(false);
        if (xml.Error is not null || xml.TimedOut || xml.ExitCode != 0)
            return null;

        try
        {
            return XDocument.Parse(xml.Stdout)
                .Descendants("node")
                .Select(ParseUiNode)
                .Where(item => item is not null)
                .Cast<UiNodeLabel>()
                .ToArray();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static UiNodeLabel? ParseUiNode(XElement node)
    {
        var labels = new[]
        {
            node.Attribute("text")?.Value,
            node.Attribute("content-desc")?.Value,
        }
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Select(item => item!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
        if (labels.Length == 0)
            return null;

        var bounds = node.Attribute("bounds")?.Value;
        if (bounds is null)
            return null;
        var match = Regex.Match(
            bounds,
            @"\[(?<x1>\d+),(?<y1>\d+)\]\[(?<x2>\d+),(?<y2>\d+)\]");
        if (!match.Success
            || !int.TryParse(match.Groups["x1"].Value, out var x1)
            || !int.TryParse(match.Groups["y1"].Value, out var y1)
            || !int.TryParse(match.Groups["x2"].Value, out var x2)
            || !int.TryParse(match.Groups["y2"].Value, out var y2))
        {
            return null;
        }

        return new UiNodeLabel((x1 + x2) / 2, (y1 + y2) / 2, labels);
    }

    private static async Task<RunnerTemplate?> LoadRunnerTemplateAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        return await Task.Run(
                () =>
                {
                    using var image = Image.Load<Rgba32>(imagePath);
                    var rgba = new byte[checked(image.Width * image.Height * 4)];
                    image.CopyPixelDataTo(rgba);

                    var minX = image.Width;
                    var minY = image.Height;
                    var maxX = -1;
                    var maxY = -1;
                    for (var y = 0; y < image.Height; y++)
                    {
                        for (var x = 0; x < image.Width; x++)
                        {
                            var alpha = rgba[(y * image.Width + x) * 4 + 3];
                            if (alpha < 24)
                                continue;
                            minX = Math.Min(minX, x);
                            minY = Math.Min(minY, y);
                            maxX = Math.Max(maxX, x);
                            maxY = Math.Max(maxY, y);
                        }
                    }

                    if (maxX < minX || maxY < minY)
                        return null;

                    var cropHeight = Math.Max(1, (int)Math.Round((maxY - minY + 1) * 0.78));
                    using var crop = image.Clone(context => context.Crop(
                        new Rectangle(minX, minY, maxX - minX + 1, cropHeight)));
                    crop.Mutate(context => context.Resize(new ResizeOptions
                    {
                        Size = new Size(128, 160),
                        Mode = ResizeMode.Stretch,
                    }));

                    var resized = new byte[checked(crop.Width * crop.Height * 4)];
                    crop.CopyPixelDataTo(resized);
                    var pixels = new byte[crop.Width * crop.Height];
                    var mask = new byte[pixels.Length];
                    for (var index = 0; index < pixels.Length; index++)
                    {
                        var offset = index * 4;
                        pixels[index] = (byte)((resized[offset] * 299
                            + resized[offset + 1] * 587
                            + resized[offset + 2] * 114) / 1000);
                        mask[index] = resized[offset + 3];
                    }

                    return new RunnerTemplate(crop.Width, crop.Height, pixels, mask);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static RunnerCellMatch FindBestRunnerCell(
        GrayImage screen,
        RunnerTemplate template,
        LastVerifiedConnection connection)
    {
        var bestScore = double.MinValue;
        var bestCell = RunnerCells[0];
        foreach (var cell in RunnerCells)
        {
            var score = CompareCell(screen, template, cell, connection);
            if (score > bestScore)
            {
                bestScore = score;
                bestCell = cell;
            }
        }

        return new RunnerCellMatch(bestCell, Math.Max(0, bestScore));
    }

    private static double CompareCell(
        GrayImage screen,
        RunnerTemplate template,
        RunnerCell cell,
        LastVerifiedConnection connection)
    {
        var x = ScaleCoordinate(cell.X + 8, screen.Width, connection.Width);
        var y = ScaleCoordinate(cell.Y + 8, screen.Height, connection.Height);
        var width = Math.Max(1, ScaleCoordinate(cell.Width - 16, screen.Width, connection.Width));
        var height = Math.Max(1, ScaleCoordinate(cell.Height - 16, screen.Height, connection.Height));
        if (x < 0 || y < 0 || x + width > screen.Width || y + height > screen.Height)
            return 0;

        var templateValues = new List<double>(template.Width * template.Height / 2);
        var screenValues = new List<double>(templateValues.Capacity);
        for (var templateY = 0; templateY < template.Height; templateY += 2)
        {
            var screenY = y + templateY * height / template.Height;
            for (var templateX = 0; templateX < template.Width; templateX += 2)
            {
                var templateIndex = templateY * template.Width + templateX;
                if (template.Mask[templateIndex] < 48)
                    continue;

                var screenX = x + templateX * width / template.Width;
                templateValues.Add(template.Pixels[templateIndex]);
                screenValues.Add(screen.Pixels[screenY * screen.Width + screenX]);
            }
        }

        if (templateValues.Count < 32)
            return 0;

        var templateMean = templateValues.Average();
        var screenMean = screenValues.Average();
        var numerator = 0d;
        var templateVariance = 0d;
        var screenVariance = 0d;
        for (var index = 0; index < templateValues.Count; index++)
        {
            var templateDelta = templateValues[index] - templateMean;
            var screenDelta = screenValues[index] - screenMean;
            numerator += templateDelta * screenDelta;
            templateVariance += templateDelta * templateDelta;
            screenVariance += screenDelta * screenDelta;
        }

        if (templateVariance < 1 || screenVariance < 1)
            return 0;
        return Math.Clamp(
            numerator / Math.Sqrt(templateVariance * screenVariance),
            -1d,
            1d);
    }

    private async Task<bool> TapRectAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        int[] rect,
        string actionName,
        CancellationToken cancellationToken)
    {
        if (rect.Length < 4)
            return false;
        var x = ScaleCoordinate(
            rect[0] + rect[2] / 2,
            connection.Width,
            definition.ReferenceWidth);
        var y = ScaleCoordinate(
            rect[1] + rect[3] / 2,
            connection.Height,
            definition.ReferenceHeight);
        return await TapAsync(
                connection,
                (x, y),
                actionName,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> TapAsync(
        LastVerifiedConnection connection,
        (int X, int Y) point,
        string actionName,
        CancellationToken cancellationToken)
    {
        var result = await _adbRuntime.TapAsync(
                connection.AdbPath,
                connection.Serial,
                point.X,
                point.Y,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result.Error is null && !result.TimedOut && result.ExitCode == 0;
    }

    private static (int X, int Y) ScalePoint(
        int x,
        int y,
        HachimiPipelineDefinition definition,
        LastVerifiedConnection connection) =>
        (
            ScaleCoordinate(x, connection.Width, definition.ReferenceWidth),
            ScaleCoordinate(y, connection.Height, definition.ReferenceHeight));

    private static int ScaleCoordinate(int value, int actual, int reference) =>
        (int)Math.Round(value * (double)Math.Max(1, actual) / Math.Max(1, reference));

    private static IEnumerable<string> GetDesiredFilterLabels(UmaTraineeRecord trainee)
    {
        foreach (var key in BestAptitudeKeys(trainee.Aptitudes.Surface))
            yield return key switch
            {
                "turf" => "Turf",
                "dirt" => "Dirt",
                _ => key,
            };

        foreach (var key in BestAptitudeKeys(trainee.Aptitudes.Distance))
            yield return key switch
            {
                "short" => "Sprint",
                "mile" => "Mile",
                "medium" => "Medium",
                "long" => "Long",
                _ => key,
            };

        foreach (var key in BestAptitudeKeys(trainee.Aptitudes.Strategy))
            yield return key switch
            {
                "front" => "Front",
                "pace" => "Pace",
                "late" => "Late",
                "end" => "End",
                _ => key,
            };
    }

    private static IEnumerable<string> BestAptitudeKeys(
        IReadOnlyDictionary<string, string> aptitudes)
    {
        var best = aptitudes.Values.Select(GradeRank).DefaultIfEmpty(-1).Max();
        return aptitudes
            .Where(item => GradeRank(item.Value) == best)
            .Select(item => item.Key.Trim().ToLowerInvariant());
    }

    private static int GradeRank(string? grade) => grade?.Trim().ToUpperInvariant() switch
    {
        "S" => 8,
        "A" => 7,
        "B" => 6,
        "C" => 5,
        "D" => 4,
        "E" => 3,
        "F" => 2,
        "G" => 1,
        _ => 0,
    };

    private static readonly Dictionary<string, int[]> FallbackFilterRects =
        new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Turf"] = [20, 260, 220, 110],
            ["Dirt"] = [460, 260, 220, 110],
            ["Sprint"] = [20, 480, 220, 110],
            ["Mile"] = [460, 480, 220, 110],
            ["Medium"] = [20, 620, 220, 110],
            ["Long"] = [460, 620, 220, 110],
            ["Front"] = [20, 840, 220, 110],
            ["Pace"] = [460, 840, 220, 110],
            ["Late"] = [20, 980, 220, 110],
            ["End"] = [460, 980, 220, 110],
        };

    private sealed record RunnerTemplate(
        int Width,
        int Height,
        byte[] Pixels,
        byte[] Mask);

    private readonly record struct RunnerCell(int X, int Y, int Width, int Height)
    {
        public int[] ToArray() => [X, Y, Width, Height];
    }

    private readonly record struct RunnerCellMatch(RunnerCell Cell, double Score);

    private sealed record UiNodeLabel(int CenterX, int CenterY, string[] Labels)
    {
        public bool Matches(string expected) => Labels.Any(label =>
            string.Equals(label, expected, StringComparison.OrdinalIgnoreCase)
            || label.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }
}
