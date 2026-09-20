using System.Collections.Concurrent;
using System.Threading;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public sealed class CareerScreenObserver
{
    private const double EarlyRecognitionThreshold = 0.985;

    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly ConcurrentDictionary<string, Lazy<Task<GrayImage?>>> _templateCache = new(
        StringComparer.OrdinalIgnoreCase);

    public CareerScreenObserver(IVisualPipelineRuntime visualRuntime)
    {
        _visualRuntime = visualRuntime ?? throw new ArgumentNullException(nameof(visualRuntime));
    }

    internal static bool IsRuntimeCareerScreen(string screenId) => screenId is
        "career_intro_event"
            or "career_main"
            or "career_races_ready"
            or "training_selection"
            or "training_result"
            or "training_event"
            or "rest_result"
            or "event_choice"
            or "rest_confirmation"
            or "race_day"
            or "race_list"
            or "race_details"
            or "race_attributes"
            or "race_playback"
            or "race_playback_settings"
            or "race_live"
            or "goal_update"
            or "race_result"
            or "reward"
            or "reward_support"
            or "goal_complete"
            or "scenario_event"
            or "complete_career"
            or "career_rank"
            or "career_result"
            or "rewards"
            or "sparks"
            or "sparks_confirmation"
            or "career_complete";

    public async Task<CareerObservation?> ObserveAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        UraCareerSessionState state,
        bool careerStartTransitionExpected,
        CancellationToken cancellationToken,
        bool careerOnly = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(state);

        var candidates = pack.ScreenProfile.Screens
            .Where(screen => careerOnly
                ? IsRuntimeCareerScreen(screen.ScreenId)
                : !string.Equals(screen.ScreenId, "race_live", StringComparison.OrdinalIgnoreCase))
            .Where(screen => careerOnly
                || state.CareerStarted
                || state.TurnIndex > 0
                || screen.ScreenId is "career_intro_event"
                    or "career_main"
                    or "career_races_ready")
            .Where(screen => !careerStartTransitionExpected
                || screen.ScreenId is "career_intro_event"
                    or "career_main"
                    or "career_races_ready")
            .OrderBy(screen => GetScreenRecognitionPriority(
                screen.ScreenId,
                careerStartTransitionExpected))
            .ToArray();

        var frames = new List<GrayImage>(capacity: 2);
        for (var sample = 0; sample < 2; sample++)
        {
            var frame = await _visualRuntime.CaptureGrayAsync(
                    connection,
                    cancellationToken)
                .ConfigureAwait(false);
            if (frame is not null)
                frames.Add(frame);
            if (sample == 0)
                await _visualRuntime.DelayAsync(120, cancellationToken)
                    .ConfigureAwait(false);
        }

        if (frames.Count == 0)
            return null;

        CareerObservation? best = null;
        foreach (var frame in frames)
        {
            CareerObservation? frameBest = null;
            foreach (var screen in candidates)
            {
                foreach (var template in screen.Templates)
                {
                    var path = ResolveCapture(pack, template);
                    var grayTemplate = await LoadTemplateCachedAsync(path, cancellationToken)
                        .ConfigureAwait(false);
                    if (grayTemplate is null)
                        continue;

                    var match = TemplateMatcher.Find(
                        frame,
                        grayTemplate,
                        roi: screen.Recognition.Roi,
                        threshold: screen.Recognition.TemplateThreshold,
                        pack.ScreenProfile.ReferenceWidth,
                        pack.ScreenProfile.ReferenceHeight);
                    if (match.Found
                        && (frameBest is null || match.Score > frameBest.Score))
                    {
                        frameBest = new CareerObservation(screen.ScreenId, match.Score);
                    }

                    if (frameBest is { Score: >= EarlyRecognitionThreshold })
                        break;
                }

                if (frameBest is { Score: >= EarlyRecognitionThreshold })
                    break;
            }

            if (frameBest is not null && (best is null || frameBest.Score > best.Score))
                best = frameBest;
        }

        return best;
    }

    private static int GetScreenRecognitionPriority(
        string screenId,
        bool careerStartTransitionExpected) =>
        screenId switch
        {
            "career_intro_event" when careerStartTransitionExpected => 0,
            "career_main" when careerStartTransitionExpected => 1,
            "career_races_ready" when careerStartTransitionExpected => 2,
            "career_races_ready" => 0,
            "career_main" => 1,
            "training_selection" => 2,
            "race_day" => 3,
            "race_list" => 4,
            "race_details" => 5,
            "race_attributes" => 6,
            "race_playback_settings" => 7,
            "race_playback" => 8,
            _ => 20,
        };

    private Task<GrayImage?> LoadTemplateCachedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var lazy = _templateCache.GetOrAdd(
            path,
            key => new Lazy<Task<GrayImage?>>(
                () => _visualRuntime.LoadTemplateAsync(
                    key,
                    string.Empty,
                    CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value.WaitAsync(cancellationToken);
    }

    private static string ResolveCapture(UraScenarioPack pack, string relativePath) =>
        UraScenarioResourceResolver.Resolve(pack, relativePath);
}
