using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>Smart-only all-five height comparison, verified against the selected card marker.</summary>
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
        return await VerifySelectedCardAsync(screen, pack, matches, jobs, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<UraTrainingSelectionHeightResult> VerifySelectedCardAsync(
        GrayImage screen, UraScenarioPack pack, List<UraTrainingLogoMatch> matches,
        List<(string Type, GrayImage Template, int[] Roi, double Threshold)> jobs,
        CancellationToken cancellationToken)
    {
        const string markerTaskName = "training_selection_selected_chevron";
        var definition = pack.ExecutionDefinition;
        if (!definition.TryGetTask(markerTaskName, out var markerTask)
            || markerTask?.Roi is not { Length: >= 4 } markerRoi)
            return UraTrainingSelectionHeightDetector.SelectHighest(matches);
        var markerTemplate = await LoadAsync(pack.VisualResources!.ResolveTaskTemplate(markerTaskName),
            cancellationToken).ConfigureAwait(false);
        if (markerTemplate is null)
            return new(null, matches, "The selected training card marker could not be loaded.");

        var markedTypes = new List<string>();
        foreach (var job in jobs)
        {
            var flat = definition.GetTask($"training_selection_training_{job.Type}").Roi!;
            var marker = UraSmartTrainingColorMatcher.Find(screen, markerTemplate,
                [flat[0], markerRoi[1], flat[2], markerRoi[3]], markerTask.TemplateThreshold,
                definition.ReferenceWidth, definition.ReferenceHeight, cancellationToken);
            if (marker.Found)
                markedTypes.Add(job.Type);
        }
        if (markedTypes.Count != 1)
            return new(null, matches, "The selected training card marker is missing or ambiguous.");
        var selectedType = markedTypes[0];

        // Duel badges and support sparkles can obscure a logo, allowing an
        // unrelated part of the character art to win a broad ROI search.
        // The gold chevrons identify the selected column independently. Use
        // the other well-matched cards to verify the actual raised/flat row.
        var flatRows = matches.Where(item => item.TrainingType != selectedType
                && item.Match.Score >= 0.9)
            .Select(item => item.Match.CenterY).Order().ToArray();
        if (flatRows.Length < 2)
            return new(null, matches, "The training card row could not be verified.");
        var flatY = flatRows[flatRows.Length / 2];
        int ScaleX(int value) => (int)Math.Round(value * screen.Width / (double)definition.ReferenceWidth);
        int ScaleY(int value) => (int)Math.Round(value * screen.Height / (double)definition.ReferenceHeight);
        var toleranceX = ScaleX(22);
        var toleranceY = ScaleY(22);
        for (var index = 0; index < jobs.Count; index++)
        {
            var job = jobs[index];
            var flat = definition.GetTask($"training_selection_training_{job.Type}").Roi!;
            var click = definition.GetTask($"training_selection_{job.Type}_raised_click");
            if (click.ClickOffset is not { Length: >= 2 } offset || offset[1] <= 0)
                return new(null, matches, "The raised training card offset is not configured.");
            var centerX = ScaleX(flat[0] + flat[2] / 2);
            var centerY = flatY - (job.Type == selectedType ? ScaleY(offset[1]) : 0);
            var match = matches[index].Match;
            if (Math.Abs(match.CenterX - centerX) <= toleranceX
                && Math.Abs(match.CenterY - centerY) <= toleranceY)
                continue;

            // Search only around the verified card geometry; no guessed match
            // or tap coordinates are substituted when its icon is obscured.
            int ReferenceX(int value) => (int)Math.Round(value * definition.ReferenceWidth / (double)screen.Width);
            int ReferenceY(int value) => (int)Math.Round(value * definition.ReferenceHeight / (double)screen.Height);
            var roi = new[]
            {
                ReferenceX(centerX - job.Template.Width / 2 - toleranceX),
                ReferenceY(centerY - job.Template.Height / 2 - toleranceY),
                ReferenceX(job.Template.Width + toleranceX * 2),
                ReferenceY(job.Template.Height + toleranceY * 2),
            };
            match = UraSmartTrainingColorMatcher.Find(screen, job.Template, roi, job.Threshold,
                definition.ReferenceWidth, definition.ReferenceHeight, cancellationToken);
            if (!match.Found)
                return new(null, matches, $"The {job.Type} training logo could not be verified on its card.");
            matches[index] = new(job.Type, match);
        }
        var result = UraTrainingSelectionHeightDetector.SelectHighest(matches);
        return result.RaisedType == selectedType ? result
            : new(null, matches, "The training card marker and logo heights disagree.");
    }
}
