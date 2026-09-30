using System.Collections.Concurrent;
using System.Threading;
using System.Windows;
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

    internal static bool IsInitialResumeCandidate(string screenId) =>
        IsRuntimeCareerScreen(screenId);

    internal static bool IsEligibleForCareerPhase(
        string screenId,
        UraCareerSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return IsRuntimeCareerScreen(screenId)
            && (!screenId.Equals("career_epithet", StringComparison.OrdinalIgnoreCase)
                || state.LastScreenId.Equals("career_result_close", StringComparison.OrdinalIgnoreCase)
                || state.LastScreenId.Equals("career_epithet", StringComparison.OrdinalIgnoreCase))
            && (!screenId.Equals("career_rating_record_updated", StringComparison.OrdinalIgnoreCase)
                || state.LastScreenId.Equals("career_result_close", StringComparison.OrdinalIgnoreCase)
                || state.LastScreenId.Equals("career_rating_record_updated", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsReturningHome(UraCareerSessionState state) =>
        state.CareerStarted
        && (state.LastScreenId is "career_complete" or "home_unselected");

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

        // Finishing Career is a one-way flow. A short race-list message can
        // match text in the finish dialog, so do not re-enter race or turn
        // handling after a settlement screen has been observed.
        var settlementInProgress = CareerScreenClassification.Classify(state.LastScreenId)
            == CareerScreenKind.Settlement;
        var returningHome = IsReturningHome(state);

        var candidates = pack.ScreenProfile.Screens
            .Where(screen => !careerOnly || IsInitialResumeCandidate(screen.ScreenId))
            // Once the run has reached the Career turn screen, only Career
            // runtime screens are valid. Startup dialogs such as
            // support_autofill_confirmation use a generic green OK-button
            // template and can otherwise collide with the Rest confirmation
            // dialog after clicking Rest.
            .Where(screen => IsEligibleForCareerPhase(screen.ScreenId, state)
                || (careerOnly
                    && (screen.ScreenId is "career_epithet" or "career_rating_record_updated"))
                || (careerStartTransitionExpected
                    && screen.ScreenId == "career_intro_event")
                || (returningHome && (screen.ScreenId is "home" or "home_unselected")))
            .Where(screen => careerOnly
                || !settlementInProgress
                || CareerScreenClassification.Classify(screen.ScreenId)
                    == CareerScreenKind.Settlement
                || (returningHome && (screen.ScreenId is "home" or "home_unselected")))
            .Where(screen => !returningHome
                || (screen.ScreenId is "career_complete" or "home" or "home_unselected"))
            .Where(screen => !string.Equals(
                    screen.ScreenId,
                    "inheritance_event",
                    StringComparison.OrdinalIgnoreCase)
                || state.InheritanceEventPending
                || careerOnly)
            // Once the late-March action is selected, wait for the GO overlay
            // instead of starting another turn from the underlying main page.
            .Where(screen => !state.InheritanceEventPending
                || !string.Equals(
                    screen.ScreenId,
                    "career_main",
                    StringComparison.OrdinalIgnoreCase))
            // Probe the banner after each non-race turn action or qualifying
            // race result. Keep it out of ordinary turn observations; the two
            // Next pages are enabled by the recognized goal sequence.
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

                    // The finale template is a full screenshot, while its
                    // distinctive marker is the gold GOAL title. Matching the
                    // full image also samples the unrelated character and background.
                    var finaleHeaderRoi = screen.ScreenId.Equals(
                            "goal_complete", StringComparison.OrdinalIgnoreCase)
                        ? screen.Recognition.RequiredTemplateRoi
                        : null;
                    var match = finaleHeaderRoi is [var x, var y, var width, var height]
                        ? MatchGoalCompleteHeader(
                            frame, grayTemplate, x, y, width, height,
                            finaleHeaderRoi,
                            screen.Recognition.RequiredTemplateThreshold,
                            pack)
                        : screen.Recognition.MatchAlphaTemplate
                        ? TemplateMatcher.FindColor(
                            frame,
                            grayTemplate,
                            roi: screen.Recognition.Roi,
                            threshold: screen.Recognition.TemplateThreshold,
                            pack.ScreenProfile.ReferenceWidth,
                            pack.ScreenProfile.ReferenceHeight)
                        : screen.Recognition.MatchColorText
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
                        && finaleHeaderRoi is not [_, _, _, _]
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

                // View Results can open the placings page directly, without
                // showing the Replay label used by the playback-result path.
                // In that case the known runner checkpoint and pending race
                // let its bottom Next button identify the result page safely.
                if (screenBest is null
                    && screen.ScreenId.Equals(
                        "race_runner_result",
                        StringComparison.OrdinalIgnoreCase)
                    && state.HasPendingRace
                    && state.RaceStrategyConfigured
                    && state.LastScreenId.Equals(
                        CareerRaceRunnerCheckpointHandler.ScreenId,
                        StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(screen.Recognition.RequiredTemplate))
                {
                    var nextTemplate = await LoadTemplateCachedAsync(
                            ResolveCapture(pack, screen.Recognition.RequiredTemplate),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (nextTemplate is not null)
                    {
                        var nextMatch = TemplateMatcher.Find(
                            frame,
                            nextTemplate,
                            roi: screen.Recognition.RequiredTemplateRoi,
                            threshold: screen.Recognition.RequiredTemplateThreshold,
                            pack.ScreenProfile.ReferenceWidth,
                            pack.ScreenProfile.ReferenceHeight);
                        if (nextMatch.Found)
                        {
                            screenBest = new CareerObservation(
                                screen.ScreenId,
                                nextMatch.Score);
                        }
                    }
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
                    frames.Count == 2 ? [ocrFrame, frames[0]] : [ocrFrame],
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

    private static TemplateMatchResult MatchGoalCompleteHeader(
        GrayImage frame,
        GrayImage template,
        int x,
        int y,
        int width,
        int height,
        int[] roi,
        double threshold,
        UraScenarioPack pack)
    {
        var goldHeader = GrayImageCodec.Crop(
            template, new Int32Rect(x, y, width, height));
        return goldHeader is null
            ? new TemplateMatchResult(false, 0, 0, 0, width, height)
            : TemplateMatcher.FindColor(
                frame,
                goldHeader,
                roi,
                threshold,
                pack.ScreenProfile.ReferenceWidth,
                pack.ScreenProfile.ReferenceHeight);
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
            "inheritance_event" => -3,
            "career_intro_event" when careerStartTransitionExpected => 0,
            "career_main" when careerStartTransitionExpected => 1,
            "career_races_ready" when careerStartTransitionExpected => 2,
            "event_choice" => 0,
            "claw_machine" => -1,
            "claw_machine_result" => -2,
            "training_event" => 1,
            "scenario_event" => 2,
            // The optional Race Recommendations dialog can collide with the
            // generic event-choice button template; its title identifies it
            // before that broader overlay check runs.
            "race_recommendations" => -1,
            "race_retry_dialog" => -2,
            // The Epithet title is specific, unlike its generic Confirm! button.
            "career_epithet" => -4,
            // The Rating Record Updated banner is specific to the post-settlement continuation.
            "career_rating_record_updated" => -4,
            // The follow-limit popup can obscure any settlement page beneath it.
            "follow_trainer_limit" => -5,
            // The gift-box overlay obscures the underlying settlement reward page.
            "event_reward" => -5,
            // The Finish dialog contains text that resembles generic race
            // notices; recognize its specific green button first.
            "complete_career" => -3,
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
            // The no-races notice appears inside Race List, so it must be
            // checked before the broader list-header template.
            "race_list_empty" => 10,
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

        // A resumed session can start directly on race_runner, where the
        // objective text is no longer visible. Once its result flow finishes,
        // admit the goal page even though the objective-specific probe could
        // not be armed from the missing goal classification.
        var resumedRaceFlowCompleted = state.RaceReplayFlowCompleted
            && state.LastScreenId.Equals(
                CareerRaceRunnerCheckpointHandler.ScreenId,
                StringComparison.OrdinalIgnoreCase);

        return screenId switch
        {
            "goal_objective_complete" => state.GoalCompletionProbeArmed
                || resumedRaceFlowCompleted,
            "goal_update" => state.LastScreenId.Equals(
                "goal_objective_complete",
                StringComparison.OrdinalIgnoreCase),
            "goal_complete" => state.GoalCompletionProbeArmed
                || resumedRaceFlowCompleted
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
