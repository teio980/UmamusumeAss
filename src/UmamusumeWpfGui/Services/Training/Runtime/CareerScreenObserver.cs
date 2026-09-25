using System.Collections.Concurrent;
using System.Threading;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public sealed class CareerScreenObserver
{
    private const double EarlyRecognitionThreshold = 0.985;
    private const int ExpectedActionConfirmationRecognitionPriority = 35;
    private const int RecognitionPriorityScale = 10;

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
        return IsRuntimeCareerScreen(screenId);
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

        // Rest and infirmary actions from Career Main have narrow expected
        // transitions. Give their confirmation dialogs early recognition
        // slots while retaining higher-priority event and goal overlays.
        string? expectedActionConfirmationScreenId = null;
        if (!careerStartTransitionExpected
            && state.LastScreenId.Equals("career_main", StringComparison.OrdinalIgnoreCase))
        {
            expectedActionConfirmationScreenId = state.LastAction switch
            {
                UraPlannedAction.Rest =>
                    state.CalendarStage == UraCalendarStage.SummerCamp
                        ? "summer_rest_confirmation"
                        : "rest_confirmation",
                UraPlannedAction.Infirmary => "infirmary_confirmation",
                _ => null,
            };
        }

        var candidates = pack.ScreenProfile.Screens
            .Where(screen => !careerOnly || IsRuntimeCareerScreen(screen.ScreenId))
            // Once the run has reached the Career turn screen, only Career
            // runtime screens are valid. Startup dialogs such as
            // support_autofill_confirmation use a generic green OK-button
            // template and can otherwise collide with the Rest confirmation
            // dialog after clicking Rest.
            .Where(screen => IsEligibleForCareerPhase(screen.ScreenId, state)
                || (careerStartTransitionExpected
                    && screen.ScreenId == "career_intro_event"))
            // These templates are relatively expensive and only matter right
            // after the last action before a goal. Keep them out of ordinary
            // turn observations; the two Next pages are enabled only as part
            // of the already-recognized goal sequence.
            .Where(screen => IsGoalFlowScreenEligible(screen.ScreenId, state))
            .Where(screen => careerOnly
                || state.CareerStarted
                || state.TurnIndex > 0
                || (careerStartTransitionExpected
                    && screen.ScreenId == "career_intro_event")
                || screen.ScreenId is "career_main"
                    or "career_races_ready"
                    or "training_selection"
                    or "training_result"
                    or "training_event"
                    or "goal_objective_complete"
                    or "goal_update"
                    or "goal_complete")
            .Where(screen => !careerStartTransitionExpected
                || screen.ScreenId is "career_intro_event"
                    or "career_main"
                    or "career_races_ready")
            .OrderBy(screen => GetCandidateRecognitionPriority(
                screen.ScreenId,
                careerStartTransitionExpected,
                expectedActionConfirmationScreenId))
            .ToArray();

        var frames = new List<GrayImage>(capacity: 2);
        for (var sample = 0; sample < 2; sample++)
        {
            GrayImage? frame;
            try
            {
                frame = await _visualRuntime.CaptureGrayAsync(
                        connection,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A single dropped screenshot is a transient observation
                // miss. Returning null lets the engine use its bounded
                // recognition retry window instead of failing the whole
                // Career run immediately.
                frame = null;
            }
            if (frame is not null)
                frames.Add(frame);
            if (sample == 0)
                await _visualRuntime.DelayAsync(120, cancellationToken)
                    .ConfigureAwait(false);
        }

        if (frames.Count == 0)
            return null;

        CareerObservation? best = null;
        var bestPriority = int.MaxValue;
        var mainFrameCount = 0;
        var frameObservations = new List<CareerObservation?>(capacity: frames.Count);
        foreach (var frame in frames)
        {
            CareerObservation? frameBest = null;
            foreach (var screen in candidates)
            {
                CareerObservation? screenBest = null;
                foreach (var template in screen.Templates)
                {
                    var path = ResolveCapture(pack, template);
                    var grayTemplate = await LoadTemplateCachedAsync(path, cancellationToken)
                        .ConfigureAwait(false);
                    if (grayTemplate is null)
                        continue;

                    var match = screen.Recognition.MatchColorText
                        ? TemplateMatcher.FindColor(
                            frame,
                            grayTemplate,
                            roi: screen.Recognition.Roi,
                            threshold: screen.Recognition.TemplateThreshold,
                            pack.ScreenProfile.ReferenceWidth,
                            pack.ScreenProfile.ReferenceHeight,
                            requireTextContrast: true)
                        : TemplateMatcher.Find(
                            frame,
                            grayTemplate,
                            roi: screen.Recognition.Roi,
                            threshold: screen.Recognition.TemplateThreshold,
                            pack.ScreenProfile.ReferenceWidth,
                            pack.ScreenProfile.ReferenceHeight);
                    if (match.Found
                        && !await MatchesRequiredTemplateAsync(
                                frame,
                                screen,
                                pack,
                                cancellationToken)
                            .ConfigureAwait(false))
                    {
                        continue;
                    }

                    if (match.Found
                        && (screenBest is null || match.Score > screenBest.Score))
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

                        screenBest = new CareerObservation(
                            screen.ScreenId,
                            match.Score,
                            energy?.Percent,
                            energy?.Confidence ?? 0);
                    }

                    if (screenBest is { Score: >= EarlyRecognitionThreshold })
                        break;
                }

                // Screens are ordered from specific dialogs/events to the
                // underlying Career page. An overlay can leave part of the
                // main screen visible, so a recognized event or result wins.
                if (screenBest is not null)
                {
                    frameBest = screenBest;
                    break;
                }
            }

            frameObservations.Add(frameBest);
            if (frameBest is null)
                continue;

            if (frameBest.ScreenId.Equals("career_main", StringComparison.OrdinalIgnoreCase))
                mainFrameCount++;

            var framePriority = GetScreenRecognitionPriority(
                frameBest.ScreenId,
                careerStartTransitionExpected);
            if (best is null
                || framePriority < bestPriority
                || (framePriority == bestPriority
                    && (frameBest.ScreenId.Equals(best.ScreenId, StringComparison.OrdinalIgnoreCase)
                        || frameBest.Score > best.Score)))
            {
                best = frameBest;
                bestPriority = framePriority;
            }
        }

        // A screen marked stable must be the winning recognition in every
        // captured sample. In particular, transient race-result frames during
        // FinalNext navigation must not re-enter the completed result flow.
        if (best is not null
            && pack.ScreenProfile.Find(best.ScreenId)?.Recognition.Stable == true)
        {
            var stableScreenId = best.ScreenId;
            if (frames.Count != 2
                || frameObservations.Count != 2
                || frameObservations.Any(observation =>
                    !string.Equals(
                        observation?.ScreenId,
                        stableScreenId,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
        }

        // The Career header and Training button can remain visible while an
        // event overlay animates in. Both sampled frames must be the main
        // page before its energy reading can drive another turn action.
        if (best?.ScreenId.Equals("career_main", StringComparison.OrdinalIgnoreCase) == true
            && mainFrameCount != 2)
        {
            return null;
        }

        if (best?.ScreenId.Equals("career_main", StringComparison.OrdinalIgnoreCase) == true)
        {
            var availableTemplate = await LoadTemplateCachedAsync(
                    ResolveCapture(pack, CareerInfirmaryDetector.AvailableTemplatePath),
                    cancellationToken)
                .ConfigureAwait(false);
            var unavailableTemplate = await LoadTemplateCachedAsync(
                    ResolveCapture(pack, CareerInfirmaryDetector.UnavailableTemplatePath),
                    cancellationToken)
                .ConfigureAwait(false);
            var infirmaryAvailable = availableTemplate is not null
                && unavailableTemplate is not null
                && frames.Count == 2
                && frames.All(frame => CareerInfirmaryDetector.IsAvailable(
                    frame,
                    availableTemplate,
                    unavailableTemplate,
                    pack.ScreenProfile.ReferenceWidth,
                    pack.ScreenProfile.ReferenceHeight));
            var careerMain = pack.ScreenProfile.Find("career_main");
            var turnsLeftRoi = careerMain?.FindOcrRegion("objective.turns_left")?.ToRoi();
            var ocrFrame = frames[^1];

            var turnText = await ReadRegionTextAsync(
                    ocrFrame,
                    careerMain?.FindOcrRegion("scenario.phase")?.ToRoi(),
                    pack.ScreenProfile.ReferenceWidth,
                    pack.ScreenProfile.ReferenceHeight,
                    "career_main.turn_position",
                    cancellationToken)
                .ConfigureAwait(false);
            var turnsLeftText = await ReadRegionTextAsync(
                    ocrFrame,
                    turnsLeftRoi,
                    pack.ScreenProfile.ReferenceWidth,
                    pack.ScreenProfile.ReferenceHeight,
                    "career_main.objective.turns_left",
                    cancellationToken)
                .ConfigureAwait(false);
            var goalText = await ReadRegionTextAsync(
                    ocrFrame,
                    careerMain?.FindOcrRegion("objective.title")?.ToRoi(),
                    pack.ScreenProfile.ReferenceWidth,
                    pack.ScreenProfile.ReferenceHeight,
                    "career_main.objective.title",
                    cancellationToken)
                .ConfigureAwait(false);
            var moodText = await ReadRegionTextAsync(
                    ocrFrame,
                    careerMain?.FindOcrRegion("mood.current")?.ToRoi(),
                    pack.ScreenProfile.ReferenceWidth,
                    pack.ScreenProfile.ReferenceHeight,
                    "career_main.mood.current",
                    cancellationToken)
                .ConfigureAwait(false);
            var turnsToGoal = CareerGoalTextParser.ParseTurnsLeft(turnsLeftText);
            if (turnsToGoal is null && ocrFrame is not null)
            {
                turnsToGoal = await CareerCountdownOcrReader.TryReadAsync(
                    [ocrFrame],
                    turnsLeftRoi,
                    pack.ScreenProfile.ReferenceWidth,
                    pack.ScreenProfile.ReferenceHeight,
                    cancellationToken)
                    .ConfigureAwait(false);
            }

            best = best with
            {
                InfirmaryAvailable = infirmaryAvailable,
                TurnPositionText = turnText,
                TurnsToGoal = turnsToGoal,
                GoalText = goalText,
                MoodText = moodText,
                FansToGoal = CareerGoalTextParser.ParseFansToGo(goalText),
            };
        }

        if (best?.ScreenId is "race_day" or "race_list")
        {
            var raceScreen = pack.ScreenProfile.Find(best.ScreenId);
            var ocrFrame = frames[^1];
            var goalText = await ReadRegionTextAsync(
                    ocrFrame,
                    raceScreen?.FindOcrRegion(
                        best.ScreenId == "race_day" ? "race.objective" : "objective.title")?.ToRoi(),
                    pack.ScreenProfile.ReferenceWidth,
                    pack.ScreenProfile.ReferenceHeight,
                    $"{best.ScreenId}.objective.title",
                    cancellationToken)
                .ConfigureAwait(false);
            best = best with { GoalText = goalText };
        }

        return best;
    }

    private async Task<bool> MatchesRequiredTemplateAsync(
        GrayImage frame,
        UraScreenDefinition screen,
        UraScenarioPack pack,
        CancellationToken cancellationToken)
    {
        var requiredPath = screen.Recognition.RequiredTemplate;
        if (string.IsNullOrWhiteSpace(requiredPath))
            return true;

        var requiredTemplate = await LoadTemplateCachedAsync(
                ResolveCapture(pack, requiredPath),
                cancellationToken)
            .ConfigureAwait(false);
        if (requiredTemplate is null)
            return false;

        return TemplateMatcher.Find(
            frame,
            requiredTemplate,
            roi: screen.Recognition.RequiredTemplateRoi,
            threshold: screen.Recognition.RequiredTemplateThreshold,
            pack.ScreenProfile.ReferenceWidth,
            pack.ScreenProfile.ReferenceHeight).Found;
    }

    private async Task<string?> ReadRegionTextAsync(
        GrayImage? frame,
        int[]? bounds,
        int referenceWidth,
        int referenceHeight,
        string taskName,
        CancellationToken cancellationToken)
    {
        if (frame is null || bounds is not { Length: >= 4 })
            return null;

        ScreenTextRecognitionResult? recognized;
        try
        {
            recognized = await _visualRuntime.DetectTextAsync(
                    frame,
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
        bool careerStartTransitionExpected)
    {
        return screenId switch
        {
            "career_intro_event" when careerStartTransitionExpected => 0,
            "career_main" when careerStartTransitionExpected => 1,
            "career_races_ready" when careerStartTransitionExpected => 2,
            "event_choice" => 0,
            "training_event" => 1,
            "scenario_event" => 2,
            // The optional Race Recommendations dialog can collide with the
            // generic event-choice button template; its title identifies it
            // before that broader overlay check runs.
            "race_recommendations" => -1,
            "goal_objective_complete" => 3,
            "goal_update" => 3,
            "goal_complete" => 2,
            "training_result" => 4,
            "infirmary_confirmation" => 5,
            "recreation_selection" => 5,
            "recreation_confirmation" => 5,
            "summer_rest_confirmation" => 5,
            "rest_confirmation" => 5,
            "training_selection" => 6,
            "career_races_ready" => 7,
            // Race Day is an overlay on Career Main. Prefer its tight banner
            // template so a restart resumes at the race entry instead of
            // issuing another turn action on the page underneath.
            "race_day" => 8,
            "career_main" => 9,
            // The Race Details dialog overlays Race List, whose header can
            // remain visible underneath it. Prefer the dialog so its Race
            // confirmation button is handled instead of selecting again.
            "race_details" => 10,
            "race_list" => 11,
            // Trophy Won is an optional overlay over the runner page. It
            // must be recognized before runner so the hidden page cannot
            // receive a strategy click through the modal.
            "race_trophy_won" => 11,
            // Race! is a resumable checkpoint and must win over the broader
            // runner/playback templates, both of which can still be visible
            // underneath the button page after a restart.
            "race_playback_start" => 12,
            // Replay is the end-of-race marker for the reusable normal-race
            // flow. It is separate from the legacy race_result screen.
            "race_runner_result" => 13,
            "race_runner" => 14,
            "race_attributes" => 16,
            "race_playback_settings" => 17,
            "race_playback" => 18,
            _ => 20,
        };
    }

    private static int GetCandidateRecognitionPriority(
        string screenId,
        bool careerStartTransitionExpected,
        string? expectedActionConfirmationScreenId)
    {
        var priority = GetScreenRecognitionPriority(
            screenId,
            careerStartTransitionExpected);
        if (expectedActionConfirmationScreenId is null)
            return priority;

        // Keep event and goal overlays ahead of the expected action dialog,
        // but check that dialog before training-result and underlying screens.
        if (screenId.Equals(
                expectedActionConfirmationScreenId,
                StringComparison.OrdinalIgnoreCase))
        {
            return ExpectedActionConfirmationRecognitionPriority;
        }

        return priority * RecognitionPriorityScale;
    }

    private static bool IsGoalFlowScreenEligible(
        string screenId,
        UraCareerSessionState state)
    {
        if (!state.CareerStarted)
            return true;

        return screenId switch
        {
            "goal_objective_complete" => state.GoalCompletionProbeArmed,
            "goal_update" => state.LastScreenId.Equals(
                "goal_objective_complete",
                StringComparison.OrdinalIgnoreCase),
            "goal_complete" => state.GoalCompletionProbeArmed
                || state.LastScreenId.Equals(
                    "goal_objective_complete",
                    StringComparison.OrdinalIgnoreCase)
                || state.LastScreenId.Equals(
                    "goal_update",
                    StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

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
