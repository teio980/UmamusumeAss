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

    internal static bool IsRuntimeCareerScreen(string screenId) =>
        CareerScreenClassification.IsRuntimeScreen(screenId);

    internal static bool IsEligibleForCareerPhase(
        string screenId,
        UraCareerSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return (!state.CareerStarted && state.TurnIndex <= 0)
            || IsRuntimeCareerScreen(screenId);
    }

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
            .Where(screen => !careerOnly || IsRuntimeCareerScreen(screen.ScreenId))
            // Once the run has reached the Career turn screen, only Career
            // runtime screens are valid. Startup dialogs such as
            // support_autofill_confirmation use a generic green OK-button
            // template and can otherwise collide with the Rest confirmation
            // dialog after clicking Rest.
            .Where(screen => IsEligibleForCareerPhase(screen.ScreenId, state))
            .Where(screen => careerOnly
                || state.CareerStarted
                || state.TurnIndex > 0
                || screen.ScreenId is "career_intro_event"
                    or "career_main"
                    or "career_races_ready"
                    or "training_selection"
                    or "training_result"
                    or "training_event")
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
                        var energyDefinition = screen.Observations.EnergyBar;
                        var energy = energyDefinition is not null
                            ? CareerEnergyBarReader.TryMeasure(
                                frame,
                                energyDefinition,
                                pack.ScreenProfile.ReferenceWidth,
                                pack.ScreenProfile.ReferenceHeight)
                            : null;
                        if (energy is not null
                            && energyDefinition is not null
                            && energy.Confidence < Math.Clamp(
                                energyDefinition.MinimumConfidence,
                                0,
                                1))
                        {
                            energy = null;
                        }

                        frameBest = new CareerObservation(
                            screen.ScreenId,
                            match.Score,
                            energy?.Percent,
                            energy?.Confidence ?? 0);
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

        if (best?.ScreenId.Equals("career_main", StringComparison.OrdinalIgnoreCase) == true)
        {
            var careerMain = pack.ScreenProfile.Find("career_main");
            var turnText = await ReadRegionTextAsync(
                    connection,
                    careerMain?.FindOcrRegion("scenario.phase")?.ToRoi(),
                    pack.ScreenProfile.ReferenceWidth,
                    pack.ScreenProfile.ReferenceHeight,
                    "career_main.turn_position",
                    cancellationToken)
                .ConfigureAwait(false);
            var turnsLeftText = await ReadRegionTextAsync(
                    connection,
                    careerMain?.FindOcrRegion("objective.turns_left")?.ToRoi(),
                    pack.ScreenProfile.ReferenceWidth,
                    pack.ScreenProfile.ReferenceHeight,
                    "career_main.objective.turns_left",
                    cancellationToken)
                .ConfigureAwait(false);
            var goalText = await ReadRegionTextAsync(
                    connection,
                    careerMain?.FindOcrRegion("objective.title")?.ToRoi(),
                    pack.ScreenProfile.ReferenceWidth,
                    pack.ScreenProfile.ReferenceHeight,
                    "career_main.objective.title",
                    cancellationToken)
                .ConfigureAwait(false);

            best = best with
            {
                TurnPositionText = turnText,
                TurnsToGoal = CareerGoalTextParser.ParseTurnsLeft(turnsLeftText),
                GoalText = goalText,
                FansToGoal = CareerGoalTextParser.ParseFansToGo(goalText),
            };
        }

        return best;
    }

    private async Task<string?> ReadRegionTextAsync(
        LastVerifiedConnection connection,
        int[]? bounds,
        int referenceWidth,
        int referenceHeight,
        string taskName,
        CancellationToken cancellationToken)
    {
        if (bounds is not { Length: >= 4 })
            return null;

        ScreenTextRecognitionResult? recognized;
        try
        {
            recognized = await _visualRuntime.DetectTextAsync(
                    connection,
                    bounds,
                    referenceWidth,
                    referenceHeight,
                    "en-US",
                    taskName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Turn OCR is an optional state reconstruction signal. A missing
            // OCR runtime must not discard the already recognized career page.
            return null;
        }

        if (recognized is null)
            return null;

        var candidates = recognized.Detections
            .OrderBy(item => item.Bounds.Y)
            .ThenBy(item => item.Bounds.X)
            .Select(item => item.Text)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
        return candidates.Length == 0
            ? null
            : string.Join(" ", candidates);
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
            "training_result" => 2,
            "training_event" => 3,
            "training_selection" => 4,
            "race_day" => 5,
            "race_list" => 6,
            "race_runner" => 7,
            "race_details" => 8,
            "race_attributes" => 9,
            "race_playback_settings" => 10,
            "race_playback" => 11,
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
