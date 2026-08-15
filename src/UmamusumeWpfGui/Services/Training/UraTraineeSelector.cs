using System.Globalization;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Career-specific trainee selector. It uses the same captured game controls
/// as Daily Race, but owns its own flow and matching logic: no Rating sort,
/// career-specific Filter ROIs, then filtered-card matching and selection.
/// </summary>
public sealed class UraTraineeSelector
{
    private const int ReferenceWidth = 900;
    private const int ReferenceHeight = 1600;
    private const int MaximumScrolls = 16;
    private const double MinimumMatchScore = 0.70;
    private const double MinimumSystemReferenceMatchScore = 0.70;
    private const string SharedTemplatePrefix = "../../";
    private const string CareerFilterTabTemplate = "uma/career_filter_tab.png";

    private static readonly double[] ScreenshotCropScaleCandidates =
        [0.32, 0.36, 0.40, 0.44, 0.48, 0.52, 0.56, 0.60, 0.64, 0.68, 0.72, 0.76, 0.80, 0.84, 0.88, 0.92, 0.96, 1.00];

    private static readonly double[] PreciseScreenshotCropScaleCandidates =
        [0.32, 0.36, 0.40, 0.44, 0.48, 0.52, 0.56, 0.60, 0.64, 0.68, 0.72, 0.76, 0.80, 0.84, 0.88, 0.92, 0.96, 1.00];

    private static readonly int[] RunnerSwipe = [760, 1150, 760, 850, 550];

    private static readonly Dictionary<string, string> FilterTemplatePaths =
        new(StringComparer.OrdinalIgnoreCase)
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

    // Career's Display Settings page currently contains Stars before Track.
    // These ROIs are intentionally owned by Career instead of changing the
    // Daily Race selector's coordinates.
    private static readonly Dictionary<string, int[]> FilterTemplateRois =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Turf"] = [20, 520, 280, 180],
            ["Dirt"] = [300, 520, 300, 180],
            ["Sprint"] = [20, 700, 280, 180],
            ["Mile"] = [300, 700, 280, 180],
            ["Medium"] = [580, 700, 300, 180],
            ["Long"] = [20, 790, 280, 180],
            ["Front"] = [20, 970, 280, 180],
            ["Pace"] = [300, 970, 280, 180],
            ["Late"] = [580, 970, 300, 180],
            ["End"] = [20, 1060, 280, 180],
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
        new(20, 1220, 160, 65),
        new(195, 1220, 160, 65),
        new(370, 1220, 160, 65),
        new(545, 1220, 160, 65),
        new(720, 1220, 160, 65),
    ];

    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly IUmaDatabaseService _umaDatabase;

    public UraTraineeSelector(
        IVisualPipelineRuntime visualRuntime,
        IUmaDatabaseService umaDatabase)
    {
        ArgumentNullException.ThrowIfNull(visualRuntime);
        ArgumentNullException.ThrowIfNull(umaDatabase);
        _visualRuntime = visualRuntime;
        _umaDatabase = umaDatabase;
    }

    public async Task<UraTraineeSelectionResult> SelectAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        string taskName,
        HachimiPipelineTask task,
        int traineeId,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(task);

        if (!_umaDatabase.TryGetTrainee(traineeId, out var trainee)
            || trainee is null
            || !trainee.Available)
        {
            return Failure(
                $"Configured trainee ID {traineeId.ToString(CultureInfo.InvariantCulture)} "
                + "was not found or is unavailable.");
        }

        var templatePaths = GetTemplatePaths(trainee);
        if (templatePaths.Count == 0)
        {
            return Failure(
                $"No trainee image template is available for {trainee.NameEn} "
                + $"({trainee.TraineeId.ToString(CultureInfo.InvariantCulture)}).");
        }

        if (!await TapTemplateAsync(
                connection,
                definition,
                "templates/daily_race/runner_display_button.png",
                [400, 1100, 500, 250],
                0.80,
                "careerRunnerFilterOpen",
                cancellationToken).ConfigureAwait(false))
        {
            return Failure("Could not open the Career trainee display settings.");
        }

        await _visualRuntime.DelayAsync(350, cancellationToken).ConfigureAwait(false);
        if (!await TapTemplateAsync(
                connection,
                definition,
                CareerFilterTabTemplate,
                [500, 120, 300, 120],
                0.78,
                "careerRunnerFilterTab",
                cancellationToken).ConfigureAwait(false))
        {
            return Failure("The Career trainee Filter tab did not appear.");
        }

        await _visualRuntime.DelayAsync(350, cancellationToken).ConfigureAwait(false);
        await TapReferenceRectAsync(
                connection,
                [620, 1280, 260, 110],
                "careerRunnerFilterReset",
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
            return new UraTraineeSelectionResult(false, filterResult.Message);

        if (!await TapTemplateAsync(
                connection,
                definition,
                "templates/daily_race/runner_filter_confirm.png",
                [300, 1300, 550, 250],
                0.80,
                "careerRunnerFilterConfirm",
                cancellationToken).ConfigureAwait(false))
        {
            return Failure("Could not confirm the Career trainee filters.");
        }

        await _visualRuntime.DelayAsync(700, cancellationToken).ConfigureAwait(false);
        var templates = await LoadTemplatesAsync(templatePaths, cancellationToken)
            .ConfigureAwait(false);
        if (templates.Count == 0)
            return Failure($"Could not decode any image template for {trainee.NameEn}.");

        var bestObservedScore = 0d;
        for (var scroll = 0; scroll <= MaximumScrolls; scroll++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var screen = await _visualRuntime.CaptureGrayAsync(
                    connection,
                    cancellationToken)
                .ConfigureAwait(false);
            if (screen is not null)
            {
                var best = FindBestCard(screen, templates);
                bestObservedScore = Math.Max(bestObservedScore, best.Score);
                logSink?.Add(
                    "Career Training",
                    $"Career trainee page {scroll + 1}: match {best.Score:0.000} / "
                    + $"required {MinimumMatchScore:0.000} at card "
                    + $"({best.Cell.X},{best.Cell.Y},{best.Cell.Width},{best.Cell.Height}).",
                    LogEntryKind.Info);

                if (best.Match is not null && best.Score >= MinimumMatchScore)
                {
                    try
                    {
                        await _visualRuntime.TapMatchAsync(
                                connection,
                                best.Match,
                                "careerTraineeSelection",
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        return Failure($"Found {trainee.NameEn}, but the trainee card could not be selected.");
                    }

                    return new UraTraineeSelectionResult(
                        true,
                        $"Filtered and selected {trainee.NameEn} "
                        + $"({trainee.TraineeId.ToString(CultureInfo.InvariantCulture)}) "
                        + $"with card score {best.Score:0.000}.");
                }
            }

            if (scroll == MaximumScrolls)
                break;

            await _visualRuntime.SwipeAsync(
                    connection,
                    RunnerSwipe,
                    definition.ReferenceWidth,
                    definition.ReferenceHeight,
                    "careerTraineeListScroll",
                    cancellationToken)
                .ConfigureAwait(false);
            await _visualRuntime.DelayAsync(350, cancellationToken).ConfigureAwait(false);
        }

        return Failure(
            $"Filtered the Career trainee list, but could not find {trainee.NameEn} "
            + $"({trainee.TraineeId.ToString(CultureInfo.InvariantCulture)}) "
            + $"after scrolling (best observed score {bestObservedScore:0.000} / "
            + $"required {MinimumMatchScore:0.000}).");
    }

    private async Task<UraTraineeSelectionResult> ApplyAptitudeFiltersAsync(
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
            if (!FilterTemplatePaths.TryGetValue(label, out var templatePath)
                || !FilterTemplateRois.TryGetValue(label, out var roi))
            {
                missingLabels.Add(label);
                continue;
            }

            var match = await _visualRuntime.WaitForMatchAsync(
                    connection,
                    SharedTemplatePath(templatePath),
                    roi,
                    0.78,
                    definition.ReferenceWidth,
                    definition.ReferenceHeight,
                    8_000,
                    250,
                    "careerRunnerFilterOption",
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
                        "careerRunnerFilterOption",
                        cancellationToken)
                    .ConfigureAwait(false);
                clicked++;
            }
            catch (InvalidOperationException)
            {
                missingLabels.Add(label);
            }

            await _visualRuntime.DelayAsync(150, cancellationToken).ConfigureAwait(false);
        }

        if (clicked == 0 || missingLabels.Count > 0)
        {
            return Failure(
                "The Career trainee filter could not be applied completely. Missing option(s): "
                + string.Join(", ", missingLabels.Distinct(StringComparer.OrdinalIgnoreCase))
                + ".");
        }

        logSink?.Add(
            "Career Training",
            $"Applied {clicked.ToString(CultureInfo.InvariantCulture)} aptitude filter option(s) "
            + $"for {trainee.NameEn}.",
            LogEntryKind.Info);
        return new UraTraineeSelectionResult(true, string.Empty);
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
                SharedTemplatePath(templatePath),
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

    private async Task TapReferenceRectAsync(
        LastVerifiedConnection connection,
        int[] referenceRect,
        string actionName,
        CancellationToken cancellationToken)
    {
        var x = ScaleCoordinate(referenceRect[0], connection.Width, ReferenceWidth);
        var y = ScaleCoordinate(referenceRect[1], connection.Height, ReferenceHeight);
        var width = Math.Max(1, ScaleCoordinate(referenceRect[2], connection.Width, ReferenceWidth));
        var height = Math.Max(1, ScaleCoordinate(referenceRect[3], connection.Height, ReferenceHeight));
        await _visualRuntime.TapMatchAsync(
                connection,
                new TemplateMatchResult(true, 1d, x, y, width, height),
                actionName,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static CardMatch FindBestCard(
        GrayImage screen,
        IReadOnlyList<RunnerTemplate> templates)
    {
        var bestScore = double.MinValue;
        var bestCell = RunnerCells[0];
        var requiredScore = MinimumMatchScore;
        var screenshotTemplates = templates
            .Where(template => template.UsesScreenshotCrop
                && template.SourceImage is not null)
            .ToArray();
        var templatesToCompare = screenshotTemplates.Length > 0
            ? screenshotTemplates
            : templates;

        foreach (var cell in RunnerCells)
        {
            foreach (var template in templatesToCompare)
            {
                var templateRequiredScore = template.UsesScreenshotCrop
                    ? MinimumSystemReferenceMatchScore
                    : MinimumMatchScore;
                var score = CompareCell(screen, template, cell);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestCell = cell;
                    requiredScore = templateRequiredScore;
                }

                // Keep the same display-priority behavior as Developer Tools:
                // select the first card that clears the identity threshold.
                if (score >= templateRequiredScore)
                {
                    return new CardMatch(
                        cell,
                        score,
                        CreateCardMatch(screen, cell, score, templateRequiredScore));
                }
            }
        }

        return new CardMatch(
            bestCell,
            Math.Max(0, bestScore),
            bestScore >= requiredScore
                ? CreateCardMatch(screen, bestCell, bestScore, requiredScore)
                : null);
    }

    private static TemplateMatchResult CreateCardMatch(
        GrayImage screen,
        RunnerCell cell,
        double score,
        double requiredScore)
    {
        var x = ScaleCoordinate(cell.X, screen.Width, ReferenceWidth);
        var y = ScaleCoordinate(cell.Y, screen.Height, ReferenceHeight);
        var width = Math.Max(1, ScaleCoordinate(cell.Width, screen.Width, ReferenceWidth));
        var height = Math.Max(1, ScaleCoordinate(cell.Height, screen.Height, ReferenceHeight));
        return new TemplateMatchResult(
            score >= requiredScore,
            score,
            x,
            y,
            width,
            height);
    }

    private static double CompareCell(
        GrayImage screen,
        RunnerTemplate template,
        RunnerCell cell)
    {
        var x = ScaleCoordinate(cell.X, screen.Width, ReferenceWidth);
        var y = ScaleCoordinate(cell.Y, screen.Height, ReferenceHeight);
        var width = Math.Max(1, ScaleCoordinate(cell.Width, screen.Width, ReferenceWidth));
        var height = Math.Max(1, ScaleCoordinate(cell.Height, screen.Height, ReferenceHeight));
        if (x < 0 || y < 0 || x + width > screen.Width || y + height > screen.Height)
            return 0;

        if (template.UsesScreenshotCrop)
        {
            if (template.SourceImage is not { } sourceImage)
                return CompareScreenshotCrop(screen, template, cell);

            return TemplateMatcher.FindScaled(
                    screen,
                    sourceImage,
                    [x, y, width, height],
                    threshold: 0,
                    referenceWidth: screen.Width,
                    referenceHeight: screen.Height,
                    PreciseScreenshotCropScaleCandidates)
                .Score;
        }

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
        RunnerCell cell)
    {
        var cellX = ScaleCoordinate(cell.X, screen.Width, ReferenceWidth);
        var cellY = ScaleCoordinate(cell.Y, screen.Height, ReferenceHeight);
        var cellWidth = Math.Max(1, ScaleCoordinate(cell.Width, screen.Width, ReferenceWidth));
        var cellHeight = Math.Max(1, ScaleCoordinate(cell.Height, screen.Height, ReferenceHeight));
        if (cellX < 0 || cellY < 0 || cellX + cellWidth > screen.Width || cellY + cellHeight > screen.Height)
            return 0;

        var referenceScale = Math.Min(
            screen.Width / (double)ReferenceWidth,
            screen.Height / (double)ReferenceHeight);
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
            var candidateWidth = Math.Max(1, (int)Math.Round(template.Width * referenceScale * candidateScale));
            var candidateHeight = Math.Max(1, (int)Math.Round(template.Height * referenceScale * candidateScale));
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
        const int candidateStep = 4;
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
                templateSamples[sampleIndex] = template.Pixels[templateY * template.Width + templateX];
                screenSamples[sampleIndex] = screen.Pixels[screenYAt * screen.Width + screenXAt];
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
                templateGradientX[gradientIndex] = templateSamples[center + 1] - templateSamples[center - 1];
                templateGradientY[gradientIndex] = templateSamples[center + sampleWidth] - templateSamples[center - sampleWidth];
                screenGradientX[gradientIndex] = screenSamples[center + 1] - screenSamples[center - 1];
                screenGradientY[gradientIndex] = screenSamples[center + sampleWidth] - screenSamples[center - sampleWidth];
                gradientIndex++;
            }
        }

        var edgeScore = (
            PearsonCorrelation(templateGradientX, screenGradientX)
            + PearsonCorrelation(templateGradientY, screenGradientY)) / 2d;
        return Math.Clamp(intensityScore * 0.75d + edgeScore * 0.25d, -1d, 1d);
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

    private static async Task<IReadOnlyList<RunnerTemplate>> LoadTemplatesAsync(
        List<string> paths,
        CancellationToken cancellationToken)
    {
        var templates = new List<RunnerTemplate>(paths.Count);
        foreach (var path in paths)
        {
            var template = await LoadRunnerTemplateAsync(path, cancellationToken)
                .ConfigureAwait(false);
            if (template is not null)
                templates.Add(template);
        }

        return templates;
    }

    private static async Task<RunnerTemplate?> LoadRunnerTemplateAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        return await Task.Run(
                () =>
                {
                    if (imagePath.Contains("system_reference", StringComparison.OrdinalIgnoreCase))
                    {
                        var decodedReference = GrayImageCodec.FromFile(imagePath);
                        if (decodedReference is not null)
                        {
                            var referenceMask = new byte[
                                checked(decodedReference.Width * decodedReference.Height)];
                            Array.Fill(referenceMask, (byte)255);
                            return new RunnerTemplate(
                                decodedReference.Width,
                                decodedReference.Height,
                                decodedReference.Pixels,
                                referenceMask,
                                UsesScreenshotCrop: true,
                                SourceImage: decodedReference);
                        }
                    }

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

                    if (opaquePixelCount >= pixelCount * 0.98)
                    {
                        var screenshotImage = GrayImageCodec.FromFile(imagePath);
                        if (screenshotImage is not null)
                        {
                            var decodedMask = new byte[
                                checked(screenshotImage.Width * screenshotImage.Height)];
                            Array.Fill(decodedMask, (byte)255);
                            return new RunnerTemplate(
                                screenshotImage.Width,
                                screenshotImage.Height,
                                screenshotImage.Pixels,
                                decodedMask,
                                UsesScreenshotCrop: true,
                                SourceImage: screenshotImage);
                        }
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
                        UsesScreenshotCrop: false,
                        SourceImage: null);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private List<string> GetTemplatePaths(UmaTraineeRecord trainee)
    {
        var paths = new List<string>(capacity: 5);
        AddExisting(paths, _umaDatabase.GetMaintenanceTraineeReferenceImagePath(trainee.TraineeId));
        AddExisting(paths, _umaDatabase.GetTraineeReferenceImagePath(trainee.TraineeId));
        AddExisting(paths, _umaDatabase.GetTraineeImagePath(trainee.TraineeId));
        AddExisting(paths, _umaDatabase.GetTraineeLiveOutfitReferenceImagePath(trainee.BaseCharacterId));
        AddExisting(paths, _umaDatabase.GetTraineeLiveOutfitImagePath(trainee.BaseCharacterId));
        return paths;
    }

    private static void AddExisting(List<string> paths, string path)
    {
        if (File.Exists(path)
            && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(path);
        }
    }

    private static string SharedTemplatePath(string templatePath) =>
        SharedTemplatePrefix + templatePath;

    private static int ScaleCoordinate(int value, int actual, int reference) =>
        (int)Math.Round(value * (double)Math.Max(1, actual) / Math.Max(1, reference));

    private static IEnumerable<string> GetDesiredFilterLabels(UmaTraineeRecord trainee)
    {
        foreach (var key in BestAptitudeKeys(trainee.Aptitudes.Surface))
        {
            yield return key switch
            {
                "turf" => "Turf",
                "dirt" => "Dirt",
                _ => key,
            };
        }

        foreach (var key in BestAptitudeKeys(trainee.Aptitudes.Distance))
        {
            yield return key switch
            {
                "short" => "Sprint",
                "mile" => "Mile",
                "medium" => "Medium",
                "long" => "Long",
                _ => key,
            };
        }

        foreach (var key in BestAptitudeKeys(trainee.Aptitudes.Strategy))
        {
            yield return key switch
            {
                "front" => "Front",
                "pace" => "Pace",
                "late" => "Late",
                "end" => "End",
                _ => key,
            };
        }
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

    private static UraTraineeSelectionResult Failure(string message) =>
        new(false, message);

    private readonly record struct RunnerCell(int X, int Y, int Width, int Height);

    private sealed record RunnerTemplate(
        int Width,
        int Height,
        byte[] Pixels,
        byte[] Mask,
        bool UsesScreenshotCrop,
        GrayImage? SourceImage);

    private sealed record CardMatch(
        RunnerCell Cell,
        double Score,
        TemplateMatchResult? Match);
}

public sealed record UraTraineeSelectionResult(
    bool Succeeded,
    string Message);
