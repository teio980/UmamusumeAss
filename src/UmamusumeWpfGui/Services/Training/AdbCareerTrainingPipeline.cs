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

    private static readonly HashSet<string> CareerEntryScreenIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "home",
            "scenario_select",
            "legacy_select",
            "trainee_select",
            "support_select",
            "support_autofill_confirmation",
            "support_ready",
            "career_races_ready",
            "career_entry",
            "career_intro_event",
            "career_main",
        };

    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly IUmaDatabaseService _umaDatabase;
    private readonly UraTraineeSelector _traineeSelector;
    private readonly UraLegacySelector _legacySelector;
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
        HachimiJsonPipelineRunner jsonRunner)
    {
        ArgumentNullException.ThrowIfNull(visualRuntime);
        ArgumentNullException.ThrowIfNull(umaDatabase);
        ArgumentNullException.ThrowIfNull(traineeSelector);
        ArgumentNullException.ThrowIfNull(legacySelector);
        ArgumentNullException.ThrowIfNull(jsonRunner);
        _visualRuntime = visualRuntime;
        _umaDatabase = umaDatabase;
        _traineeSelector = traineeSelector;
        _legacySelector = legacySelector;
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
            ValidateSupportCards(settings.SupportCardIds, settings.SupportDeckPreset);
        }
        else if (settings.SupportDeckMode.Equals("highest-star", StringComparison.OrdinalIgnoreCase)
            && GetRequiredSupportTypes(settings.SupportDeckPreset) is null)
        {
            throw new InvalidOperationException(
                "Highest-star support selection requires a support deck preset.");
        }
        var pack = await UraScenarioPackLoader.LoadAsync(settings.ManifestPath, cancellationToken)
            .ConfigureAwait(false);
        logSink?.Add(
            "Career Training",
            $"Loaded {pack.Manifest.DisplayName} for {trainee.NameEn} ({trainee.TraineeId}).");

        var scenario = new UraScenarioModule(pack);
        var strategy = UraStrategyRegistry.Create(settings.StrategyId);
        var checkpointStore = new UraCheckpointStore(settings.TraineeId);
        var state = await checkpointStore.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? scenario.CreateInitialState();
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
            state.TurnIndex > 0
                ? $"Resuming checkpoint at turn {state.TurnIndex}, objective {state.CurrentObjectiveId}."
                : "Starting a new URA career session.");

        var actionCount = 0;
        var setupObservationRetryCount = 0;
        var careerEntryFlowStarted = state.CareerEntryOpened;
        if (!state.CareerStarted && !state.CareerEntryOpened)
        {
            logSink?.Add("Career Training", "Entering Career from the game Home screen.");
            state.CareerEntryOpened = await EnsureCareerEntryAsync(
                    connection,
                    pack,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!state.CareerEntryOpened)
            {
                return Failure(
                    "Could not enter Career from the game Home screen.",
                    "home",
                    actionCount);
            }

            careerEntryFlowStarted = true;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            actionCount++;
        }

        while (actionCount < 300)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = await ObserveAsync(connection, pack, state, cancellationToken)
                .ConfigureAwait(false);
            if (observation is null)
            {
                if (state.CareerEntryOpened
                    && !state.CareerStarted
                    && setupObservationRetryCount < 12)
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

                    await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
                    actionCount++;
                    continue;
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
                var supportDeckMode = settings.SupportDeckMode.Trim().ToLowerInvariant();
                if (supportDeckMode == "highest-star")
                {
                    if (state.SupportCardsSelected)
                    {
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
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (rankedSelectionResult is not null)
                        return rankedSelectionResult;

                    state.SupportCardsSelected = true;
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
                            logSink,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (supportSelectionResult is not null)
                        return supportSelectionResult;

                    state.SupportCardsSelected = true;
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
                "Career Training",
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
                "Career Training",
                $"Could not enter Career from the shared Home entry task: {result.Message}",
                LogEntryKind.Failure);
            return false;
        }

        logSink?.Add("Career Training", "Opened Career from the shared Home tab.");
        return true;
    }

    private async Task<UraObservation?> ObserveAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        UraCareerSessionState state,
        CancellationToken cancellationToken)
    {
        var careerEntryFlowActive = state.CareerEntryOpened && !state.CareerStarted;
        var traineeSelectionExpected = state.ScenarioSelected
            && !state.TraineeSelected
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

    private async Task<CareerTrainingResult?> SelectConfiguredSupportCardsAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        IReadOnlyList<int> supportCardIds,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var resetResult = await RunScreenActionAsync(
                connection,
                pack,
                "support_select",
                "reset",
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (resetResult is not null)
            return resetResult;

        var openResult = await RunScreenActionAsync(
                connection,
                pack,
                "support_select",
                "open",
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (openResult is not null)
            return openResult;

        foreach (var supportCardId in supportCardIds)
        {
            var templatePath = ResolveSupportCardTemplate(pack, supportCardId);
            if (templatePath is null)
            {
                return Failure(
                    $"Support card {supportCardId.ToString(CultureInfo.InvariantCulture)} "
                    + "has no local selection template.",
                    "support_select");
            }

            var scrollTopResult = await RunScreenActionAsync(
                    connection,
                    pack,
                    "support_select",
                    "scroll_top",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (scrollTopResult is not null)
                return scrollTopResult;

            var result = await RunScreenActionAsync(
                    connection,
                    pack,
                    "support_select",
                    "select",
                    logSink,
                    cancellationToken,
                    new HachimiPipelineRunOptions
                    {
                        TemplateOverrides = new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["support_select_support_card"] = templatePath,
                        },
                    })
                .ConfigureAwait(false);
            if (result is not null)
                return result;
        }

        return await RunScreenActionAsync(
                connection,
                pack,
                "support_select",
                "close",
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CareerTrainingResult?> SelectHighestStarSupportCardsAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string supportDeckPreset,
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

        var resetResult = await RunScreenActionAsync(
                connection,
                pack,
                "support_select",
                "reset",
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (resetResult is not null)
            return resetResult;

        var openResult = await RunScreenActionAsync(
                connection,
                pack,
                "support_select",
                "open",
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (openResult is not null)
            return openResult;

        var scrollTopResult = await RunScreenActionAsync(
                connection,
                pack,
                "support_select",
                "scroll_top",
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (scrollTopResult is not null)
            return scrollTopResult;

        foreach (var required in requiredTypes)
        {
            var remaining = required.Value;
            foreach (var rarity in new[] { "SSR", "SR" })
            {
                if (remaining == 0)
                    break;

                var filterResult = await ConfigureHighestStarFilterAsync(
                        connection,
                        pack,
                        required.Key,
                        rarity,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (filterResult is not null)
                    return filterResult;

                var badgePath = ResolveSupportCardRarityBadge(pack, rarity);
                if (badgePath is null)
                {
                    return Failure(
                        $"The {rarity} support-card badge template is missing.",
                        "support_select");
                }

                var selected = await SelectRankedSupportCardSlotsAsync(
                        connection,
                        pack,
                        badgePath,
                        remaining,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (selected.Failure is not null)
                    return selected.Failure;
                remaining -= selected.SelectedCount;
            }

            if (remaining > 0)
            {
                return Failure(
                    $"Could not find {remaining} more {required.Key} support card(s) after checking SSR and SR.",
                    "support_select");
            }
        }

        return await RunScreenActionAsync(
                connection,
                pack,
                "support_select",
                "close",
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CareerTrainingResult?> ConfigureHighestStarFilterAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string supportType,
        string rarity,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var actions = new List<string>
        {
            "ranked.display_settings",
            "ranked.sort_uncap",
            "ranked.filter_tab",
            "ranked.filter_reset",
        };

        foreach (var otherRarity in new[] { "R", "SR", "SSR" })
        {
            if (!otherRarity.Equals(rarity, StringComparison.OrdinalIgnoreCase))
                actions.Add($"ranked.filter_{otherRarity.ToLowerInvariant()}");
        }

        foreach (var otherType in new[] { "Speed", "Stamina", "Power", "Guts", "Wit", "Friend" })
        {
            if (!otherType.Equals(supportType, StringComparison.OrdinalIgnoreCase))
                actions.Add($"ranked.filter_{GetSupportFilterKey(otherType)}");
        }

        actions.Add("ranked.filter_apply");
        actions.Add("ranked.sort_desc");

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

    private async Task<RankedSupportSlotResult> SelectRankedSupportCardSlotsAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string badgePath,
        int requiredCount,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var selectedCount = 0;
        foreach (var slotRoi in GetSupportCardBadgeRois())
        {
            if (selectedCount >= requiredCount)
                break;

            var result = await RunScreenActionAsync(
                    connection,
                    pack,
                    "support_select",
                    "ranked.select_card_badge",
                    logSink,
                    cancellationToken,
                    new HachimiPipelineRunOptions
                    {
                        TemplateOverrides = new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["support_select_support_card_badge"] = badgePath,
                        },
                        RoiOverrides = new Dictionary<string, int[]>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["support_select_support_card_badge"] = slotRoi,
                        },
                    })
                .ConfigureAwait(false);
            if (result is null)
            {
                selectedCount++;
                continue;
            }

            // A missing badge means this sorted slot is empty. Continue to
            // the next slot so SSR can fall back to SR without guessing.
        }

        return new RankedSupportSlotResult(selectedCount, null);
    }

    private static string? ResolveSupportCardRarityBadge(
        UraScenarioPack pack,
        string rarity)
    {
        var path = Path.Combine(
            pack.RootDirectory,
            "screens",
            "templates",
            "support_cards",
            rarity.ToLowerInvariant() + "_badge.png");
        return File.Exists(path) ? path : null;
    }

    private static List<int[]> GetSupportCardBadgeRois()
    {
        var rois = new List<int[]>(25);
        foreach (var y in new[] { 140, 353, 566, 779, 992 })
        {
            foreach (var x in new[] { 38, 204, 371, 538, 705 })
                rois.Add([x, y, 75, 80]);
        }

        return rois;
    }

    private static string GetSupportFilterKey(string supportType) =>
        supportType.ToLowerInvariant() switch
        {
            "speed" => "speed",
            "stamina" => "stamina",
            "power" => "power",
            "guts" => "guts",
            "wit" => "wit",
            "friend" => "friend",
            _ => throw new InvalidOperationException(
                $"Unsupported support type '{supportType}'."),
        };

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

    private static CareerTrainingResult Failure(
        string message,
        string lastScreenId,
        int actionsCompleted = 0) =>
        new(false, message, actionsCompleted, lastScreenId);

    private sealed record RankedSupportSlotResult(
        int SelectedCount,
        CareerTrainingResult? Failure);

    private sealed record UraObservation(string ScreenId, double Score);
}
