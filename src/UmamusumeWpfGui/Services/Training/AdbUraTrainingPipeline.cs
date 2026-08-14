using System.Globalization;
using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public sealed class AdbUraTrainingPipeline : IUraTrainingPipeline
{
    private const double RecognitionThreshold = 0.78;

    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly IUmaDatabaseService _umaDatabase;
    private readonly UraTraineeSelector _traineeSelector;
    private readonly UraRaceResultRecognizer _raceResultRecognizer;
    private readonly HachimiJsonPipelineRunner _jsonRunner;
    private readonly object _runLock = new();
    private CancellationTokenSource? _runCancellation;

    public AdbUraTrainingPipeline(
        IVisualPipelineRuntime visualRuntime,
        IUmaDatabaseService umaDatabase,
        UraTraineeSelector traineeSelector,
        HachimiJsonPipelineRunner jsonRunner)
    {
        ArgumentNullException.ThrowIfNull(visualRuntime);
        ArgumentNullException.ThrowIfNull(umaDatabase);
        ArgumentNullException.ThrowIfNull(traineeSelector);
        ArgumentNullException.ThrowIfNull(jsonRunner);
        _visualRuntime = visualRuntime;
        _umaDatabase = umaDatabase;
        _traineeSelector = traineeSelector;
        _raceResultRecognizer = new UraRaceResultRecognizer(visualRuntime);
        _jsonRunner = jsonRunner;
    }

    public async Task<UraTrainingResult> RunAsync(
        LastVerifiedConnection connection,
        UraTrainingSettings settings,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(settings);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_runLock)
        {
            if (_runCancellation is not null)
            {
                return Failure("A URA training run is already in progress.", "busy");
            }

            _runCancellation = linked;
        }

        try
        {
            return await RunCoreAsync(connection, settings, logSink, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            logSink?.Add("URA Training", "URA training was stopped.", LogEntryKind.Failure);
            return Failure("URA training was stopped.", "canceled");
        }
        finally
        {
            lock (_runLock)
            {
                if (ReferenceEquals(_runCancellation, linked))
                    _runCancellation = null;
            }
        }
    }

    public Task<UraTrainingResult> StopAsync(
        LastVerifiedConnection connection,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_runLock)
        {
            _runCancellation?.Cancel();
        }

        logSink?.Add("URA Training", "Stop requested.");
        return Task.FromResult(new UraTrainingResult(true, "Stop requested.", 0, "stop"));
    }

    private async Task<UraTrainingResult> RunCoreAsync(
        LastVerifiedConnection connection,
        UraTrainingSettings settings,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(settings);

        if (!_umaDatabase.TryGetTrainee(settings.TraineeId, out var trainee)
            || trainee is null
            || !trainee.Available)
        {
            return Failure(
                $"Configured trainee ID {settings.TraineeId.ToString(CultureInfo.InvariantCulture)} "
                + "was not found or is unavailable.",
                "unknown");
        }

        ValidateSupportCards(settings.SupportCardIds);
        var pack = await UraScenarioPackLoader.LoadAsync(settings.ManifestPath, cancellationToken)
            .ConfigureAwait(false);
        logSink?.Add(
            "URA Training",
            $"Loaded {pack.Manifest.DisplayName} for {trainee.NameEn} ({trainee.TraineeId}).");

        var scenario = new UraScenarioModule(pack);
        var strategy = UraStrategyRegistry.Create(settings.StrategyId);
        var checkpointStore = new UraCheckpointStore(settings.TraineeId);
        var state = await checkpointStore.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? scenario.CreateInitialState();
        if (!string.Equals(state.ScenarioId, pack.Manifest.ScenarioId, StringComparison.OrdinalIgnoreCase))
            state = scenario.CreateInitialState();
        state.CareerEntryOpened = false;
        logSink?.Add(
            "URA Training",
            state.TurnIndex > 0
                ? $"Resuming checkpoint at turn {state.TurnIndex}, objective {state.CurrentObjectiveId}."
                : "Starting a new URA career session.");
        state.CareerEntryOpened = await EnsureCareerEntryAsync(
                connection,
                pack,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);

        var actionCount = 0;
        while (actionCount < 300)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = await ObserveAsync(connection, pack, state, cancellationToken)
                .ConfigureAwait(false);
            if (observation is null)
            {
                return Failure(
                    "Could not recognize a stable URA screen; automation paused safely.",
                    state.LastScreenId,
                    actionCount);
            }

            state.LastScreenId = observation.ScreenId;
            scenario.ObserveScreen(state, observation.ScreenId, observation.Score);
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            logSink?.Add(
                "URA Training",
                $"Recognized {observation.ScreenId} with score {observation.Score:0.000}.");

            if (observation.ScreenId == "home")
            {
                if (!state.CareerEntryOpened)
                {
                    state.CareerEntryOpened = await EnsureCareerEntryAsync(
                            connection,
                            pack,
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!state.CareerEntryOpened)
                    {
                        return Failure(
                            "Could not enter URA Career from the shared Home JSON entry flow.",
                            "home",
                            actionCount);
                    }

                    actionCount++;
                    continue;
                }

                await checkpointStore.ClearAsync(cancellationToken).ConfigureAwait(false);
                return new UraTrainingResult(
                    true,
                    "URA career completed and returned to Home.",
                    actionCount,
                    observation.ScreenId);
            }

            var terminal = await HandleScreenAsync(
                    connection,
                    pack,
                    settings,
                    scenario,
                    strategy,
                    state,
                    observation,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (terminal is not null)
            {
                if (terminal.Succeeded)
                    await checkpointStore.ClearAsync(cancellationToken).ConfigureAwait(false);
                else
                    await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                return terminal with { ActionsCompleted = actionCount };
            }

            actionCount++;
        }

        await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return Failure(
            "URA training exceeded the safety action limit and was paused.",
            state.LastScreenId,
            actionCount);
    }

    private async Task<UraTrainingResult?> HandleScreenAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        UraTrainingSettings settings,
        UraScenarioModule scenario,
        UraDefaultStrategy strategy,
        UraCareerSessionState state,
        UraObservation observation,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        switch (observation.ScreenId)
        {
            case "scenario_select":
            case "scenario_intro":
                return await RunScreenActionAsync(
                        connection, pack, observation.ScreenId, "next", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "trainee_select":
                return await RunScreenActionAsync(
                        connection,
                        pack,
                        "trainee_select",
                        "pick",
                        logSink,
                        cancellationToken,
                        new HachimiPipelineRunOptions
                        {
                            CustomActionExecutor = async (
                                    actionConnection,
                                    definition,
                                    taskName,
                                    task,
                                    actionLogSink,
                                    actionCancellationToken) =>
                            {
                                var selection = await _traineeSelector.SelectAsync(
                                        actionConnection,
                                        settings.TraineeId,
                                        task.SearchRois,
                                        actionCancellationToken)
                                    .ConfigureAwait(false);
                                return selection.Succeeded
                                    ? HachimiCustomActionResult.Success(selection.Message, selection.Match)
                                    : HachimiCustomActionResult.Failure(selection.Message);
                            }
                        })
                    .ConfigureAwait(false);
            case "support_select":
                if (settings.SupportCardIds.Count > 0)
                {
                    return Failure(
                        "Support card IDs were validated, but this profile has no "
                        + "support-card reference templates/selector yet; refusing to auto-fill "
                        + "a different deck.",
                        observation.ScreenId);
                }
                return await RunScreenActionAsync(
                        connection, pack, "support_select", "auto_fill", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "support_autofill_confirmation":
                return await RunScreenActionAsync(
                        connection,
                        pack,
                        "support_autofill_confirmation",
                        "autofill_ok",
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
            case "support_ready":
                return await RunScreenActionAsync(
                        connection, pack, "support_ready", "start", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "legacy_select":
                return await RunScreenActionAsync(
                        connection, pack, "legacy_select", "next", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "career_intro_event":
                return await RunScreenActionAsync(
                        connection, pack, "career_intro_event", "advance", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "career_main":
                return await HandleCareerMainAsync(
                        connection, pack, scenario, strategy, state, logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "career_races_ready":
                return await RunScreenActionAsync(
                        connection, pack, "career_races_ready", "races", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "career_entry":
                return await RunScreenActionAsync(
                        connection, pack, "career_entry", "start", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "training_selection":
                state.LastAction = UraPlannedAction.Training;
                return await RunScreenActionAsync(
                        connection, pack, "training_selection", "speed", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "training_result":
            case "training_event":
            case "rest_result":
                state.HasScenarioEvent = false;
                return await RunScreenActionAsync(
                        connection, pack, observation.ScreenId, "advance", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "event_choice":
                return await RunScreenActionAsync(
                        connection, pack, "event_choice", "choice_first", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "rest_confirmation":
                state.LastAction = UraPlannedAction.Rest;
                return await RunScreenActionAsync(
                        connection, pack, "rest_confirmation", "confirm", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "race_day":
                state.HasPendingRace = true;
                return await RunScreenActionAsync(
                        connection, pack, "race_day", "open_list", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "race_list":
                return await RunScreenActionAsync(
                        connection, pack, "race_list", "goal_entry", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "race_details":
                return await RunScreenActionAsync(
                        connection, pack, "race_details", "confirm", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "race_attributes":
                return await RunScreenActionAsync(
                        connection, pack, "race_attributes", "start_playback", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "race_playback":
                return await RunScreenActionAsync(
                        connection, pack, "race_playback", "play", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "race_playback_settings":
                return await RunScreenActionAsync(
                        connection,
                        pack,
                        "race_playback_settings",
                        "playback_settings_ok",
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
            case "race_live":
                return await RunScreenActionAsync(
                        connection, pack, "race_live", "live_next", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "goal_update":
                return await RunScreenActionAsync(
                        connection, pack, "goal_update", "update_next", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "race_result":
                var currentRace = scenario.CurrentRace(state);
                if (currentRace is null)
                    return Failure(
                        "Race result was shown but the scenario has no current race.",
                        observation.ScreenId);

                var placementObservation = await _raceResultRecognizer.RecognizeAsync(
                        connection,
                        pack,
                        currentRace,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (placementObservation is null)
                {
                    return Failure(
                        $"Could not confirm the placement for race '{currentRace.RaceId}' "
                        + "from a data-backed result template; automation paused safely.",
                        observation.ScreenId);
                }

                try
                {
                    scenario.ApplyRaceResult(
                        state,
                        placementObservation.Placement,
                        placementObservation.Confidence);
                }
                catch (UraUnknownOutcomeException ex)
                {
                    return Failure(ex.Message, observation.ScreenId);
                }
                return await RunScreenActionAsync(
                        connection, pack, "race_result", "next", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "reward":
                return await RunScreenActionAsync(
                        connection, pack, "reward", "next", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "reward_support":
                return await RunScreenActionAsync(
                        connection, pack, "reward_support", "next", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "goal_complete":
                state.HasPendingRace = true;
                return await RunScreenActionAsync(
                        connection, pack, "goal_complete", "next", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "scenario_event":
                state.HasScenarioEvent = false;
                return await RunScreenActionAsync(
                        connection, pack, "scenario_event", "advance", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "complete_career":
                return await RunScreenActionAsync(
                        connection, pack, "complete_career", "finish", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "career_rank":
                return await RunScreenActionAsync(
                        connection, pack, "career_rank", "next", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "career_result":
                return await RunScreenActionAsync(
                        connection, pack, "career_result", "next", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "rewards":
                return await RunScreenActionAsync(
                        connection, pack, "rewards", "next", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "sparks":
                return await RunScreenActionAsync(
                        connection, pack, "sparks", "confirm", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "sparks_confirmation":
                return await RunScreenActionAsync(
                        connection, pack, "sparks_confirmation", "keep", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "career_complete":
                return await RunScreenActionAsync(
                        connection, pack, "career_complete", "to_home", logSink, cancellationToken)
                    .ConfigureAwait(false);
            default:
                if (settings.PauseOnUnknownOutcome)
                {
                    logSink?.Add(
                        "URA Training",
                        $"Unknown or unsupported stable screen '{observation.ScreenId}'; paused.",
                        LogEntryKind.Failure);
                    return Failure(
                        $"Unsupported stable screen '{observation.ScreenId}'.",
                        observation.ScreenId);
                }

                return null;
        }
    }

    private async Task<UraTrainingResult?> HandleCareerMainAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        UraScenarioModule scenario,
        UraDefaultStrategy strategy,
        UraCareerSessionState state,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var decision = strategy.ChooseTurnAction(scenario, state);
        var availableActions = scenario.GetAvailableActions(state, "career_main");
        if (!availableActions.Contains(decision.Action))
        {
            return Failure(
                $"URA strategy selected unavailable action '{decision.Action}' in phase '{state.PhaseId}'.",
                "career_main");
        }
        logSink?.Add("URA Strategy", decision.Reason);
        var actionId = decision.Action switch
        {
            UraPlannedAction.Rest => "rest",
            UraPlannedAction.FinaleRace => "finale_races",
            _ => "training",
        };
        state.LastAction = decision.Action;
        return await RunScreenActionAsync(
                connection,
                pack,
                "career_main",
                actionId,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> EnsureCareerEntryAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var screen = pack.ScreenProfile.Find("home");
        if (screen is null || string.IsNullOrWhiteSpace(screen.EntryTask))
        {
            logSink?.Add(
                "URA Training",
                "Home entryTask is missing from screen_profile.json.",
                LogEntryKind.Failure);
            return false;
        }

        var result = await _jsonRunner.RunAsync(
                connection,
                pack.ExecutionDefinition,
                screen.EntryTask,
                logSink: logSink,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            logSink?.Add(
                "URA Training",
                $"Could not enter Career from the shared Home entry task: {result.Message}",
                LogEntryKind.Failure);
            return false;
        }

        logSink?.Add("URA Training", "Opened Career from the shared Home tab.");
        return true;
    }

    private async Task<UraObservation?> ObserveAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        UraCareerSessionState state,
        CancellationToken cancellationToken)
    {
        var candidates = pack.ScreenProfile.Screens
            .Where(screen => !string.Equals(screen.ScreenId, "race_live", StringComparison.OrdinalIgnoreCase))
            .OrderBy(screen => screen.ScreenId switch
            {
                "home" => 0,
                "scenario_select" => 1,
                "trainee_select" => 2,
                "support_select" => 3,
                "support_ready" => 4,
                "career_races_ready" => 5,
                "career_main" => 6,
                "training_selection" => 7,
                "race_day" => 8,
                "race_list" => 9,
                "race_details" => 10,
                "race_attributes" => 11,
                "race_playback_settings" => 12,
                "race_playback" => 13,
                _ => 20,
            })
            .ToArray();

        // Observe a small stable sample once, then score all screen templates
        // against that same frame. Calling WaitForMatchAsync once per screen
        // would recapture and wait serially for every candidate, making a
        // 38-screen profile needlessly slow and less deterministic.
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

        UraObservation? best = null;
        foreach (var frame in frames)
        {
            foreach (var screen in candidates)
            {
                foreach (var template in screen.Templates)
                {
                    var path = ResolveCapture(pack, template);
                    var grayTemplate = await _visualRuntime.LoadTemplateAsync(
                            path,
                            string.Empty,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (grayTemplate is null)
                        continue;

                    var match = TemplateMatcher.Find(
                        frame,
                        grayTemplate,
                        roi: null,
                        threshold: RecognitionThreshold,
                        pack.ScreenProfile.ReferenceWidth,
                        pack.ScreenProfile.ReferenceHeight);
                    if (match.Found
                        && (best is null || match.Score > best.Score))
                    {
                        best = new UraObservation(screen.ScreenId, match.Score);
                    }
                }
            }
        }

        return best;
    }

    private async Task<UraTrainingResult?> RunScreenActionAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string screenId,
        string actionId,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken,
        HachimiPipelineRunOptions? options = null)
    {
        var screen = pack.ScreenProfile.Find(screenId);
        if (screen is null)
        {
            return Failure(
                $"Screen '{screenId}' is missing from screen_profile.json.",
                screenId);
        }

        var action = screen.FindAction(actionId);
        if (action is null || string.IsNullOrWhiteSpace(action.Task))
        {
            return Failure(
                $"Screen action '{screenId}.{actionId}' is missing from screen_profile.json.",
                screenId);
        }

        var result = await _jsonRunner.RunAsync(
                connection,
                pack.ExecutionDefinition,
                action.Task,
                options: options,
                logSink: logSink,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Failure(
                $"Could not execute JSON task '{action.Task}' for '{screenId}.{actionId}': {result.Message}",
                screenId);
        }

        return null;
    }

    private static string ResolveCapture(UraScenarioPack pack, string relativePath) =>
        UraScenarioResourceResolver.Resolve(pack, relativePath);

    private void ValidateSupportCards(IReadOnlyList<int> supportCardIds)
    {
        foreach (var id in supportCardIds)
        {
            if (!_umaDatabase.TryGetSupportCard(id, out var card) || card is null || !card.Available)
            {
                throw new InvalidOperationException(
                    $"Configured support card ID {id.ToString(CultureInfo.InvariantCulture)} "
                    + "was not found or is unavailable.");
            }
        }
    }

    private static UraTrainingResult Failure(
        string message,
        string lastScreenId,
        int actionsCompleted = 0) =>
        new(false, message, actionsCompleted, lastScreenId);

    private sealed record UraObservation(string ScreenId, double Score);
}
