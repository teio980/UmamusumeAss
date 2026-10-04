using System.Collections.Concurrent;
using System.Text;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Recognizes opted-in event titles only after their configured action marker
/// appears. Both the marker and OCR use the caller's already captured frame.
/// </summary>
internal sealed class CareerEventTitleRecognizer
{
    internal const string TitleRegionId = "event.title";

    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly ConcurrentDictionary<string, Lazy<Task<GrayImage?>>> _templates =
        new(StringComparer.OrdinalIgnoreCase);

    public CareerEventTitleRecognizer(IVisualPipelineRuntime visualRuntime)
    {
        _visualRuntime = visualRuntime ?? throw new ArgumentNullException(nameof(visualRuntime));
    }

    public async Task<CareerEventTitleMatch?> RecognizeAsync(
        GrayImage frame,
        UraScenarioPack pack,
        string screenId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var events = pack.Events?.Events.Where(item =>
            item.OcrTitle is { } recognition
            && recognition.ScreenId.Equals(screenId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (events is not { Length: > 0 })
            return null;

        var screen = pack.ScreenProfile.Find(screenId);
        var roi = screen?.FindOcrRegion(TitleRegionId)?.ToRoi();
        if (screen is null || roi is null)
            return null;

        var readyActions = new Dictionary<string,
            (TemplateMatchResult Match, HachimiPipelineRunOptions? Options)>(StringComparer.OrdinalIgnoreCase);
        foreach (var actionId in events.Select(item => item.OcrTitle!.ActionId)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var action = screen.FindAction(actionId);
            if (action is null
                || !pack.ExecutionDefinition.Tasks.TryGetValue(action.Task, out var task)
                || string.IsNullOrWhiteSpace(task.Template))
            {
                continue;
            }

            var path = pack.VisualResources is { } resources
                && resources.TryGetResolvedTaskTemplate(action.Task, out var resolved)
                ? resolved!
                : UraScenarioResourceResolver.Resolve(pack, task.Template);
            var template = await _templates.GetOrAdd(path,
                    key => new Lazy<Task<GrayImage?>>(() => _visualRuntime.LoadTemplateAsync(
                        key, string.Empty, CancellationToken.None)))
                .Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (template is null)
                continue;

            var needsReferenceScaling = task.ScaleCandidates.Count == 0
                && (frame.Width != pack.ExecutionDefinition.ReferenceWidth
                    || frame.Height != pack.ExecutionDefinition.ReferenceHeight);
            List<double> scales = needsReferenceScaling ? [1d] : task.ScaleCandidates;
            var match = scales.Count > 0
                ? TemplateMatcher.FindScaled(frame, template, task.Roi, task.TemplateThreshold,
                    pack.ExecutionDefinition.ReferenceWidth, pack.ExecutionDefinition.ReferenceHeight, scales)
                : TemplateMatcher.Find(frame, template, task.Roi, task.TemplateThreshold,
                    pack.ExecutionDefinition.ReferenceWidth, pack.ExecutionDefinition.ReferenceHeight);
            if (match.Found && HasMatchingMarkerColors(frame, template, match, task.TemplateThreshold))
            {
                var options = needsReferenceScaling ? new HachimiPipelineRunOptions
                {
                    ScaleCandidatesOverrides = new Dictionary<string, IReadOnlyList<double>>(
                        StringComparer.OrdinalIgnoreCase) { [action.Task] = scales },
                } : null;
                readyActions.Add(actionId, (match, options));
            }
        }

        if (readyActions.Count == 0)
            return null;

        try
        {
            var recognized = await _visualRuntime.DetectTextAsync(
                    frame, roi, pack.ScreenProfile.ReferenceWidth, pack.ScreenProfile.ReferenceHeight,
                    "en-US", $"career_event.{screenId}.title", cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (recognized is null)
                return null;

            var title = string.Join(" ", recognized.Detections
                .OrderBy(item => item.Bounds.Y).ThenBy(item => item.Bounds.X)
                .Select(item => item.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
            var normalizedTitle = NormalizeTitle(title);
            if (normalizedTitle.Length == 0)
                return null;

            foreach (var item in events)
            {
                if (readyActions.TryGetValue(item.OcrTitle!.ActionId, out var readyAction)
                    && normalizedTitle.Equals(NormalizeTitle(item.Title), StringComparison.Ordinal))
                {
                    return new CareerEventTitleMatch(item, title, readyAction.Match, readyAction.Options);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not DateChangedInterruptionException and not DateChangedRecoveryException)
        {
            // A transient OCR failure is an observation miss, not an action.
        }

        return null;
    }

    private static bool HasMatchingMarkerColors(
        GrayImage frame, GrayImage template, TemplateMatchResult match, double threshold)
    {
        if (frame.RgbaPixels is not { } pixels || template.RgbaPixels is not { } expected)
            return false;

        // The grayscale hoof can correlate with clothing on a dialogue page.
        // Recheck the template's colored glyph at that same location, excluding
        // its white backing, so a bright patch cannot establish choice readiness.
        var colorError = 0d;
        var count = 0;
        for (var y = 0; y < template.Height; y++)
        {
            for (var x = 0; x < template.Width; x++)
            {
                var offset = (y * template.Width + x) * 4;
                var red = expected[offset];
                var green = expected[offset + 1];
                var blue = expected[offset + 2];
                if (expected[offset + 3] < 128
                    || Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) < 40)
                {
                    continue;
                }

                var screenX = match.X + Math.Min(match.Width - 1,
                    (int)((x + 0.5) * match.Width / template.Width));
                var screenY = match.Y + Math.Min(match.Height - 1,
                    (int)((y + 0.5) * match.Height / template.Height));
                var screenOffset = (screenY * frame.Width + screenX) * 4;
                colorError += Math.Abs(pixels[screenOffset] - red)
                    + Math.Abs(pixels[screenOffset + 1] - green)
                    + Math.Abs(pixels[screenOffset + 2] - blue);
                count++;
            }
        }

        return count > 0 && 1d - colorError / (count * 765d) >= Math.Clamp(threshold, 0, 1);
    }

    internal static string NormalizeTitle(string title)
    {
        var normalized = new StringBuilder();
        foreach (var rune in title.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
                normalized.Append(Rune.ToUpperInvariant(rune).ToString());
        }
        return normalized.ToString();
    }
}

internal sealed record CareerEventTitleMatch(
    UraEventDefinition Event,
    string Title,
    TemplateMatchResult OptionMatch,
    HachimiPipelineRunOptions? RunOptions = null)
{
    public CareerObservation Observation => new(
        Event.OcrTitle!.ScreenId,
        OptionMatch.Score,
        EventId: Event.EventId,
        EventTitle: Title);
}
