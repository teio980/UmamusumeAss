using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public sealed class AdbCareerTrainingPipeline : ICareerTrainingPipeline
{
    private const double EarlyRecognitionThreshold = 0.985;
    private const string CareerFinalConfirmationScreenId = "career_final_confirmation";
    private const string SupportStartTransitionScreenId = "support_start_transition";
    private const string CareerStartTransitionScreenId = "career_start_transition";

    // The ranked picker is a five-column grid. These are search regions only:
    // every selection still comes from a JSON template match inside the region.
    private static readonly int[][] RankedSupportCardSlotRois =
    [
        [35, 130, 165, 220],
        [202, 130, 165, 220],
        [369, 130, 165, 220],
        [536, 130, 165, 220],
        [703, 130, 165, 220],
    ];

    private static readonly HashSet<string> CareerEntryScreenIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "home",
            "career_continue",
            "scenario_select",
            "legacy_select",
            "trainee_select",
            "support_select",
            "support_autofill_confirmation",
            "support_ready",
            "career_races_ready",
            CareerFinalConfirmationScreenId,
            "career_intro_event",
            "career_main",
        };

    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly IUmaDatabaseService _umaDatabase;
    private readonly UraTraineeSelector _traineeSelector;
    private readonly UraLegacySelector _legacySelector;
    private readonly CareerEntryNavigator _entryNavigator;
    private readonly UraRaceResultRecognizer _raceResultRecognizer;
    private readonly HachimiJsonPipelineRunner _jsonRunner;
    private readonly ConcurrentDictionary<string, Lazy<Task<GrayImage?>>> _templateCache = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly object _runLock = new();
    private CancellationTokenSource? _runCancellation;

    public AdbCareerTrainingPipeline(
        IVisualPipelineRuntime visualRuntime,
        IUmaDatabaseService umaDatabase,
        UraTraineeSelector traineeSelector,
        UraLegacySelector legacySelector,
        CareerEntryNavigator entryNavigator,
        HachimiJsonPipelineRunner jsonRunner)
    {
        ArgumentNullException.ThrowIfNull(visualRuntime);
        ArgumentNullException.ThrowIfNull(umaDatabase);
        ArgumentNullException.ThrowIfNull(traineeSelector);
        ArgumentNullException.ThrowIfNull(legacySelector);
        ArgumentNullException.ThrowIfNull(entryNavigator);
        ArgumentNullException.ThrowIfNull(jsonRunner);
        _visualRuntime = visualRuntime;
        _umaDatabase = umaDatabase;
        _traineeSelector = traineeSelector;
        _legacySelector = legacySelector;
        _entryNavigator = entryNavigator;
        _raceResultRecognizer = new UraRaceResultRecognizer(visualRuntime);
        _jsonRunner = jsonRunner;
    }

    public async Task<CareerTrainingResult> RunAsync(
        LastVerifiedConnection connection,
        CareerTrainingSettings settings,
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
                return Failure("A Career training run is already in progress.", "busy");
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
            logSink?.Add("Career Training", "Career training was stopped.", LogEntryKind.Failure);
            return Failure("Career training was stopped.", "canceled");
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

    public Task<CareerTrainingResult> StopAsync(
        LastVerifiedConnection connection,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_runLock)
        {
            _runCancellation?.Cancel();
        }

        logSink?.Add("Career Training", "Stop requested.");
        return Task.FromResult(new CareerTrainingResult(true, "Stop requested.", 0, "stop"));
    }

    private async Task<CareerTrainingResult> RunCoreAsync(
        LastVerifiedConnection connection,
        CareerTrainingSettings settings,
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

        if (settings.SupportDeckMode.Equals("selected", StringComparison.OrdinalIgnoreCase))
        {
            ValidateSupportCards(GetSelectedSupportCardIds(settings), settings.SupportDeckPreset);
            ValidateFriendSupportCard(settings, allowCustomPreset: true);
        }
        else if (settings.SupportDeckMode.Equals("highest-star", StringComparison.OrdinalIgnoreCase)
            && GetRequiredSupportTypes(settings.SupportDeckPreset) is null)
        {
            throw new InvalidOperationException(
                "Highest-star support selection requires a support deck preset.");
        }
        else if (settings.SupportDeckMode.Equals("highest-star", StringComparison.OrdinalIgnoreCase))
        {
            ValidateFriendSupportCard(settings);
        }
        var pack = await UraScenarioPackLoader.LoadAsync(settings.ManifestPath, cancellationToken)
            .ConfigureAwait(false);
        logSink?.Add(
            "Career Training",
            $"Loaded {pack.Manifest.DisplayName} for {trainee.NameEn} ({trainee.TraineeId}).");

        var scenario = new UraScenarioModule(pack);
        var strategy = UraStrategyRegistry.Create(settings.StrategyId);
        var checkpointStore = new UraCheckpointStore(settings.TraineeId);
        var checkpoint = await checkpointStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        UraCareerSessionState state;
        if (!settings.ContinueExistingCareer)
        {
            await checkpointStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            state = scenario.CreateInitialState();
        }
        else
        {
            state = checkpoint ?? scenario.CreateInitialState();
            // The game is the source of truth for the Resume entry. Preserve
            // the checkpoint's turn/objective data, but always reopen Career
            // so the JSON Resume action is used instead of skipping Home.
            state.CareerEntryOpened = false;
            state.CareerStarted = false;
        }
        if (!string.Equals(state.ScenarioId, pack.Manifest.ScenarioId, StringComparison.OrdinalIgnoreCase))
            state = scenario.CreateInitialState();
        if (state.TurnIndex == 0 && !state.CareerStarted)
        {
            // A failed setup attempt can leave only the entry flags in the
            // checkpoint. A new zero-turn career must always restart at the
            // real game Home screen instead of skipping into URA recognition.
            state.CareerEntryOpened = false;
            state.TraineeSelected = false;
            state.SupportCardsSelected = false;
            state.ScenarioSelected = false;
            state.LegacySelected = false;
            state.ScenarioSelectionAdvanceAttempts = 0;
        }
        logSink?.Add(
            "Career Training",
            "Normal Career mode selected; Final Confirmation will use Normal Career start.");
        logSink?.Add(
            "Career Training",
            state.TurnIndex > 0
                ? $"Resuming checkpoint at turn {state.TurnIndex}, objective {state.CurrentObjectiveId}."
                : "Starting a new URA career session.");
        logSink?.Add(
            "Career Training",
            settings.ContinueExistingCareer
                ? "Existing Career handling selected: Resume."
                : "Existing Career handling selected: Delete Data.");

        var actionCount = 0;
        var setupObservationRetryCount = 0;
        // After Start Career is clicked on support_ready, the formation page
        // can remain visible for a few frames. Keep that transition local to
        // this run so a stale support_select template cannot restart support
        // selection while the game is opening Final Confirmation.
        var supportStartTransitionExpected = false;
        var careerEntryFlowStarted = state.CareerEntryOpened;
        if (!state.CareerStarted && !state.CareerEntryOpened)
        {
            var entryState = new CareerEntryNavigationState
            {
                LastScreenId = state.LastScreenId,
                ActionsCompleted = actionCount,
            };
            var entry = await _entryNavigator.NavigateAsync(
                    connection,
                    pack,
                    settings,
                    entryState,
                    logSink,
                    progressCallback: null,
                    cancellationToken)
                .ConfigureAwait(false);
            actionCount = entry.ActionsCompleted;
            state.CareerEntryOpened = entry.Succeeded;
            state.LastScreenId = entry.LastScreenId;
            if (!entry.Succeeded)
            {
                return Failure(
                    entry.Message,
                    entry.LastScreenId,
                    actionCount);
            }

            careerEntryFlowStarted = true;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            actionCount++;
        }

        while (actionCount < 300)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var postSupportStartExpected = supportStartTransitionExpected
                || state.LastScreenId.Equals(
                    "support_ready",
                    StringComparison.OrdinalIgnoreCase)
                || state.LastScreenId.Equals(
                    SupportStartTransitionScreenId,
                    StringComparison.OrdinalIgnoreCase);
            var careerStartTransitionExpected = state.LastScreenId.Equals(
                CareerStartTransitionScreenId,
                StringComparison.OrdinalIgnoreCase);
            var observation = await ObserveAsync(
                    connection,
                    pack,
                    state,
                    postSupportStartExpected,
                    careerStartTransitionExpected,
                    cancellationToken)
                .ConfigureAwait(false);
            if (observation is null)
            {
                var legacyToSupportTransition = state.LegacySelected
                    && state.LastScreenId.Equals(
                        "legacy_select",
                        StringComparison.OrdinalIgnoreCase)
                    && !state.CareerStarted;
                var setupRetryLimit = legacyToSupportTransition
                    ? 80
                    : postSupportStartExpected || careerStartTransitionExpected ? 40 : 12;
                if (state.CareerEntryOpened
                    && !state.CareerStarted
                    && setupObservationRetryCount < setupRetryLimit)
                {
                    setupObservationRetryCount++;
                    await _visualRuntime.DelayAsync(250, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                return Failure(
                    "Could not recognize a stable Career screen; automation paused safely.",
                    state.LastScreenId,
                    actionCount);
            }

            setupObservationRetryCount = 0;
            if (observation.ScreenId.Equals("support_ready", StringComparison.OrdinalIgnoreCase))
            {
                if (!supportStartTransitionExpected)
                {
                    logSink?.Add(
                        "Career Training",
                        "Support setup complete; waiting for Final Confirmation.");
                }

                supportStartTransitionExpected = true;
            }
            else if (postSupportStartExpected)
            {
                supportStartTransitionExpected = false;
            }
            state.LastScreenId = observation.ScreenId;
            scenario.ObserveScreen(state, observation.ScreenId, observation.Score);
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            logSink?.Add(
                "Career Training",
                $"Recognized {observation.ScreenId} with score {observation.Score:0.000}.");

            if (observation.ScreenId == "home")
            {
                if (!state.CareerStarted)
                {
                    // The Home entry graph may have completed its last tap
                    // while the UI is still rendering Home. Do not replay the
                    // Home/Career taps during that transition.
                    if (careerEntryFlowStarted)
                    {
                        await _visualRuntime.DelayAsync(250, cancellationToken)
                            .ConfigureAwait(false);
                        actionCount++;
                        continue;
                    }

                    return Failure(
                        "Career entry state was lost after the shared navigation flow.",
                        "home",
                        actionCount);
                }

                await checkpointStore.ClearAsync(cancellationToken).ConfigureAwait(false);
                return new CareerTrainingResult(
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

            // Persist setup transitions as well as observations. In
            // particular, ScenarioSelected must survive a restart after the
            // first Next click so we do not re-enter the scenario carousel.
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            actionCount++;
        }

        await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return Failure(
            "Career training exceeded the safety action limit and was paused.",
            state.LastScreenId,
            actionCount);
    }

    private async Task<CareerTrainingResult?> HandleLegacySelectionAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        CareerTrainingSettings settings,
        UraCareerSessionState state,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        if (state.LegacySelected)
        {
            return await RunScreenActionAsync(
                    connection,
                    pack,
                    "legacy_select",
                    "next",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var legacyPickResult = await RunScreenActionAsync(
                connection,
                pack,
                "legacy_select",
                "choose",
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
                        var selection = await _legacySelector.SelectAsync(
                                actionConnection,
                                definition,
                                settings,
                                actionLogSink,
                                actionCancellationToken)
                            .ConfigureAwait(false);
                        return selection.Succeeded
                            ? HachimiCustomActionResult.Success(selection.Message)
                            : HachimiCustomActionResult.Failure(selection.Message);
                    }
                })
            .ConfigureAwait(false);
        if (legacyPickResult is null)
            state.LegacySelected = true;
        return legacyPickResult;
    }

    private async Task<CareerTrainingResult?> HandleScreenAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        CareerTrainingSettings settings,
        UraScenarioModule scenario,
        UraDefaultStrategy strategy,
        UraCareerSessionState state,
        UraObservation observation,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        switch (observation.ScreenId)
        {
            case "career_continue":
                var careerContinueAction = settings.ContinueExistingCareer ? "resume" : "delete";
                logSink?.Add(
                    "Career Training",
                    $"Continue Career dialog detected; selecting '{careerContinueAction}'.");
                return await RunScreenActionAsync(
                        connection,
                        pack,
                        "career_continue",
                        careerContinueAction,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
            case "scenario_select":
                return await HandleScenarioSelectionAsync(
                        connection,
                        pack,
                        state,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
            case "trainee_select":
                if (state.TraineeSelected)
                {
                    var traineeNextResult = await RunScreenActionAsync(
                            connection,
                            pack,
                            "trainee_select",
                            "next",
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (traineeNextResult is not null)
                        return traineeNextResult;

                    logSink?.Add(
                        "Career Training",
                        "Trainee Next succeeded; continuing directly into Legacy Select.");

                    // The live URA flow goes directly from Trainee Select to
                    // Legacy Select. Continue that transition explicitly
                    // instead of waiting for the generic screen observer to
                    // rediscover the next page.
                    return await HandleLegacySelectionAsync(
                            connection,
                            pack,
                            settings,
                            state,
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                var traineePickResult = await RunScreenActionAsync(
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
                                        definition,
                                        taskName,
                                        task,
                                        settings.TraineeId,
                                        actionLogSink,
                                        actionCancellationToken)
                                    .ConfigureAwait(false);
                                return selection.Succeeded
                                    ? HachimiCustomActionResult.Success(selection.Message)
                                    : HachimiCustomActionResult.Failure(selection.Message);
                            }
                        })
                    .ConfigureAwait(false);
                if (traineePickResult is not null)
                    return traineePickResult;

                state.TraineeSelected = true;
                logSink?.Add(
                    "Career Training",
                    "Trainee selection and Next succeeded; continuing directly into Legacy Select.");

                // trainee_select_pick is a chained JSON task: after the
                // custom picker succeeds it automatically runs
                // trainee_select_trainee_next. The screen is therefore
                // already transitioning to Legacy Select here. Do not return
                // to the generic observer, which can miss that short-lived
                // transition and pause before the legacy selector runs.
                return await HandleLegacySelectionAsync(
                        connection,
                        pack,
                        settings,
                        state,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
            case "support_select":
                if (!state.SupportCardsSelected)
                {
                    var resetFormationResult = await RunScreenActionAsync(
                            connection,
                            pack,
                            "support_select",
                            "reset_if_needed",
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (resetFormationResult is not null)
                        return resetFormationResult;
                }

                var supportDeckMode = settings.SupportDeckMode.Trim().ToLowerInvariant();
                if (supportDeckMode == "highest-star")
                {
                    if (state.SupportCardsSelected)
                    {
                        state.LastScreenId = SupportStartTransitionScreenId;
                        return await RunScreenActionAsync(
                                connection,
                                pack,
                                "support_select",
                                "start",
                                logSink,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    var rankedSelectionResult = await SelectHighestStarSupportCardsAsync(
                            connection,
                            pack,
                            settings.SupportDeckPreset,
                            settings.FriendSupportCardId,
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (rankedSelectionResult is not null)
                        return rankedSelectionResult;

                    state.SupportCardsSelected = true;
                    state.LastScreenId = SupportStartTransitionScreenId;
                    return await RunScreenActionAsync(
                            connection,
                            pack,
                            "support_select",
                            "start",
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (supportDeckMode == "selected")
                {
                    if (settings.SupportCardIds.Count is not (5 or 6))
                    {
                        return Failure(
                            "Selected support deck mode requires exactly 5 or 6 cards.",
                            "support_select");
                    }

                    if (state.SupportCardsSelected)
                    {
                        state.LastScreenId = SupportStartTransitionScreenId;
                        return await RunScreenActionAsync(
                                connection,
                                pack,
                                "support_select",
                                "start",
                                logSink,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    var supportSelectionResult = await SelectConfiguredSupportCardsAsync(
                            connection,
                            pack,
                            settings.SupportCardIds,
                            settings.FriendSupportCardId,
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (supportSelectionResult is not null)
                        return supportSelectionResult;

                    state.SupportCardsSelected = true;
                    state.LastScreenId = SupportStartTransitionScreenId;
                    return await RunScreenActionAsync(
                            connection,
                            pack,
                            "support_select",
                            "start",
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
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
                state.LastScreenId = SupportStartTransitionScreenId;
                return await RunScreenActionAsync(
                        connection, pack, "support_ready", "start", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "legacy_select":
                return await HandleLegacySelectionAsync(
                        connection,
                        pack,
                        settings,
                        state,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
            case "career_intro_event":
                // Keep the post-start filter active until the first Career
                // screen is reached; the intro event itself may take more
                // than one frame to advance.
                state.LastScreenId = CareerStartTransitionScreenId;
                return await RunScreenActionAsync(
                        connection, pack, "career_intro_event", "advance", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "career_main":
                return await HandleCareerMainAsync(
                        connection, pack, scenario, strategy, state, logSink, cancellationToken)
                    .ConfigureAwait(false);
            case "career_races_ready":
                state.LastScreenId = CareerStartTransitionScreenId;
                return await RunScreenActionAsync(
                        connection, pack, "career_races_ready", "races", logSink, cancellationToken)
                    .ConfigureAwait(false);
            case CareerFinalConfirmationScreenId:
                // The final Start click leaves the old formation page visible
                // for a short transition. Mark that transition before the
                // JSON click so the next observation cannot restart support
                // selection from a stale template.
                state.LastScreenId = CareerStartTransitionScreenId;
                return await RunScreenActionAsync(
                        connection, pack, CareerFinalConfirmationScreenId, "start", logSink, cancellationToken)
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
                        "Career Training",
                        $"Unknown or unsupported stable screen '{observation.ScreenId}'; paused.",
                        LogEntryKind.Failure);
                    return Failure(
                        $"Unsupported stable screen '{observation.ScreenId}'.",
                        observation.ScreenId);
                }

                return null;
        }
    }

    private async Task<CareerTrainingResult?> HandleScenarioSelectionAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        UraCareerSessionState state,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var selection = pack.ScreenProfile.ScenarioSelection;
        if (selection is null)
        {
            return await RunScreenActionAsync(
                    connection, pack, "scenario_select", "next", logSink, cancellationToken)
                .ConfigureAwait(false);
        }

        var observed = await FindScenarioSelectionAsync(
                connection,
                pack,
                selection,
                cancellationToken)
            .ConfigureAwait(false);
        if (!observed.Captured)
        {
            return Failure(
                "Could not capture the scenario selection screen.",
                "scenario_select");
        }

        if (observed.Match is { Found: true } match)
        {
            state.ScenarioSelectionAdvanceAttempts = 0;
            logSink?.Add(
                "Career Training",
                $"Detected target scenario '{selection.ScenarioId}' "
                + $"with score {match.Score:0.000}; confirming selection.");
            var result = await RunScreenActionAsync(
                    connection, pack, "scenario_select", "next", logSink, cancellationToken)
                .ConfigureAwait(false);
            if (result is null)
                state.ScenarioSelected = true;
            return result;
        }

        if (state.ScenarioSelectionAdvanceAttempts >= selection.MaxAdvanceAttempts)
        {
            return Failure(
                $"Target scenario '{selection.ScenarioId}' was not found after "
                + $"{selection.MaxAdvanceAttempts} carousel advances.",
                "scenario_select");
        }

        state.ScenarioSelectionAdvanceAttempts++;
        logSink?.Add(
            "Career Training",
            $"Target scenario '{selection.ScenarioId}' is not visible; advancing "
            + $"the scenario carousel ({state.ScenarioSelectionAdvanceAttempts}/"
            + $"{selection.MaxAdvanceAttempts}).");
        return await RunScreenActionAsync(
                connection, pack, "scenario_select", "next_card", logSink, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(bool Captured, TemplateMatchResult? Match)> FindScenarioSelectionAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        CareerScenarioSelectionDefinition selection,
        CancellationToken cancellationToken)
    {
        var frame = await _visualRuntime.CaptureGrayAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (frame is null)
            return (false, null);

        TemplateMatchResult? best = null;
        foreach (var templatePath in selection.Recognition.GetTemplates())
        {
            var template = await LoadTemplateCachedAsync(
                    ResolveCapture(pack, templatePath),
                    cancellationToken)
                .ConfigureAwait(false);
            if (template is null)
                continue;

            var match = TemplateMatcher.Find(
                frame,
                template,
                selection.Recognition.Roi,
                selection.Recognition.TemplateThreshold,
                pack.ScreenProfile.ReferenceWidth,
                pack.ScreenProfile.ReferenceHeight);
            if (match.Found && (best is null || match.Score > best.Score))
                best = match;
        }

        return (true, best);
    }

    private async Task<CareerTrainingResult?> HandleCareerMainAsync(
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

    private async Task<UraObservation?> ObserveAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        UraCareerSessionState state,
        bool postSupportStartExpected,
        bool careerStartTransitionExpected,
        CancellationToken cancellationToken)
    {
        var careerEntryFlowActive = state.CareerEntryOpened && !state.CareerStarted;
        var traineeSelectionExpected = state.ScenarioSelected
            && !state.TraineeSelected
            && !state.CareerStarted;
        // The game keeps the Auto-Fill button on the formation page after an
        // auto-fill confirmation. Without this context, that page can be
        // recognized as support_select again before support_ready gets a
        // chance to handle Start Career.
        var supportReadyExpected = state.LastScreenId.Equals(
            "support_autofill_confirmation",
            StringComparison.OrdinalIgnoreCase);
        var legacyToSupportTransition = state.LegacySelected
            && state.LastScreenId.Equals(
                "legacy_select",
                StringComparison.OrdinalIgnoreCase)
            && !state.CareerStarted;
        var candidates = pack.ScreenProfile.Screens
            .Where(screen => !string.Equals(screen.ScreenId, "race_live", StringComparison.OrdinalIgnoreCase))
            .Where(screen => !careerEntryFlowActive
                || !string.Equals(screen.ScreenId, "home", StringComparison.OrdinalIgnoreCase))
            .Where(screen => state.CareerStarted
                || state.TurnIndex > 0
                || CareerEntryScreenIds.Contains(screen.ScreenId))
            .Where(screen => !traineeSelectionExpected
                || string.Equals(screen.ScreenId, "trainee_select", StringComparison.OrdinalIgnoreCase))
            .Where(screen => !supportReadyExpected
                || string.Equals(screen.ScreenId, "support_ready", StringComparison.OrdinalIgnoreCase))
            // Start Career has already been tapped. Do not let a stale
            // formation template win while the game is transitioning to
            // Final Confirmation, where the normal Start action continues.
            .Where(screen => !postSupportStartExpected
                || (screen.ScreenId is not "support_select"
                    and not "support_ready"
                    and not "support_autofill_confirmation"))
            // Normal Career has already received its final Start click. Only
            // accept the first screens that can legitimately follow it; the
            // formation templates from the previous page must not win here.
            .Where(screen => !careerStartTransitionExpected
                || screen.ScreenId is "career_intro_event"
                    or "career_main"
                    or "career_races_ready")
            .Where(screen => !legacyToSupportTransition
                || string.Equals(screen.ScreenId, "support_select", StringComparison.OrdinalIgnoreCase))
            .OrderBy(screen => GetScreenRecognitionPriority(
                screen.ScreenId,
                supportReadyExpected,
                postSupportStartExpected,
                careerStartTransitionExpected))
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
            UraObservation? frameBest = null;
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
                        frameBest = new UraObservation(screen.ScreenId, match.Score);
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
        bool supportReadyExpected,
        bool postSupportStartExpected,
        bool careerStartTransitionExpected) =>
        screenId switch
        {
            "career_continue" => 0,
            "home" => 0,
            "scenario_select" => 1,
            "trainee_select" => 2,
            CareerFinalConfirmationScreenId when postSupportStartExpected => 0,
            CareerFinalConfirmationScreenId => 3,
            "career_intro_event" when careerStartTransitionExpected => 0,
            "career_main" when careerStartTransitionExpected => 1,
            "career_races_ready" when careerStartTransitionExpected => 2,
            "support_ready" when supportReadyExpected => 4,
            "support_select" => 5,
            "support_ready" => 6,
            "career_races_ready" => 7,
            "career_main" => 8,
            "training_selection" => 9,
            "race_day" => 10,
            "race_list" => 11,
            "race_details" => 12,
            "race_attributes" => 13,
            "race_playback_settings" => 14,
            "race_playback" => 15,
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

    private async Task<CareerTrainingResult?> RunScreenActionAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string screenId,
        string actionId,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken,
        HachimiPipelineRunOptions? options = null,
        bool allowVisualMiss = false)
    {
        // Keep checkpoints written by versions that called the old semantic
        // screen id usable after the final-confirmation screen was split out.
        // The actual action and all interaction details still come from the
        // JSON screen profile; this is only an id migration.
        if (screenId.Equals("career_entry", StringComparison.OrdinalIgnoreCase)
            && pack.ScreenProfile.Find("career_entry") is null)
        {
            screenId = CareerFinalConfirmationScreenId;
        }

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
            if (allowVisualMiss && IsVisualTaskTimeout(result.Message))
            {
                logSink?.Add(
                    "Career Training",
                    $"Optional post-start action '{screenId}.{actionId}' was not visible; "
                        + "continuing with the next fallback.",
                    LogEntryKind.Info);
                return null;
            }

            return Failure(
                $"Could not execute JSON task '{action.Task}' for '{screenId}.{actionId}': {result.Message}",
                screenId);
        }

        return null;
    }

    private static bool IsVisualTaskTimeout(string message) =>
        message.StartsWith(
            "Timed out waiting for JSON task '",
            StringComparison.Ordinal);

    private static string ResolveCapture(UraScenarioPack pack, string relativePath) =>
        UraScenarioResourceResolver.Resolve(pack, relativePath);

    private static HachimiPipelineRunOptions SupportPickerOpenOptions(
        UraScenarioPack pack,
        bool friendSlotOnly)
    {
        var task = pack.ExecutionDefinition.GetTask("support_select_support_open");
        var configuredRois = task.SearchRois;
        IReadOnlyList<int[]> searchRois;

        if (configuredRois.Count <= 1)
        {
            searchRois = configuredRois;
        }
        else if (friendSlotOnly)
        {
            // The final JSON search ROI is the Friends slot. It has a
            // different layout from the five owned-card slots.
            searchRois = [configuredRois[^1]];
        }
        else
        {
            // Never allow an owned-card open to compete with the Friends
            // slot. The template matcher still chooses the actual match
            // center from these JSON-declared regions.
            searchRois = configuredRois.Take(configuredRois.Count - 1).ToArray();
        }

        return new HachimiPipelineRunOptions
        {
            SearchRoiOverrides = new Dictionary<string, IReadOnlyList<int[]>>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["support_select_support_open"] = searchRois,
            },
        };
    }

    private async Task<CareerTrainingResult?> SelectConfiguredSupportCardsAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        IReadOnlyList<int> supportCardIds,
        int? friendSupportCardId,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        foreach (var supportCardId in supportCardIds)
        {
            if (!_umaDatabase.TryGetSupportCard(supportCardId, out var supportCard)
                || supportCard is null
                || !supportCard.Available)
            {
                return Failure(
                    $"Configured support card {supportCardId.ToString(CultureInfo.InvariantCulture)} "
                    + "was not found or is unavailable.",
                    "support_select");
            }

            var templatePath = ResolveSupportCardTemplate(pack, supportCardId);
            if (templatePath is null)
            {
                return Failure(
                    $"Support card {supportCardId.ToString(CultureInfo.InvariantCulture)} "
                    + "has no local selection template.",
                    "support_select");
            }

            var openResult = await RunScreenActionAsync(
                    connection,
                    pack,
                    "support_select",
                    "open",
                    logSink,
                    cancellationToken,
                    SupportPickerOpenOptions(pack, friendSlotOnly: false))
                .ConfigureAwait(false);
            if (openResult is not null)
                return openResult;

            // An exact card can appear many pages into the unfiltered list.
            // Use the same metadata-backed filter and Level-desc sort as the
            // highest-star path before matching the card image. The picker
            // closes after each selection, so configure it again when the
            // next card is opened (this also handles cards from different
            // type/rarity groups deterministically).
            var filterResult = await ConfigureHighestStarFilterAsync(
                    connection,
                    pack,
                    supportCard.Type,
                    supportCard.Rarity,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (filterResult is not null)
                return filterResult;

            var result = await RunScreenActionAsync(
                    connection,
                    pack,
                    "support_select",
                    "ranked.select_exact_card",
                    logSink,
                    cancellationToken,
                    new HachimiPipelineRunOptions
                    {
                        TemplateOverrides = new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["support_select_support_card_exact"] = templatePath,
                        },
                    })
                .ConfigureAwait(false);
            if (result is not null)
                return result;
        }

        if (friendSupportCardId is not > 0)
        {
            // Selecting a support card closes the picker automatically and
            // returns to the formation screen. Do not click the picker Close
            // button here: after the last selection it no longer exists and
            // its old coordinate overlaps the game's Home tab.
            return null;
        }

        if (!_umaDatabase.TryGetSupportCard(friendSupportCardId.Value, out var friendCard)
            || friendCard is null
            || !friendCard.Available)
        {
            return Failure(
                $"Configured guest support card {friendSupportCardId.Value.ToString(CultureInfo.InvariantCulture)} "
                + "was not found or is unavailable.",
                "support_select");
        }

        var openFriendResult = await RunScreenActionAsync(
                connection,
                pack,
                "support_select",
                "open",
                logSink,
                cancellationToken,
                SupportPickerOpenOptions(pack, friendSlotOnly: true))
            .ConfigureAwait(false);
        if (openFriendResult is not null)
            return openFriendResult;

        var friendFilterResult = await ConfigureHighestStarFilterAsync(
                connection,
                pack,
                friendCard.Type,
                rarity: null,
                logSink,
                cancellationToken,
                friendPage: true)
            .ConfigureAwait(false);
        if (friendFilterResult is not null)
            return friendFilterResult;

        var friendTemplatePath = ResolveSupportCardTemplate(
            pack,
            friendCard.SupportCardId);
        if (friendTemplatePath is null)
        {
            return Failure(
                $"Guest support card {friendCard.SupportCardId.ToString(CultureInfo.InvariantCulture)} "
                + "has no local selection template.",
                "support_select");
        }

        // The filtered guest list is sorted by Level descending. The
        // template matcher therefore picks the highest-level copy.
        return await RunScreenActionAsync(
                connection,
                pack,
                "support_select",
                "ranked.select_exact_card",
                logSink,
                cancellationToken,
                new HachimiPipelineRunOptions
                {
                    TemplateOverrides = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["support_select_support_card_exact"] = friendTemplatePath,
                    },
                })
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<int> GetSelectedSupportCardIds(
        CareerTrainingSettings settings)
    {
        if (settings.FriendSupportCardId is not > 0)
            return settings.SupportCardIds;

        if (settings.SupportCardIds.Count != 5)
        {
            throw new InvalidOperationException(
                "Selected support deck mode requires 5 own cards when a friend card is configured.");
        }

        return settings.SupportCardIds
            .Append(settings.FriendSupportCardId.Value)
            .ToArray();
    }

    private async Task<CareerTrainingResult?> SelectHighestStarSupportCardsAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string supportDeckPreset,
        int? friendSupportCardId,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var requiredTypes = GetRequiredSupportTypes(supportDeckPreset);
        if (requiredTypes is null)
        {
            return Failure(
                "Highest-star support selection requires a support deck preset.",
                "support_select");
        }

        UmaSupportCardRecord? guestCard = null;
        if (friendSupportCardId is > 0)
        {
            if (!_umaDatabase.TryGetSupportCard(
                    friendSupportCardId.Value,
                    out guestCard)
                || guestCard is null
                || !guestCard.Available)
            {
                return Failure(
                    $"Configured guest support card {friendSupportCardId.Value.ToString(CultureInfo.InvariantCulture)} "
                    + "was not found or is unavailable.",
                    "support_select");
            }
        }
        else
        {
            var automaticGuestType = GetAutomaticGuestSupportType(requiredTypes);
            if (automaticGuestType is null)
            {
                return Failure(
                    "The selected support preset has no usable type for the automatic guest card.",
                    "support_select");
            }

            // Do not click an own-card grid slot to reserve the guest type.
            // The guest slot opens Borrow Card later and uses its own list
            // layout. The type is only needed now to leave the correct number
            // of own cards for the preset.
            guestCard = new UmaSupportCardRecord
            {
                Type = automaticGuestType,
            };
        }

        if (guestCard is null)
        {
            return Failure(
                "Could not identify a highest-level guest support card from the selected preset types.",
                "support_select");
        }

        var ownRequiredTypes = GetOwnRequiredSupportTypes(
            requiredTypes,
            guestCard);
        if (ownRequiredTypes is null)
        {
            return Failure(
                $"Guest support card {friendSupportCardId.GetValueOrDefault().ToString(CultureInfo.InvariantCulture)} "
                + "does not fit the selected support deck preset.",
                "support_select");
        }

        var pickerOpen = false;
        foreach (var required in ownRequiredTypes)
        {
            var remaining = required.Value;
            if (remaining <= 0)
                continue;

            if (!pickerOpen)
            {
                var openResult = await RunScreenActionAsync(
                        connection,
                        pack,
                        "support_select",
                        "open",
                        logSink,
                        cancellationToken,
                        SupportPickerOpenOptions(pack, friendSlotOnly: false))
                    .ConfigureAwait(false);
                if (openResult is not null)
                    return openResult;

                pickerOpen = true;
            }

            // Apply the filter once for the whole type group. Selecting a
            // card closes the picker, but the game's filter/sort state is
            // retained when the next slot is opened.
            logSink?.Add(
                "Career Training",
                $"Filtering {remaining} highest-level {required.Key} support card(s) once.");
            var filterResult = await ConfigureHighestStarFilterAsync(
                    connection,
                    pack,
                    required.Key,
                    rarity: null,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (filterResult is not null)
                return filterResult;

            while (remaining > 0)
            {
                if (!pickerOpen)
                {
                    var openResult = await RunScreenActionAsync(
                            connection,
                            pack,
                            "support_select",
                            "open",
                            logSink,
                            cancellationToken,
                            SupportPickerOpenOptions(pack, friendSlotOnly: false))
                        .ConfigureAwait(false);
                    if (openResult is not null)
                        return openResult;

                    pickerOpen = true;
                }

                var selected = await SelectHighestSupportCardAsync(
                        connection,
                        pack,
                        required.Key,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (selected is not null)
                    return selected;

                remaining--;
                // A successful card tap closes the picker automatically.
                pickerOpen = false;
            }

            if (remaining > 0)
            {
                if (pickerOpen)
                {
                    var closeResult = await RunScreenActionAsync(
                            connection,
                            pack,
                            "support_select",
                            "close",
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (closeResult is not null)
                        return closeResult;
                }

                return Failure(
                    $"Could not find {remaining} more {required.Key} support card(s) after filtering and sorting by Level.",
                    "support_select");
            }
        }

        // The own cards are now filled. The next open targets the Friends slot;
        // filter it by the configured guest card metadata and select the
        // highest-level copy of that exact card.
        if (!pickerOpen)
        {
            var openGuestResult = await RunScreenActionAsync(
                    connection,
                    pack,
                    "support_select",
                    "open",
                    logSink,
                    cancellationToken,
                    SupportPickerOpenOptions(pack, friendSlotOnly: true))
                .ConfigureAwait(false);
            if (openGuestResult is not null)
                return openGuestResult;

            pickerOpen = true;
        }

        var guestFilterResult = await ConfigureHighestStarFilterAsync(
                connection,
                pack,
                guestCard.Type,
                rarity: null,
                logSink,
                cancellationToken,
                friendPage: true)
            .ConfigureAwait(false);
        if (guestFilterResult is not null)
            return guestFilterResult;

        if (friendSupportCardId is not > 0)
        {
            // With no configured friend card, the filtered/sorted first card
            // is the highest-level guest. Use the JSON type-badge recognition
            // click path to select that first unselected card; no card-database
            // identity scan is needed here.
            var automaticGuestSelection = await SelectHighestGuestSupportCardAsync(
                    connection,
                    pack,
                    guestCard.Type,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            return automaticGuestSelection;
        }

        var guestTemplatePath = ResolveSupportCardTemplate(
                pack,
            guestCard.SupportCardId);
        if (guestTemplatePath is null)
        {
            return Failure(
                $"Guest support card {guestCard.SupportCardId.ToString(CultureInfo.InvariantCulture)} "
                + "has no local selection template.",
                "support_select");
        }

        // The guest list is sorted by Level descending. The template
        // matcher scans top-to-bottom, so identical copies resolve to the
        // highest-level guest card.
        var guestSelectionResult = await RunScreenActionAsync(
                connection,
                pack,
                "support_select",
                "ranked.select_exact_card",
                logSink,
                cancellationToken,
                new HachimiPipelineRunOptions
                {
                    TemplateOverrides = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["support_select_support_card_exact"] = guestTemplatePath,
                    },
                })
            .ConfigureAwait(false);
        if (guestSelectionResult is not null)
            return guestSelectionResult;

        // Every successful card selection closes the picker. The next state
        // recognizer will verify the formation screen before Start Career.
        return null;
    }

    private string? GetAutomaticGuestSupportType(
        IReadOnlyDictionary<string, int> requiredTypes)
    {
        var candidateTypes = requiredTypes.ContainsKey("Friend")
            ? _umaDatabase.SupportCards
                .Where(card => card.Available && !string.IsNullOrWhiteSpace(card.Type))
                .Select(card => card.Type.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : requiredTypes.Keys
                .Where(type => GetSupportFilterKey(type) is not null)
                .ToArray();

        return candidateTypes.FirstOrDefault();
    }

    private async Task<CareerTrainingResult?> ConfigureHighestStarFilterAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string? supportType,
        string? rarity,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken,
        bool friendPage = false)
    {
        return await ConfigureHighestStarFilterAsync(
                connection,
                pack,
                supportType is null ? Array.Empty<string>() : [supportType],
                rarity,
                logSink,
                cancellationToken,
                friendPage)
            .ConfigureAwait(false);
    }

    private async Task<CareerTrainingResult?> ConfigureHighestStarFilterAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        IReadOnlyList<string> supportTypes,
        string? rarity,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken,
        bool friendPage = false)
    {
        var actions = BuildHighestStarFilterActionsForTypes(supportTypes, rarity)
            .Select(action => friendPage && action.Equals(
                    "ranked.sort_level",
                    StringComparison.OrdinalIgnoreCase)
                ? "ranked.friend_sort_level"
                : action);

        foreach (var action in actions)
        {
            var result = await RunScreenActionAsync(
                    connection,
                    pack,
                    "support_select",
                    action,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is not null)
                return result;
        }

        return null;
    }

    internal static IReadOnlyList<string> BuildHighestStarFilterActions(
        string? supportType,
        string? rarity)
        => BuildHighestStarFilterActionsForTypes(
            supportType is null ? Array.Empty<string>() : [supportType],
            rarity);

    internal static IReadOnlyList<string> BuildHighestStarFilterActionsForTypes(
        IEnumerable<string> supportTypes,
        string? rarity)
    {
        var supportFilterKeys = supportTypes
            .Select(GetSupportFilterKey)
            .Where(key => key is not null)
            .Select(key => key!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (supportFilterKeys.Length > 1)
        {
            throw new InvalidOperationException(
                "Support card filtering accepts exactly one category per pass.");
        }

        var actions = new List<string>
        {
            "ranked.display_settings",
            "ranked.filter_tab",
            "ranked.filter_reset",
        };

        var requestedRarity = GetSupportRarityFilter(rarity);
        foreach (var availableRarity in requestedRarity is null
                     ? new[] { "SSR", "SR" }
                     : new[] { requestedRarity })
        {
            actions.Add($"ranked.filter_{availableRarity.ToLowerInvariant()}");
        }

        foreach (var supportFilterKey in supportFilterKeys)
        {
            if (!actions.Contains($"ranked.filter_{supportFilterKey}", StringComparer.OrdinalIgnoreCase))
            {
                actions.Add($"ranked.filter_{supportFilterKey}");
            }
        }

        actions.Add("ranked.filter_apply");
        // Applying the filter returns to the card list and resets the list
        // display/sort controls on some game builds. Set Level only
        // after Apply. Re-open the display settings, commit the primary sort,
        // then switch the list to Desc so the following card scan sees the
        // highest-level copy first.
        actions.Add("ranked.display_settings");
        actions.Add("ranked.sort_level");
        actions.Add("ranked.sort_apply");
        actions.Add("ranked.sort_desc");
        return actions;
    }

    private static Dictionary<string, int>? GetOwnRequiredSupportTypes(
        IReadOnlyDictionary<string, int> requiredTypes,
        UmaSupportCardRecord guestCard)
    {
        var ownTypes = requiredTypes.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.OrdinalIgnoreCase);

        // This preset explicitly means five own cards plus one guest card.
        if (ownTypes.Remove("Friend"))
            return ownTypes;

        var guestType = guestCard.Type?.Trim();
        if (string.IsNullOrWhiteSpace(guestType)
            || !ownTypes.TryGetValue(guestType, out var guestTypeCount)
            || guestTypeCount <= 0)
        {
            return null;
        }

        if (guestTypeCount == 1)
            ownTypes.Remove(guestType);
        else
            ownTypes[guestType] = guestTypeCount - 1;

        return ownTypes;
    }

    private Task<CareerTrainingResult?> SelectHighestSupportCardAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string? supportType,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        return SelectHighestUnselectedSupportCardAsync(
            connection,
            pack,
            supportType,
            logSink,
            cancellationToken);
    }

    private Task<CareerTrainingResult?> SelectHighestGuestSupportCardAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string supportType,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var typeTemplate = ResolveFriendSupportTypeBadgeTemplate(pack, supportType);
        if (typeTemplate is null)
        {
            return Task.FromResult<CareerTrainingResult?>(Failure(
                $"Support type '{supportType}' has no recognition template.",
                "support_select"));
        }

        return RunScreenActionAsync(
            connection,
            pack,
            "support_select",
            "ranked.select_friend_highest_card",
            logSink,
            cancellationToken,
            new HachimiPipelineRunOptions
            {
                TemplateOverrides = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["support_select_support_friend_top_card_ssr"] = typeTemplate,
                    ["support_select_support_friend_top_card_sr"] = typeTemplate,
                },
            });
    }

    private async Task<CareerTrainingResult?> SelectHighestUnselectedSupportCardAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string? supportType,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var typeTemplate = ResolveSupportTypeBadgeTemplate(pack, supportType ?? string.Empty);
        if (typeTemplate is null)
        {
            return Failure(
                $"Support type '{supportType ?? "unknown"}' has no recognition template.",
                "support_select");
        }

        foreach (var slotRoi in RankedSupportCardSlotRois)
        {
            var selectedProbe = await RunScreenActionAsync(
                    connection,
                    pack,
                    "support_select",
                    "ranked.detect_selected_card",
                    logSink,
                    cancellationToken,
                    new HachimiPipelineRunOptions
                    {
                        RoiOverrides = new Dictionary<string, int[]>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["support_select_support_selected_card"] = slotRoi,
                        },
                    })
                .ConfigureAwait(false);

            if (selectedProbe is null)
                continue;

            if (!IsExpectedTemplateMiss(
                    selectedProbe,
                    "support_select_support_selected_card"))
            {
                return selectedProbe;
            }

            var selection = await RunScreenActionAsync(
                    connection,
                    pack,
                    "support_select",
                    "ranked.select_highest_card",
                    logSink,
                    cancellationToken,
                    new HachimiPipelineRunOptions
                    {
                        TemplateOverrides = new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            // Click the recognized type badge inside the
                            // first unselected card. The ROI only limits the
                            // search; the tap comes from the template match.
                            ["support_select_support_top_card_ssr"] = typeTemplate,
                            ["support_select_support_top_card_sr"] = typeTemplate,
                        },
                        RoiOverrides = new Dictionary<string, int[]>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["support_select_support_top_card_ssr"] = slotRoi,
                            ["support_select_support_top_card_sr"] = slotRoi,
                        },
                    })
                .ConfigureAwait(false);

            if (selection is null)
                return null;

            if (!IsExpectedTemplateMiss(
                    selection,
                    "support_select_support_top_card_ssr",
                    "support_select_support_top_card_sr"))
            {
                return selection;
            }
        }

        return Failure(
            "Could not find an unselected SSR/SR support card in the ranked list.",
            "support_select");
    }

    private static bool IsExpectedTemplateMiss(
        CareerTrainingResult result,
        params string[] taskNames) =>
        result.LastScreenId.Equals("support_select", StringComparison.OrdinalIgnoreCase)
        && taskNames.Any(taskName => result.Message.Contains(
            $"Timed out waiting for JSON task '{taskName}'",
            StringComparison.OrdinalIgnoreCase));

    private static string? GetSupportFilterKey(string? supportType) =>
        supportType?.Trim().ToLowerInvariant() switch
        {
            "speed" => "speed",
            "stamina" => "stamina",
            "power" => "power",
            "guts" => "guts",
            "wit" => "wit",
            "friend" => "friend",
            _ => null,
        };

    private static string? GetSupportRarityFilter(string? rarity) =>
        rarity?.Trim().ToUpperInvariant() switch
        {
            "3" or "SSR" => "SSR",
            "2" or "SR" => "SR",
            "1" or "R" => "R",
            _ => null,
        };

    private static string? ResolveSupportTypeBadgeTemplate(
        UraScenarioPack pack,
        string supportType)
    {
        var filterKey = GetSupportFilterKey(supportType);
        if (filterKey is null)
            return null;

        var path = UraScenarioResourceResolver.Resolve(
            pack,
            $"screens/templates/support_cards/type_{filterKey}.png");
        return File.Exists(path) ? path : null;
    }

    private static string? ResolveFriendSupportTypeBadgeTemplate(
        UraScenarioPack pack,
        string supportType)
    {
        var filterKey = GetSupportFilterKey(supportType);
        if (filterKey is null)
            return null;

        var friendPath = UraScenarioResourceResolver.Resolve(
            pack,
            $"screens/templates/support_cards/friend_type_{filterKey}.png");
        // Borrow Card badges include card art behind the icon, so the normal
        // deck templates are not safe fallbacks here. Require a dedicated
        // friend-page template for every supported category.
        return File.Exists(friendPath) ? friendPath : null;
    }

    private string? ResolveSupportCardTemplate(UraScenarioPack pack, int supportCardId)
    {
        var directory = _umaDatabase.GetSupportCardTemplateDirectory(supportCardId);
        if (Directory.Exists(directory))
        {
            var template = Directory.EnumerateFiles(directory)
                .Where(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (template is not null)
                return template;
        }

        var fallback = Path.Combine(
            pack.RootDirectory,
            "screens",
            "templates",
            "support_cards",
            supportCardId.ToString(CultureInfo.InvariantCulture) + ".png");
        return File.Exists(fallback) ? fallback : null;
    }

    private static Dictionary<string, int>? GetRequiredSupportTypes(
        string supportDeckPreset) =>
        supportDeckPreset.ToLowerInvariant() switch
        {
            "speed3-stamina3" => new Dictionary<string, int>
            {
                ["Speed"] = 3,
                ["Stamina"] = 3,
            },
            "speed3-stamina2-wit1" => new Dictionary<string, int>
            {
                ["Speed"] = 3,
                ["Stamina"] = 2,
                ["Wit"] = 1,
            },
            "speed2-stamina2-power1-wit1" => new Dictionary<string, int>
            {
                ["Speed"] = 2,
                ["Stamina"] = 2,
                ["Power"] = 1,
                ["Wit"] = 1,
            },
            "speed2-stamina1-power1-wit1-friend1" => new Dictionary<string, int>
            {
                ["Speed"] = 2,
                ["Stamina"] = 1,
                ["Power"] = 1,
                ["Wit"] = 1,
                ["Friend"] = 1,
            },
            _ => null,
        };

    private void ValidateSupportCards(
        IReadOnlyList<int> supportCardIds,
        string supportDeckPreset)
    {
        if (supportCardIds.Count > 0 && supportCardIds.Count is not (5 or 6))
        {
            throw new InvalidOperationException(
                "A configured support deck must contain 5 own cards, or 5 own cards plus 1 friend card.");
        }

        var cards = new List<UmaSupportCardRecord>(supportCardIds.Count);
        foreach (var id in supportCardIds)
        {
            if (!_umaDatabase.TryGetSupportCard(id, out var card) || card is null || !card.Available)
            {
                throw new InvalidOperationException(
                    $"Configured support card ID {id.ToString(CultureInfo.InvariantCulture)} "
                    + "was not found or is unavailable.");
            }

            cards.Add(card);
        }

        var requiredTypes = GetRequiredSupportTypes(supportDeckPreset);
        if (requiredTypes is null)
            return;

        if (supportCardIds.Count != 6)
        {
            throw new InvalidOperationException(
                $"Support deck preset '{supportDeckPreset}' requires exactly 6 cards.");
        }

        var actualTypes = cards
            .GroupBy(card => card.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var required in requiredTypes)
        {
            if (!actualTypes.TryGetValue(required.Key, out var actual)
                || actual != required.Value)
            {
                throw new InvalidOperationException(
                    $"Support deck does not match preset '{supportDeckPreset}': "
                    + $"expected {required.Value} {required.Key}, got {actual}.");
            }
        }
    }

    private void ValidateFriendSupportCard(
        CareerTrainingSettings settings,
        bool allowCustomPreset = false)
    {
        var requiredTypes = GetRequiredSupportTypes(settings.SupportDeckPreset);
        if (requiredTypes is null && !allowCustomPreset)
        {
            return;
        }

        if (settings.FriendSupportCardId is not > 0)
            return;

        if (!_umaDatabase.TryGetSupportCard(
                settings.FriendSupportCardId.Value,
                out var friendCard)
            || friendCard is null
            || !friendCard.Available)
        {
            throw new InvalidOperationException(
                $"Configured guest support card {settings.FriendSupportCardId.Value.ToString(CultureInfo.InvariantCulture)} "
                + "was not found or is unavailable.");
        }

        if (requiredTypes is null || requiredTypes.ContainsKey("Friend"))
            return;

        var guestType = friendCard.Type?.Trim();
        if (string.IsNullOrWhiteSpace(guestType)
            || !requiredTypes.TryGetValue(guestType, out var requiredCount)
            || requiredCount <= 0)
        {
            throw new InvalidOperationException(
                $"Configured guest support card {settings.FriendSupportCardId.Value.ToString(CultureInfo.InvariantCulture)} "
                + $"has type '{friendCard.Type}' which is not part of preset '{settings.SupportDeckPreset}'.");
        }
    }

    private static CareerTrainingResult Failure(
        string message,
        string lastScreenId,
        int actionsCompleted = 0) =>
        new(false, message, actionsCompleted, lastScreenId);

    private sealed record UraObservation(string ScreenId, double Score);
}
