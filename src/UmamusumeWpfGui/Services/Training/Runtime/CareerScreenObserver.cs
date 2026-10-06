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
    private readonly CareerEventTitleRecognizer _eventTitleRecognizer;
    private readonly ConcurrentDictionary<string, Lazy<Task<GrayImage?>>> _templateCache = new(
        StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<GrayImage> LastFrames { get; private set; } = [];
    internal IReadOnlyList<string> LastCandidateScreenIds { get; private set; } = [];
    internal IReadOnlyList<string?> LastFrameScreenIds { get; private set; } = [];
    internal IReadOnlyList<string> LastCaptureErrors { get; private set; } = [];

    public CareerScreenObserver(IVisualPipelineRuntime visualRuntime)
    {
        _visualRuntime = visualRuntime ?? throw new ArgumentNullException(nameof(visualRuntime));
        _eventTitleRecognizer = new CareerEventTitleRecognizer(visualRuntime);
    }

    internal static bool IsRuntimeCareerScreen(string screenId) =>
        CareerScreenClassification.IsRuntimeScreen(screenId);

    internal static bool IsRuntimeCareerScreen(
        string screenId,
        UraScreenProfile profile) =>
        CareerObservationPolicy.IsRuntimeCareerScreen(screenId, profile);

    internal static bool IsInitialResumeCandidate(string screenId) =>
        IsRuntimeCareerScreen(screenId);

    internal static bool IsInitialResumeCandidate(
        string screenId,
        UraScreenProfile profile) =>
        CareerObservationPolicy.IsInitialResumeCandidate(screenId, profile);

    internal static bool IsEligibleForCareerPhase(
        string screenId,
        UraCareerSessionState state)
    {
        return CareerObservationPolicy.IsEligibleForCareerPhase(screenId, state);
    }

    internal static bool IsEligibleForCareerPhase(
        string screenId,
        UraCareerSessionState state,
        UraScreenProfile profile) =>
        CareerObservationPolicy.IsEligibleForCareerPhase(screenId, state, profile);

    internal static bool IsReturningHome(UraCareerSessionState state) =>
        CareerObservationPolicy.IsReturningHome(state);

    public async Task<CareerObservation?> ObserveAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        UraCareerSessionState state,
        bool careerStartTransitionExpected,
        CancellationToken cancellationToken,
        bool careerOnly = false,
        bool resumeRecovery = false,
        bool includeMainDetails = true)
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
        var settlementInProgress = CareerScreenClassification.Classify(
                state.LastScreenId, pack.ScreenProfile)
            == CareerScreenKind.Settlement;
        var returningHome = IsReturningHome(state);

        var candidates = pack.ScreenProfile.Screens
            .Where(screen => CareerObservationPolicy.IsCandidate(
                screen,
                pack.ScreenProfile,
                state,
                careerOnly,
                careerStartTransitionExpected,
                resumeRecovery,
                settlementInProgress,
                returningHome))
            .OrderBy(screen => GetCandidateRecognitionPriority(
                screen,
                careerStartTransitionExpected,
                expectedActionConfirmationScreenId))
            .ThenBy(screen => pack.ScreenProfile.Screens.IndexOf(screen))
            .ToArray();

        LastCandidateScreenIds = candidates.Select(screen => screen.ScreenId).ToArray();
        LastFrameScreenIds = [];
        var captureErrors = new List<string>();
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
            catch (Exception exception) when (exception is not DateChangedInterruptionException and not DateChangedRecoveryException)
            {
                // A single dropped screenshot is a transient observation
                // miss. Returning null lets the engine use its bounded
                // recognition retry window instead of failing the whole
                // Career run immediately.
                captureErrors.Add($"{exception.GetType().Name}: {exception.Message}");
                frame = null;
            }
            if (frame is not null)
                frames.Add(frame);
            if (sample == 0)
                await _visualRuntime.DelayAsync(120, cancellationToken)
                    .ConfigureAwait(false);
        }

        LastFrames = frames.ToArray();
        LastCaptureErrors = captureErrors.ToArray();
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
                    var path = ResolveCapture(pack, screen, template);
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
                    var match = screen.ScreenId is "claw_machine" or "claw_machine_result"
                        ? CareerClawVision.MatchMarker(frame, grayTemplate,
                            pack.ScreenProfile, screen.Recognition)
                        : finaleHeaderRoi is [var x, var y, var width, var height]
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

                if (screenBest is null)
                {
                    var titleMatch = await _eventTitleRecognizer.RecognizeAsync(
                            frame, pack, screen.ScreenId, cancellationToken)
                        .ConfigureAwait(false);
                    screenBest = titleMatch?.Observation;
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
                            ResolveCapture(pack, screen, screen.Recognition.RequiredTemplate),
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
                    frameBest = screenBest with
                    {
                        ClassifiedKind = CareerScreenClassification.Classify(
                            screen.ScreenId, pack.ScreenProfile),
                    };
                    break;
                }
            }

            frameObservations.Add(frameBest);
            if (frameBest is null)
                continue;

            if (frameBest.ScreenId.Equals("career_main", StringComparison.OrdinalIgnoreCase))
                mainFrameCount++;

            var framePriority = pack.ScreenProfile.Find(frameBest.ScreenId) is { } observedScreen
                ? GetScreenRecognitionPriority(observedScreen, careerStartTransitionExpected)
                : int.MaxValue;
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

        LastFrameScreenIds = frameObservations.Select(observation => observation?.ScreenId).ToArray();

        // A screen marked stable must be the winning recognition in every
        // captured sample. In particular, transient race-result frames during
        // FinalNext navigation must not re-enter the completed result flow.
        if (best is not null
            && (best.EventId is not null
                || pack.ScreenProfile.Find(best.ScreenId)?.Recognition.Stable == true))
        {
            var stableScreenId = best.ScreenId;
            if (frames.Count != 2
                || frameObservations.Count != 2
                || frameObservations.Any(observation =>
                    !string.Equals(
                        observation?.ScreenId,
                        stableScreenId,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(observation?.EventId, best.EventId,
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

        if (includeMainDetails && best?.ScreenId.Equals("career_main", StringComparison.OrdinalIgnoreCase) == true)
        {
            var infirmaryAvailable = false;
            if (frames.Count == 2
                && CareerInfirmaryDetector.TryGetVisualPolicy(
                    pack.ScreenProfile,
                    out var infirmaryPolicy)
                && infirmaryPolicy is not null)
            {
                var availableTemplate = await LoadTemplateCachedAsync(
                        infirmaryPolicy.AvailableTemplatePath,
                        cancellationToken)
                    .ConfigureAwait(false);
                var unavailableTemplate = await LoadTemplateCachedAsync(
                        infirmaryPolicy.UnavailableTemplatePath,
                        cancellationToken)
                    .ConfigureAwait(false);
                infirmaryAvailable = availableTemplate is not null
                    && unavailableTemplate is not null
                    && frames.All(frame => CareerInfirmaryDetector.IsAvailable(
                        frame,
                        availableTemplate,
                        unavailableTemplate,
                        pack.ScreenProfile.ReferenceWidth,
                        pack.ScreenProfile.ReferenceHeight,
                        infirmaryPolicy));
            }
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

        if (best?.ScreenId is "race_day" or "race_list" or "race_streak_warning")
        {
            var raceScreen = pack.ScreenProfile.Find(best.ScreenId);
            var ocrFrame = frames[^1];
            var goalText = await ReadRegionTextAsync(
                    ocrFrame,
                    raceScreen?.FindOcrRegion(
                        best.ScreenId == "race_list" ? "objective.title" : "race.objective")?.ToRoi(),
                    pack.ScreenProfile.ReferenceWidth,
                    pack.ScreenProfile.ReferenceHeight,
                    best.ScreenId == "race_streak_warning"
                        ? "career_main.race_streak_warning.objective.title"
                        : $"{best.ScreenId}.objective.title",
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
                ResolveCapture(pack, screen, requiredPath),
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
        catch (Exception exception) when (exception is not DateChangedInterruptionException and not DateChangedRecoveryException)
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
        UraScreenDefinition screen,
        bool careerStartTransitionExpected)
    {
        if (careerStartTransitionExpected)
        {
            var startupOverride = screen.ScreenId switch
            {
                "career_intro_event" => 0,
                "career_main" => 1,
                "career_races_ready" => 2,
                _ => (int?)null,
            };
            if (startupOverride is int priority)
                return priority;
        }

        return CareerScreenClassification.GetRecognitionPriority(screen);
    }

    private static int GetCandidateRecognitionPriority(
        UraScreenDefinition screen,
        bool careerStartTransitionExpected,
        string? expectedActionConfirmationScreenId)
    {
        var priority = GetScreenRecognitionPriority(
            screen,
            careerStartTransitionExpected);
        if (expectedActionConfirmationScreenId is null)
            return priority * RecognitionPriorityScale;

        // Keep event and goal overlays ahead of the expected action dialog,
        // but check that dialog before training-result and underlying screens.
        if (screen.ScreenId.Equals(
                expectedActionConfirmationScreenId,
                StringComparison.OrdinalIgnoreCase))
        {
            return ExpectedActionConfirmationRecognitionPriority;
        }

        return priority * RecognitionPriorityScale;
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

    private static string ResolveCapture(
        UraScenarioPack pack,
        UraScreenDefinition screen,
        string relativePath) =>
        screen.SourceDirectory is not null
            ? UraScenarioResourceResolver.Resolve(pack, screen, relativePath)
            : UraScenarioResourceResolver.Resolve(pack, relativePath);
}
