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
    private const double MinimumSelectedPortraitScore = 0.42;
    public const double MinimumImageMatchScore = 0.38;
    public const double MinimumSystemReferenceMatchScore = 0.60;

    // A system reference is normally cropped from a full-size emulator
    // screenshot, while the same Uma is rendered much smaller inside a
    // runner card. Search a small range of relative sizes so the reference
    // can still match the card portrait instead of assuming a 1:1 crop.
    private static readonly double[] ScreenshotCropScaleCandidates =
        [0.56, 0.52, 0.60, 0.48, 0.64, 0.72, 0.84, 1.00];

    private static readonly double[] SelectedPortraitScaleCandidates =
        [1.40, 1.20, 1.60, 1.00, 1.80, 2.00];

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

    /// <summary>
    /// Runs the same runner-card matcher used by Daily Race without touching
    /// the emulator. Developer Tools uses this to make its diagnostic result
    /// match the real automation path.
    /// </summary>
    public static async Task<TemplateMatchResult?> FindBestMatchAsync(
        GrayImage screen,
        string imagePath,
        LastVerifiedConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(connection);

        return await FindBestMatchAsync(
                screen,
                [imagePath],
                connection,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the runner-card matcher against all available visual variants for
    /// one trainee. A screenshot crop, race-outfit source image, and uniform
    /// source image can all be supplied; the highest-scoring variant wins.
    /// </summary>
    public static async Task<TemplateMatchResult?> FindBestMatchAsync(
        GrayImage screen,
        IReadOnlyList<string> imagePaths,
        LastVerifiedConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(imagePaths);
        ArgumentNullException.ThrowIfNull(connection);

        var templates = await LoadRunnerTemplatesAsync(imagePaths, cancellationToken)
            .ConfigureAwait(false);
        if (templates.Count == 0)
            return null;

        var best = FindBestRunnerCell(screen, templates, connection);
        return CreateRunnerCellMatch(best, screen, connection);
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

        if (!await EnsureDescendingSortAsync(
                connection,
                definition,
                cancellationToken).ConfigureAwait(false))
        {
            return HachimiCustomActionResult.Failure(
                "Could not verify Rating descending order on the runner list.");
        }

        if (traineeId is null)
        {
            await TapReferenceRectAsync(
                    connection,
                    [20, 800, 160, 190],
                    "runnerSelectHighest",
                    cancellationToken)
                .ConfigureAwait(false);
            return HachimiCustomActionResult.Success(
                "No runner was specified; selected the first runner after Rating descending sort.");
        }

        if (!_umaDatabase.TryGetTrainee(traineeId.Value, out var trainee)
            || trainee is null)
        {
            return HachimiCustomActionResult.Failure(
                $"The configured Daily Race runner ID {traineeId.Value.ToString(CultureInfo.InvariantCulture)} "
                + "was not found in the Uma database.");
        }

        var templatePaths = GetRunnerTemplatePaths(trainee);
        if (templatePaths.Count == 0)
        {
            return HachimiCustomActionResult.Failure(
                $"No runner image template is available for {trainee.NameEn} "
                + $"({trainee.TraineeId.ToString(CultureInfo.InvariantCulture)}). "
                + "Add a system reference or download the Uma image assets first.");
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

        // The tab label is small anti-aliased text and proved noticeably less
        // stable than the surrounding fixed dialog layout on real devices.
        // Tap the center of the right-hand tab, then let the concrete aptitude
        // option matches below verify that the Filter pane actually opened.
        await TapReferenceRectAsync(
                connection,
                [450, 120, 430, 100],
                "runnerFilterTab",
                cancellationToken)
            .ConfigureAwait(false);

        await _visualRuntime.DelayAsync(350, cancellationToken).ConfigureAwait(false);
        await TapReferenceRectAsync(
                connection,
                [620, 1280, 260, 110],
                "runnerFilterReset",
                cancellationToken)
            .ConfigureAwait(false);
        await _visualRuntime.DelayAsync(250, cancellationToken).ConfigureAwait(false);

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
        var templates = await LoadRunnerTemplatesAsync(templatePaths, cancellationToken)
            .ConfigureAwait(false);
        if (templates.Count == 0)
        {
            return HachimiCustomActionResult.Failure(
                $"Could not decode any image template for {trainee.NameEn}.");
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
                var best = FindBestRunnerCell(screen, templates, connection);
                if (best.Score >= best.RequiredScore)
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

                    await _visualRuntime.DelayAsync(500, cancellationToken)
                        .ConfigureAwait(false);
                    var selectedPortraitScore = await FindSelectedPortraitScoreAsync(
                            connection,
                            templates,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (selectedPortraitScore < MinimumSelectedPortraitScore)
                    {
                        return HachimiCustomActionResult.Failure(
                            $"Tapped a card for {trainee.NameEn}, but the selected runner detail "
                            + $"could not be verified (score {selectedPortraitScore:0.000} / "
                            + $"threshold {MinimumSelectedPortraitScore:0.000}).");
                    }

                    return HachimiCustomActionResult.Success(
                        $"Filtered and selected {trainee.NameEn} "
                        + $"({trainee.TraineeId.ToString(CultureInfo.InvariantCulture)}) "
                        + $"at card score {best.Score:0.000}; "
                        + $"selected portrait score {selectedPortraitScore:0.000}.");
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
        var screenX = ScaleCoordinate(best.Cell.X, screen.Width, DefaultReferenceWidth);
        var screenY = ScaleCoordinate(best.Cell.Y, screen.Height, DefaultReferenceHeight);
        var screenCellWidth = Math.Max(
            1,
            ScaleCoordinate(best.Cell.Width, screen.Width, DefaultReferenceWidth));
        var screenCellHeight = Math.Max(
            1,
            ScaleCoordinate(best.Cell.Height, screen.Height, DefaultReferenceHeight));
        var x = ScaleCoordinate(screenX, connection.Width, screen.Width);
        var y = ScaleCoordinate(screenY, connection.Height, screen.Height);
        var width = Math.Max(1, ScaleCoordinate(screenCellWidth, connection.Width, screen.Width));
        var height = Math.Max(1, ScaleCoordinate(screenCellHeight, connection.Height, screen.Height));
        return new TemplateMatchResult(
            best.Score >= best.RequiredScore,
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
        var missingLabels = new List<string>();

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
            {
                missingLabels.Add(label);
                continue;
            }

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
                missingLabels.Add(label);
            }

            await _visualRuntime.DelayAsync(150, cancellationToken)
                .ConfigureAwait(false);
        }

        if (clicked == 0 || missingLabels.Count > 0)
        {
            return HachimiCustomActionResult.Failure(
                "The runner filter could not be applied completely. Missing option(s): "
                + string.Join(", ", missingLabels.Distinct(StringComparer.OrdinalIgnoreCase))
                + ".");
        }

        logSink?.Add(
            "Daily Race",
            $"Applied {clicked.ToString(CultureInfo.InvariantCulture)} aptitude filter option(s) "
            + $"for {trainee.NameEn}.",
            LogEntryKind.Info);
        return HachimiCustomActionResult.Success(string.Empty);
    }

    private async Task<bool> EnsureDescendingSortAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        CancellationToken cancellationToken)
    {
        var roi = new[] { 700, 1120, 200, 180 };
        var descending = await _visualRuntime.WaitForMatchAsync(
                connection,
                "templates/daily_race/runner_sort_desc.png",
                roi,
                0.80,
                definition.ReferenceWidth,
                definition.ReferenceHeight,
                5_000,
                250,
                "runnerDesc",
                definition.BaseDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        if (descending is { Found: true })
            return true;

        var ascending = await _visualRuntime.WaitForMatchAsync(
                connection,
                "templates/daily_race/runner_sort_asc.png",
                roi,
                0.80,
                definition.ReferenceWidth,
                definition.ReferenceHeight,
                8_000,
                250,
                "runnerAsc",
                definition.BaseDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        if (ascending is not { Found: true })
            return false;

        await _visualRuntime.TapMatchAsync(
                connection,
                ascending,
                "runnerSetDesc",
                cancellationToken)
            .ConfigureAwait(false);
        await _visualRuntime.DelayAsync(600, cancellationToken).ConfigureAwait(false);
        var verified = await _visualRuntime.WaitForMatchAsync(
                connection,
                "templates/daily_race/runner_sort_desc.png",
                roi,
                0.80,
                definition.ReferenceWidth,
                definition.ReferenceHeight,
                8_000,
                250,
                "runnerDescVerify",
                definition.BaseDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        return verified is { Found: true };
    }

    private async Task TapReferenceRectAsync(
        LastVerifiedConnection connection,
        int[] referenceRect,
        string actionName,
        CancellationToken cancellationToken)
    {
        var x = ScaleCoordinate(referenceRect[0], connection.Width, DefaultReferenceWidth);
        var y = ScaleCoordinate(referenceRect[1], connection.Height, DefaultReferenceHeight);
        var width = Math.Max(
            1,
            ScaleCoordinate(referenceRect[2], connection.Width, DefaultReferenceWidth));
        var height = Math.Max(
            1,
            ScaleCoordinate(referenceRect[3], connection.Height, DefaultReferenceHeight));
        await _visualRuntime.TapMatchAsync(
                connection,
                new TemplateMatchResult(true, 1d, x, y, width, height),
                actionName,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<double> FindSelectedPortraitScoreAsync(
        LastVerifiedConnection connection,
        IReadOnlyList<RunnerTemplate> templates,
        CancellationToken cancellationToken)
    {
        var screenshotTemplates = templates
            .Where(template => template.UsesScreenshotCrop)
            .ToArray();
        // Source assets do not contain the exact selected-detail crop. The
        // card match has already verified those assets, so skip this optional
        // second check when no emulator screenshot reference is available.
        if (screenshotTemplates.Length == 0)
            return MinimumSelectedPortraitScore;

        var screen = await _visualRuntime.CaptureGrayAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (screen is null)
            return 0;

        var regionX = ScaleCoordinate(20, screen.Width, DefaultReferenceWidth);
        var regionY = ScaleCoordinate(180, screen.Height, DefaultReferenceHeight);
        var regionWidth = ScaleCoordinate(410, screen.Width, DefaultReferenceWidth);
        var regionHeight = ScaleCoordinate(470, screen.Height, DefaultReferenceHeight);
        var referenceScale = Math.Min(
            screen.Width / (double)DefaultReferenceWidth,
            screen.Height / (double)DefaultReferenceHeight);
        var bestScore = double.MinValue;
        foreach (var template in screenshotTemplates)
        {
            foreach (var scale in SelectedPortraitScaleCandidates)
            {
                var targetWidth = Math.Max(
                    1,
                    (int)Math.Round(template.Width * referenceScale * scale));
                var targetHeight = Math.Max(
                    1,
                    (int)Math.Round(template.Height * referenceScale * scale));
                if (targetWidth > regionWidth || targetHeight > regionHeight)
                    continue;

                bestScore = Math.Max(
                    bestScore,
                    FindBestScreenshotCropScaleScore(
                        screen,
                        template,
                        regionX,
                        regionY,
                        regionWidth,
                        regionHeight,
                        targetWidth,
                        targetHeight));
            }
        }

        return Math.Max(0, bestScore);
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

                    var pixelCount = checked(image.Width * image.Height);
                    var opaquePixelCount = 0;
                    for (var index = 0; index < pixelCount; index++)
                    {
                        if (rgba[index * 4 + 3] >= 240)
                            opaquePixelCount++;
                    }

                    // system_reference images are usually crops captured from
                    // the emulator. Keep their aspect ratio and search for
                    // the crop inside each runner card instead of stretching
                    // it as if it were a transparent full-body asset.
                    if (opaquePixelCount >= pixelCount * 0.98)
                    {
                        var screenshotPixels = new byte[pixelCount];
                        var screenshotMask = new byte[pixelCount];
                        for (var index = 0; index < pixelCount; index++)
                        {
                            var offset = index * 4;
                            screenshotPixels[index] = (byte)((rgba[offset] * 299
                                + rgba[offset + 1] * 587
                                + rgba[offset + 2] * 114) / 1000);
                            screenshotMask[index] = 255;
                        }

                        return new RunnerTemplate(
                            image.Width,
                            image.Height,
                            screenshotPixels,
                            screenshotMask,
                            UsesScreenshotCrop: true);
                    }

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

                    return new RunnerTemplate(
                        crop.Width,
                        crop.Height,
                        pixels,
                        mask,
                        UsesScreenshotCrop: false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static RunnerCellMatch FindBestRunnerCell(
        GrayImage screen,
        IReadOnlyList<RunnerTemplate> templates,
        LastVerifiedConnection connection)
    {
        var bestScore = double.MinValue;
        var bestCell = RunnerCells[0];
        var requiredScore = MinimumImageMatchScore;
        foreach (var template in templates)
        {
            var templateRequiredScore = template.UsesScreenshotCrop
                ? MinimumSystemReferenceMatchScore
                : MinimumImageMatchScore;
            foreach (var cell in RunnerCells)
            {
                var score = CompareCell(screen, template, cell, connection);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestCell = cell;
                    requiredScore = templateRequiredScore;
                }
            }
        }

        return new RunnerCellMatch(bestCell, Math.Max(0, bestScore), requiredScore);
    }

    private static double CompareCell(
        GrayImage screen,
        RunnerTemplate template,
        RunnerCell cell,
        LastVerifiedConnection connection)
    {
        if (template.UsesScreenshotCrop)
            return CompareScreenshotCrop(screen, template, cell, connection);

        var x = ScaleCoordinate(cell.X + 8, screen.Width, DefaultReferenceWidth);
        var y = ScaleCoordinate(cell.Y + 8, screen.Height, DefaultReferenceHeight);
        var width = Math.Max(
            1,
            ScaleCoordinate(cell.Width - 16, screen.Width, DefaultReferenceWidth));
        var height = Math.Max(
            1,
            ScaleCoordinate(cell.Height - 16, screen.Height, DefaultReferenceHeight));
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

    private static double CompareScreenshotCrop(
        GrayImage screen,
        RunnerTemplate template,
        RunnerCell cell,
        LastVerifiedConnection connection)
    {
        var cellX = ScaleCoordinate(cell.X + 8, screen.Width, DefaultReferenceWidth);
        var cellY = ScaleCoordinate(cell.Y + 8, screen.Height, DefaultReferenceHeight);
        var cellWidth = Math.Max(
            1,
            ScaleCoordinate(cell.Width - 16, screen.Width, DefaultReferenceWidth));
        var cellHeight = Math.Max(
            1,
            ScaleCoordinate(cell.Height - 16, screen.Height, DefaultReferenceHeight));
        if (cellX < 0 || cellY < 0 || cellX + cellWidth > screen.Width || cellY + cellHeight > screen.Height)
            return 0;

        var scaleX = screen.Width / (double)DefaultReferenceWidth;
        var scaleY = screen.Height / (double)DefaultReferenceHeight;
        var referenceScale = Math.Min(scaleX, scaleY);
        var targetWidth = Math.Max(1, (int)Math.Round(template.Width * referenceScale));
        var targetHeight = Math.Max(1, (int)Math.Round(template.Height * referenceScale));
        var fitScale = Math.Min(
            1d,
            Math.Min(
                cellWidth / (double)Math.Max(1, targetWidth),
                cellHeight / (double)Math.Max(1, targetHeight)));
        var bestScore = double.MinValue;
        foreach (var relativeScale in ScreenshotCropScaleCandidates)
        {
            var candidateScale = fitScale * relativeScale;
            var candidateWidth = Math.Max(
                1,
                (int)Math.Round(template.Width * referenceScale * candidateScale));
            var candidateHeight = Math.Max(
                1,
                (int)Math.Round(template.Height * referenceScale * candidateScale));
            if (candidateWidth > cellWidth || candidateHeight > cellHeight)
                continue;

            bestScore = Math.Max(
                bestScore,
                FindBestScreenshotCropScaleScore(
                    screen,
                    template,
                    cellX,
                    cellY,
                    cellWidth,
                    cellHeight,
                    candidateWidth,
                    candidateHeight));
        }

        return Math.Max(0, bestScore);
    }

    private static double FindBestScreenshotCropScaleScore(
        GrayImage screen,
        RunnerTemplate template,
        int cellX,
        int cellY,
        int cellWidth,
        int cellHeight,
        int targetWidth,
        int targetHeight)
    {
        var sampleWidth = Math.Min(16, template.Width);
        var sampleHeight = Math.Min(16, template.Height);
        var candidateStep = 4;
        var maxX = cellX + cellWidth - targetWidth;
        var maxY = cellY + cellHeight - targetHeight;
        var bestScore = double.MinValue;
        var bestX = cellX;
        var bestY = cellY;
        for (var y = cellY; y <= maxY; y += candidateStep)
        {
            for (var x = cellX; x <= maxX; x += candidateStep)
            {
                var score = CompareScreenshotCropAt(
                    screen,
                    template,
                    x,
                    y,
                    targetWidth,
                    targetHeight,
                    sampleWidth,
                    sampleHeight);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestX = x;
                    bestY = y;
                }
            }
        }

        // The portrait is small enough that a four-pixel miss can noticeably
        // depress NCC. Refine around the coarse winner one pixel at a time.
        var refineMinX = Math.Max(cellX, bestX - candidateStep + 1);
        var refineMaxX = Math.Min(maxX, bestX + candidateStep - 1);
        var refineMinY = Math.Max(cellY, bestY - candidateStep + 1);
        var refineMaxY = Math.Min(maxY, bestY + candidateStep - 1);
        for (var y = refineMinY; y <= refineMaxY; y++)
        {
            for (var x = refineMinX; x <= refineMaxX; x++)
            {
                bestScore = Math.Max(
                    bestScore,
                    CompareScreenshotCropAt(
                        screen,
                        template,
                        x,
                        y,
                        targetWidth,
                        targetHeight,
                        sampleWidth,
                        sampleHeight));
            }
        }

        return bestScore;
    }

    private static double CompareScreenshotCropAt(
        GrayImage screen,
        RunnerTemplate template,
        int screenX,
        int screenY,
        int targetWidth,
        int targetHeight,
        int sampleWidth,
        int sampleHeight)
    {
        var sampleCount = sampleWidth * sampleHeight;
        Span<double> templateSamples = stackalloc double[sampleCount];
        Span<double> screenSamples = stackalloc double[sampleCount];
        for (var sampleY = 0; sampleY < sampleHeight; sampleY++)
        {
            var templateY = sampleY * template.Height / sampleHeight;
            var screenYAt = screenY + sampleY * targetHeight / sampleHeight;
            for (var sampleX = 0; sampleX < sampleWidth; sampleX++)
            {
                var templateX = sampleX * template.Width / sampleWidth;
                var screenXAt = screenX + sampleX * targetWidth / sampleWidth;
                var sampleIndex = sampleY * sampleWidth + sampleX;
                templateSamples[sampleIndex] =
                    template.Pixels[templateY * template.Width + templateX];
                screenSamples[sampleIndex] =
                    screen.Pixels[screenYAt * screen.Width + screenXAt];
            }
        }

        var intensityScore = PearsonCorrelation(templateSamples, screenSamples);
        if (sampleWidth < 3 || sampleHeight < 3)
            return intensityScore;

        var gradientCount = (sampleWidth - 2) * (sampleHeight - 2);
        Span<double> templateGradientX = stackalloc double[gradientCount];
        Span<double> templateGradientY = stackalloc double[gradientCount];
        Span<double> screenGradientX = stackalloc double[gradientCount];
        Span<double> screenGradientY = stackalloc double[gradientCount];
        var gradientIndex = 0;
        for (var sampleY = 1; sampleY < sampleHeight - 1; sampleY++)
        {
            for (var sampleX = 1; sampleX < sampleWidth - 1; sampleX++)
            {
                var center = sampleY * sampleWidth + sampleX;
                templateGradientX[gradientIndex] =
                    templateSamples[center + 1] - templateSamples[center - 1];
                templateGradientY[gradientIndex] =
                    templateSamples[center + sampleWidth] - templateSamples[center - sampleWidth];
                screenGradientX[gradientIndex] =
                    screenSamples[center + 1] - screenSamples[center - 1];
                screenGradientY[gradientIndex] =
                    screenSamples[center + sampleWidth] - screenSamples[center - sampleWidth];
                gradientIndex++;
            }
        }

        var edgeScore = (
            PearsonCorrelation(templateGradientX, screenGradientX)
            + PearsonCorrelation(templateGradientY, screenGradientY)) / 2d;
        return Math.Clamp(
            intensityScore * 0.75d + edgeScore * 0.25d,
            -1d,
            1d);
    }

    private static double PearsonCorrelation(
        ReadOnlySpan<double> templateValues,
        ReadOnlySpan<double> screenValues)
    {
        if (templateValues.Length == 0 || templateValues.Length != screenValues.Length)
            return 0;

        var templateTotal = 0d;
        var screenTotal = 0d;
        for (var index = 0; index < templateValues.Length; index++)
        {
            templateTotal += templateValues[index];
            screenTotal += screenValues[index];
        }

        var templateMean = templateTotal / templateValues.Length;
        var screenMean = screenTotal / screenValues.Length;
        var numerator = 0d;
        var templateVariance = 0d;
        var screenVariance = 0d;
        for (var index = 0; index < templateValues.Length; index++)
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

    private List<string> GetRunnerTemplatePaths(UmaTraineeRecord trainee)
    {
        var paths = new List<string>(capacity: 4);
        AddExistingTemplatePath(
            paths,
            _umaDatabase.GetMaintenanceTraineeReferenceImagePath(trainee.TraineeId));
        AddExistingTemplatePath(
            paths,
            _umaDatabase.GetTraineeReferenceImagePath(trainee.TraineeId));
        AddExistingTemplatePath(
            paths,
            _umaDatabase.GetTraineeImagePath(trainee.TraineeId));
        AddExistingTemplatePath(
            paths,
            _umaDatabase.GetTraineeUniformCroppedImagePath(trainee.BaseCharacterId));
        AddExistingTemplatePath(
            paths,
            _umaDatabase.GetTraineeUniformImagePath(trainee.BaseCharacterId));
        return paths;
    }

    private static void AddExistingTemplatePath(
        List<string> paths,
        string path)
    {
        if (File.Exists(path)
            && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(path);
        }
    }

    private static async Task<IReadOnlyList<RunnerTemplate>> LoadRunnerTemplatesAsync(
        IReadOnlyList<string> imagePaths,
        CancellationToken cancellationToken)
    {
        var templates = new List<RunnerTemplate>(imagePaths.Count);
        foreach (var imagePath in imagePaths)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                continue;

            var template = await LoadRunnerTemplateAsync(imagePath, cancellationToken)
                .ConfigureAwait(false);
            if (template is not null)
                templates.Add(template);
        }

        return templates;
    }

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
        byte[] Mask,
        bool UsesScreenshotCrop);

    private readonly record struct RunnerCell(int X, int Y, int Width, int Height)
    {
        public int[] ToArray() => [X, Y, Width, Height];
    }

    private readonly record struct RunnerCellMatch(
        RunnerCell Cell,
        double Score,
        double RequiredScore);

}
