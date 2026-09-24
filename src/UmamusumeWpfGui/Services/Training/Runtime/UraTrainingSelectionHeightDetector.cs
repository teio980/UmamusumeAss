using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

internal sealed record UraTrainingLogoMatch(
    string TrainingType,
    TemplateMatchResult Match);

internal sealed record UraTrainingSelectionHeightResult(
    string? RaisedType,
    IReadOnlyList<UraTrainingLogoMatch> Matches,
    string? Error,
    bool ScreenChanged = false)
{
    public bool Succeeded => RaisedType is not null;
}

/// <summary>
/// Finds all five training logos in one frame and identifies the raised one
/// by its height. The individual templates are supplied by the URA pack.
/// </summary>
internal sealed class UraTrainingSelectionHeightDetector
{
    private readonly IVisualPipelineRuntime _visualRuntime;

    public UraTrainingSelectionHeightDetector(IVisualPipelineRuntime visualRuntime) =>
        _visualRuntime = visualRuntime ?? throw new ArgumentNullException(nameof(visualRuntime));

    public async Task<UraTrainingSelectionHeightResult> DetectAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        CancellationToken cancellationToken)
    {
        var screen = await _visualRuntime.CaptureGrayAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (screen is null)
            return new(null, [], "The training selection screenshot could not be captured.");

        var definition = pack.ExecutionDefinition;
        var recognition = pack.ScreenProfile.Find("training_selection")?.Recognition;
        if (recognition is null || string.IsNullOrWhiteSpace(recognition.Template))
            return new(null, [], "The training selection header is not configured.");

        var header = await _visualRuntime.LoadTemplateAsync(
                recognition.Template,
                definition.BaseDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        if (header is null)
            return new(null, [], "The training selection header could not be loaded.");

        if (!IsTrainingSelectionScreen(screen, header, recognition, pack.ScreenProfile))
            return new(null, [], null, ScreenChanged: true);

        var templates = new Dictionary<string, GrayImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var trainingType in UraTrainingTypeCatalog.SupportedTypes)
        {
            var normal = definition.GetTask($"training_selection_training_{trainingType}");
            if (string.IsNullOrWhiteSpace(normal.Template))
                return new(null, [], $"The {trainingType} training logo is not configured.");

            var template = await _visualRuntime.LoadTemplateAsync(
                    normal.Template,
                    definition.BaseDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            if (template is null)
                return new(null, [], $"The {trainingType} training logo could not be loaded.");

            templates.Add(trainingType, template);
        }

        return CompareHeights(screen, definition, templates);
    }

    // After a training tap, only its own logo matters. Other logos can be
    // temporarily obscured while the picker is leaving the screen.
    public async Task<TemplateMatchResult?> FindTrainingTypeAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string trainingType,
        CancellationToken cancellationToken)
    {
        var screen = await _visualRuntime.CaptureGrayAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (screen is null)
            return null;

        var definition = pack.ExecutionDefinition;
        var normal = definition.GetTask($"training_selection_training_{trainingType}");
        if (string.IsNullOrWhiteSpace(normal.Template))
            return null;

        var template = await _visualRuntime.LoadTemplateAsync(
                normal.Template,
                definition.BaseDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        return template is null
            ? null
            : FindTrainingType(screen, definition, trainingType, template);
    }

    internal static bool IsTrainingSelectionScreen(
        GrayImage screen,
        GrayImage header,
        UraScreenRecognition recognition,
        UraScreenProfile profile) =>
        TemplateMatcher.Find(
            screen,
            header,
            recognition.Roi,
            recognition.TemplateThreshold,
            profile.ReferenceWidth,
            profile.ReferenceHeight).Found;

    internal static UraTrainingSelectionHeightResult CompareHeights(
        GrayImage screen,
        HachimiPipelineDefinition definition,
        IReadOnlyDictionary<string, GrayImage> templates)
    {
        var matches = new List<UraTrainingLogoMatch>(UraTrainingTypeCatalog.SupportedTypes.Count);
        foreach (var trainingType in UraTrainingTypeCatalog.SupportedTypes)
        {
            var normal = definition.GetTask($"training_selection_training_{trainingType}");
            if (!templates.TryGetValue(trainingType, out var template)
                || FindTrainingType(screen, definition, trainingType, template) is not { } match)
            {
                return new(null, matches,
                    $"The {trainingType} training logo regions are not configured.");
            }
            if (!match.Found)
            {
                return new(null, matches,
                    $"The {trainingType} training logo was not found "
                    + $"(score {match.Score:0.000} / threshold {normal.TemplateThreshold:0.000}).");
            }

            matches.Add(new UraTrainingLogoMatch(trainingType, match));
        }

        return SelectHighest(matches);
    }

    internal static TemplateMatchResult? FindTrainingType(
        GrayImage screen,
        HachimiPipelineDefinition definition,
        string trainingType,
        GrayImage template)
    {
        var normal = definition.GetTask($"training_selection_training_{trainingType}");
        var raised = definition.GetTask($"training_selection_{trainingType}_raised_probe");
        if (normal.Roi is not { Length: >= 4 }
            || raised.Roi is not { Length: >= 4 })
            return null;

        return TemplateMatcher.FindColor(
            screen,
            template,
            Union(normal.Roi, raised.Roi),
            normal.TemplateThreshold,
            definition.ReferenceWidth,
            definition.ReferenceHeight);
    }

    internal static UraTrainingSelectionHeightResult SelectHighest(
        IReadOnlyList<UraTrainingLogoMatch> matches)
    {
        if (matches.Count != UraTrainingTypeCatalog.SupportedTypes.Count)
            return new(null, matches, "Not all five training logos were matched.");

        return new(matches.MinBy(item => item.Match.CenterY)!.TrainingType, matches, null);
    }

    private static int[] Union(int[] first, int[] second)
    {
        var x = Math.Min(first[0], second[0]);
        var y = Math.Min(first[1], second[1]);
        var right = Math.Max(first[0] + first[2], second[0] + second[2]);
        var bottom = Math.Max(first[1] + first[3], second[1] + second[3]);
        return [x, y, right - x, bottom - y];
    }
}
