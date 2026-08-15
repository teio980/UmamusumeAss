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

        ValidateSupportCards(settings.SupportCardIds);
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
                    return await RunScreenActionAsync(
                            connection,
                            pack,
                            "trainee_select",
                            "next",
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
                if (traineePickResult is null)
                    state.TraineeSelected = true;
                return traineePickResult;
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
                if (state.LegacySelected)
                {
                    return await RunScreenActionAsync(
                            connection, pack, "legacy_select", "next", logSink, cancellationToken)
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

    private static CareerTrainingResult Failure(
        string message,
        string lastScreenId,
        int actionsCompleted = 0) =>
        new(false, message, actionsCompleted, lastScreenId);

    private sealed record UraObservation(string ScreenId, double Score);
}
