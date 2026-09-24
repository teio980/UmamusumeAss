using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

internal sealed record UraTrainingLogoMatch(
    string TrainingType,
    TemplateMatchResult Match);

internal sealed record UraTrainingSelectionHeightResult(
    string? RaisedType,
    IReadOnlyList<UraTrainingLogoMatch> Matches,
    string? Error)
{
    public bool Succeeded => RaisedType is not null;
}

/// <summary>
/// Finds all five training logos in one frame and identifies the raised one
/// by its height. The individual templates are supplied by the URA pack.
/// </summary>
internal sealed class UraTrainingSelectionHeightDetector
{
    private const int MinimumHeightGap = 30;
    private readonly IVisualPipelineRuntime _visualRuntime;

    public UraTrainingSelectionHeightDetector(IVisualPipelineRuntime visualRuntime) =>
        _visualRuntime = visualRuntime ?? throw new ArgumentNullException(nameof(visualRuntime));

    public async Task<UraTrainingSelectionHeightResult> DetectAsync(
        LastVerifiedConnection connection,
        HachimiPipelineDefinition definition,
        CancellationToken cancellationToken)
    {
        var screen = await _visualRuntime.CaptureGrayAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (screen is null)
            return new(null, [], "The training selection screenshot could not be captured.");

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

    internal static UraTrainingSelectionHeightResult CompareHeights(
        GrayImage screen,
        HachimiPipelineDefinition definition,
        IReadOnlyDictionary<string, GrayImage> templates)
    {
        var matches = new List<UraTrainingLogoMatch>(UraTrainingTypeCatalog.SupportedTypes.Count);
        foreach (var trainingType in UraTrainingTypeCatalog.SupportedTypes)
        {
            var normal = definition.GetTask($"training_selection_training_{trainingType}");
            var raised = definition.GetTask($"training_selection_{trainingType}_raised_probe");
            if (normal.Roi is not { Length: >= 4 }
                || raised.Roi is not { Length: >= 4 }
                || !templates.TryGetValue(trainingType, out var template))
            {
                return new(null, matches,
                    $"The {trainingType} training logo regions are not configured.");
            }

            var match = TemplateMatcher.FindColor(
                screen,
                template,
                Union(normal.Roi, raised.Roi),
                normal.TemplateThreshold,
                definition.ReferenceWidth,
                definition.ReferenceHeight);
            if (!match.Found)
            {
                return new(null, matches,
                    $"The {trainingType} training logo was not found "
                    + $"(score {match.Score:0.000} / threshold {normal.TemplateThreshold:0.000}).");
            }

            matches.Add(new UraTrainingLogoMatch(trainingType, match));
        }

        if (matches.Count != UraTrainingTypeCatalog.SupportedTypes.Count)
            return new(null, matches, "Not all five training logos were matched.");

        var byHeight = matches.OrderBy(item => item.Match.CenterY).ToArray();
        var gap = byHeight[1].Match.CenterY - byHeight[0].Match.CenterY;
        var minimumGap = (int)Math.Round(
            MinimumHeightGap * screen.Height / (double)definition.ReferenceHeight);
        if (gap < minimumGap)
        {
            return new(null, matches,
                $"The highest training logo is not clearly raised "
                + $"(gap {gap}px / required {minimumGap}px).");
        }

        return new(byHeight[0].TrainingType, matches, null);
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
