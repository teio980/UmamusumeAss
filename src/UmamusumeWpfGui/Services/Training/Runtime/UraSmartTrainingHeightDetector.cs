using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>Smart-only accelerated original all-five height comparison.</summary>
internal sealed class UraSmartTrainingHeightDetector(IVisualPipelineRuntime runtime)
{
    private readonly Dictionary<string, GrayImage> _templates = new(StringComparer.OrdinalIgnoreCase);

    private async Task<GrayImage?> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (_templates.TryGetValue(path, out var template))
            return template;
        template = await runtime.LoadTemplateAsync(path, string.Empty, cancellationToken).ConfigureAwait(false);
        if (template is not null)
            _templates[path] = template;
        return template;
    }

    public async Task<UraTrainingSelectionHeightResult> DetectFrameAsync(
        GrayImage screen, UraScenarioPack pack, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var screenDefinition = pack.ScreenProfile.Find("training_selection");
        var recognition = screenDefinition?.Recognition;
        if (recognition is null || string.IsNullOrWhiteSpace(recognition.Template))
            return new(null, [], "The training selection header is not configured.");
        var resources = pack.VisualResources!;
        var header = await LoadAsync(resources.ResolveScreenTemplate(screenDefinition!, recognition.Template), cancellationToken).ConfigureAwait(false);
        if (header is null)
            return new(null, [], "The training selection header could not be loaded.");
        if (!UraTrainingSelectionHeightDetector.IsTrainingSelectionScreen(screen, header, recognition, pack.ScreenProfile))
            return new(null, [], null, ScreenChanged: true);
        var definition = pack.ExecutionDefinition;
        var backTask = definition.GetTask("training_selection_back");
        var back = await LoadAsync(resources.ResolveTaskTemplate("training_selection_back"), cancellationToken).ConfigureAwait(false);
        if (back is null)
            return new(null, [], "The training preview Back template could not be loaded.");
        if (!UraSmartTrainingColorMatcher.Find(screen, back, backTask.Roi, backTask.TemplateThreshold,
                pack.ScreenProfile.ReferenceWidth, pack.ScreenProfile.ReferenceHeight, cancellationToken).Found)
            return new(null, [], null, ScreenChanged: true);
        var jobs = new List<(string Type, GrayImage Template, int[] Roi, double Threshold)>();
        foreach (var type in UraTrainingTypeCatalog.SupportedTypes)
        {
            var normal = definition.GetTask($"training_selection_training_{type}");
            var raised = definition.GetTask($"training_selection_{type}_raised_probe");
            if (normal.Roi is not { Length: >= 4 } flat || raised.Roi is not { Length: >= 4 } up)
                return new(null, [], $"The {type} training logo regions are not configured.");
            var template = await LoadAsync(resources.ResolveTaskTemplate($"training_selection_training_{type}"), cancellationToken).ConfigureAwait(false);
            if (template is null)
                return new(null, [], $"The {type} training logo could not be loaded.");
            var x = Math.Min(flat[0], up[0]);
            var y = Math.Min(flat[1], up[1]);
            int[] roi = [x, y, Math.Max(flat[0] + flat[2], up[0] + up[2]) - x,
                Math.Max(flat[1] + flat[3], up[1] + up[3]) - y];
            jobs.Add((type, template, roi, normal.TemplateThreshold));
        }
        var results = new TemplateMatchResult[jobs.Count];
        // These searches read the same immutable screenshot and write separate slots.
        // Keep bounded CPU parallelism; neither screenshots nor game input are concurrent.
        await Task.Run(() => Parallel.For(0, jobs.Count, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Min(3, Environment.ProcessorCount),
        }, index =>
        {
            var job = jobs[index];
            results[index] = UraSmartTrainingColorMatcher.Find(screen, job.Template, job.Roi,
                job.Threshold, definition.ReferenceWidth, definition.ReferenceHeight, cancellationToken);
        }), cancellationToken).ConfigureAwait(false);
        var matches = new List<UraTrainingLogoMatch>();
        for (var index = 0; index < jobs.Count; index++)
        {
            var type = jobs[index].Type;
            var match = results[index];
            if (!match.Found)
                return new(null, matches, $"The {type} training logo was not found (score {match.Score:0.000}).");
            matches.Add(new(type, match));
        }
        return UraTrainingSelectionHeightDetector.SelectHighest(matches);
    }
}
