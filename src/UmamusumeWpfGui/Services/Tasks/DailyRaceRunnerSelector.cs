using System.IO;
using System.Globalization;
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

    private static readonly int[] RunnerSwipe = [760, 1150, 760, 850, 550];

    private static readonly Dictionary<string, string> FilterTemplatePaths =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Turf"] = "templates/daily_race/runner_filter_turf.png",
            ["Dirt"] = "templates/daily_race/runner_filter_dirt.png",
            ["Sprint"] = "templates/daily_race/runner_filter_sprint.png",
            ["Mile"] = "templates/daily_race/runner_filter_mile.png",
            ["Medium"] = "templates/daily_race/runner_filter_medium.png",
            ["Long"] = "templates/daily_race/runner_filter_long.png",
            ["Front"] = "templates/daily_race/runner_filter_front.png",
            ["Pace"] = "templates/daily_race/runner_filter_pace.png",
            ["Late"] = "templates/daily_race/runner_filter_late.png",
            ["End"] = "templates/daily_race/runner_filter_end.png",
        };

    private static readonly Dictionary<string, int[]> FilterTemplateRois =
        new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Turf"] = [20, 280, 280, 180],
            ["Dirt"] = [300, 280, 300, 180],
            ["Sprint"] = [20, 440, 280, 180],
            ["Mile"] = [300, 440, 280, 180],
            ["Medium"] = [580, 440, 300, 180],
            ["Long"] = [20, 550, 280, 180],
            ["Front"] = [20, 700, 280, 180],
            ["Pace"] = [300, 700, 280, 180],
            ["Late"] = [580, 700, 300, 180],
            ["End"] = [20, 790, 280, 180],
        };

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

    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly IUmaDatabaseService _umaDatabase;

    public DailyRaceRunnerSelector(
        IVisualPipelineRuntime visualRuntime,
        IUmaDatabaseService umaDatabase)
    {
        ArgumentNullException.ThrowIfNull(visualRuntime);
        ArgumentNullException.ThrowIfNull(umaDatabase);
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
            return HachimiCustomActionResult.Success(
                "No runner was specified; kept the runner selected after Rating sort.");
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

        if (!await TapTemplateAsync(
                connection,
                definition,
                "templates/daily_race/runner_display_button.png",
                [400, 1100, 500, 250],
                0.80,
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

        if (!await TapTemplateAsync(
                connection,
                definition,
                "templates/daily_race/runner_filter_tab.png",
                [0, 100, 900, 250],
                0.80,
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

        if (!await TapTemplateAsync(
                connection,
                definition,
                "templates/daily_race/runner_filter_confirm.png",
                [300, 1300, 550, 250],
                0.80,
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
                    var match = CreateRunnerCellMatch(best, screen, connection);
                    try
                    {
                        await _visualRuntime.TapMatchAsync(
                                connection,
                                match,
                                "runnerSelection",
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
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

    private static TemplateMatchResult CreateRunnerCellMatch(
        RunnerCellMatch best,
        GrayImage screen,
        LastVerifiedConnection connection)
    {
        var x = ScaleCoordinate(best.Cell.X, screen.Width, connection.Width);
        var y = ScaleCoordinate(best.Cell.Y, screen.Height, connection.Height);
        var width = Math.Max(1, ScaleCoordinate(best.Cell.Width, screen.Width, connection.Width));
        var height = Math.Max(1, ScaleCoordinate(best.Cell.Height, screen.Height, connection.Height));
        return new TemplateMatchResult(
            true,
            best.Score,
            x,
            y,
            width,
            height);
    }

    private async Task<HachimiCustomActionResult> ApplyAptitudeFiltersAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        UmaTraineeRecord trainee,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var desiredLabels = GetDesiredFilterLabels(trainee).ToArray();
        var clicked = 0;

        foreach (var label in desiredLabels)
        {
            if (!FilterTemplatePaths.TryGetValue(label, out var templatePath))
                continue;

            var match = await _visualRuntime.WaitForMatchAsync(
                    connection,
                    templatePath,
                    FilterTemplateRois[label],
                    0.78,
                    definition.ReferenceWidth,
                    definition.ReferenceHeight,
                    8_000,
                    250,
                    "runnerFilterOption",
                    definition.BaseDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            if (match is not { Found: true })
                continue;

            try
            {
                await _visualRuntime.TapMatchAsync(
                        connection,
                        match,
                        "runnerFilterOption",
                        cancellationToken)
                    .ConfigureAwait(false);
                clicked++;
            }
            catch (InvalidOperationException)
            {
                // Keep trying the remaining aptitude templates so one failed
                // ADB tap does not hide which filter option was unavailable.
            }

            await _visualRuntime.DelayAsync(150, cancellationToken)
                .ConfigureAwait(false);
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

    private async Task<bool> TapTemplateAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        string templatePath,
        int[] roi,
        double threshold,
        string actionName,
        CancellationToken cancellationToken)
    {
        var match = await _visualRuntime.WaitForMatchAsync(
                connection,
                templatePath,
                roi,
                threshold,
                definition.ReferenceWidth,
                definition.ReferenceHeight,
                8_000,
                250,
                actionName,
                definition.BaseDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        if (match is not { Found: true })
            return false;

        try
        {
            await _visualRuntime.TapMatchAsync(
                    connection,
                    match,
                    actionName,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

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

}
