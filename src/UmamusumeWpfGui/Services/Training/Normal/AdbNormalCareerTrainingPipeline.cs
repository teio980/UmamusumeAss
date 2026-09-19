using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public sealed class AdbNormalCareerTrainingPipeline : ICareerTrainingPipeline
{
    private const double EarlyRecognitionThreshold = 0.985;
    private const string CareerFinalConfirmationScreenId = "career_final_confirmation";
    private const string CareerStartTransitionScreenId = "career_start_transition";
    private sealed record StartupPageStage(
        string RecognitionScreenId,
        string ResumeScreenId,
        NormalCareerSetupStage SetupStage,
        CareerEntryNavigationStep? EntryStep = null);

    private static readonly StartupPageStage[] StartupPageStages =
    [
        new(
            "normal_quick_mode_settings",
            "normal_quick_mode_settings",
            NormalCareerSetupStage.ConfigureQuickMode),
        new(
            "normal_scenario_select_startup",
            "scenario_select",
            NormalCareerSetupStage.EnterCareer,
            CareerEntryNavigationStep.Scenario),
        new(
            "normal_trainee_select_startup",
            "trainee_select",
            NormalCareerSetupStage.EnterCareer,
            CareerEntryNavigationStep.Trainee),
        new(
            "normal_legacy_select_startup",
            "legacy_select",
            NormalCareerSetupStage.EnterCareer,
            CareerEntryNavigationStep.Legacy),
        new(
            "normal_support_select_startup",
            "support_select",
            NormalCareerSetupStage.EnterCareer,
            CareerEntryNavigationStep.Support),
        new(
            "normal_final_confirmation_startup",
            "career_final_confirmation",
            NormalCareerSetupStage.ConfigureMode),
    ];

    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly IUmaDatabaseService _umaDatabase;
    private readonly CareerEntryNavigator _entryNavigator;
    private readonly UraRaceResultRecognizer _raceResultRecognizer;
    private readonly HachimiJsonPipelineRunner _jsonRunner;
    private readonly ConcurrentDictionary<string, Lazy<Task<GrayImage?>>> _templateCache = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly object _runLock = new();
    private CancellationTokenSource? _runCancellation;
    private IHachimiTaskLogSink? _taskLogSink;

    public AdbNormalCareerTrainingPipeline(
        IVisualPipelineRuntime visualRuntime,
        IUmaDatabaseService umaDatabase,
        CareerEntryNavigator entryNavigator,
        HachimiJsonPipelineRunner jsonRunner)
    {
        ArgumentNullException.ThrowIfNull(visualRuntime);
        ArgumentNullException.ThrowIfNull(umaDatabase);
        ArgumentNullException.ThrowIfNull(entryNavigator);
        ArgumentNullException.ThrowIfNull(jsonRunner);
        _visualRuntime = visualRuntime;
        _umaDatabase = umaDatabase;
        _entryNavigator = entryNavigator;
        _raceResultRecognizer = new UraRaceResultRecognizer(visualRuntime);
        _jsonRunner = jsonRunner;
    }

    public async Task<CareerTrainingResult> RunAsync(
        LastVerifiedConnection connection,
        CareerTrainingSettings settings,
        IGrassTaskLogSink? logSink,
        IHachimiTaskLogSink? taskLogSink = null,
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
            _taskLogSink = taskLogSink;
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
        IHachimiTaskLogSink? taskLogSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_runLock)
        {
            _runCancellation?.Cancel();
        }

        logSink?.Add("Career Training", "Stop requested.");
        taskLogSink?.Add("Stop", "Stop requested.", HachimiTaskLogEventKind.Warning);
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
            && SupportDeckPresetCatalog.GetRequiredTypes(settings.SupportDeckPreset) is null)
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
        if (!CareerStrategyCatalog.TryGetLineupStrategyUiMapping(
                settings.LineupStrategy,
                out _))
        {
            return Failure(
                $"Normal Career lineup strategy '{settings.LineupStrategy}' is invalid.",
                "career_final_confirmation");
        }
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
            state.CareerStarted = false;
        }
        if (!string.Equals(state.ScenarioId, pack.Manifest.ScenarioId, StringComparison.OrdinalIgnoreCase))
            state = scenario.CreateInitialState();
        NormalizeSetupStage(state);
        logSink?.Add(
            "Career Training",
            "Normal Career mode selected; Final Confirmation will use Normal Career start.");
        _taskLogSink?.Add(
            "Setup",
            settings.ContinueExistingCareer
                ? "Normal Career selected; existing career will be resumed."
                : "Normal Career selected; a new career will be started.",
            HachimiTaskLogEventKind.Action);
        logSink?.Add(
            "Career Training",
            state.TurnIndex > 0
                ? $"Resuming checkpoint at turn {state.TurnIndex}, objective {state.CurrentObjectiveId}."
                : settings.ContinueExistingCareer
                    ? "No local Career turn checkpoint; the in-game Resume action will be used."
                    : "Starting a new URA career session.");
        logSink?.Add(
            "Career Training",
            settings.ContinueExistingCareer
                ? "Existing Career handling selected: Resume."
                : "Existing Career handling selected: Delete Data.");

        CareerEntryNavigationStep? startupEntryStep = null;

        // Recover the first supported mid-flow page before invoking the shared
        // Home -> Career navigator. Each recoverable page intentionally uses
        // one small, stable recognition template and maps to a setup stage;
        // future pages can be added to StartupPageStages without changing the
        // entry navigator or the turn engine.
        if (!state.CareerStarted
            && state.NormalSetupStage == NormalCareerSetupStage.EnterCareer)
        {
            var startupPage = await DetectNormalStartupPageAsync(
                    connection,
                    pack,
                    cancellationToken)
                .ConfigureAwait(false);
            if (startupPage is { } detected)
            {
                state.NormalSetupStage = detected.SetupStage;
                state.LastScreenId = detected.ResumeScreenId;
                startupEntryStep = detected.EntryStep;
                await checkpointStore.SaveAsync(state, cancellationToken)
                    .ConfigureAwait(false);
                logSink?.Add(
                    "Career Training",
                    $"Startup page recognized as {detected.RecognitionScreenId}; "
                    + $"resuming from {detected.ResumeScreenId}.");
            }
        }

        var actionCount = 0;
        var setupObservationRetryCount = 0;
        var careerStartTransitionExpected = !state.CareerStarted
            && (state.NormalSetupStage == NormalCareerSetupStage.AwaitCareerMain
                || IsPersistedCareerStartTransitionExpected(state));
        if (!state.CareerStarted
            && state.NormalSetupStage == NormalCareerSetupStage.EnterCareer
            && !careerStartTransitionExpected)
        {
            var entryState = new CareerEntryNavigationState
            {
                // A recognized startup page may already be inside the shared
                // Career entry flow. Seed the navigator at that step so it
                // continues from the current page instead of clicking Home
                // and Career again.
                Step = startupEntryStep ?? CareerEntryNavigationStep.Home,
                // A zero-turn checkpoint can contain a stale entry screen
                // from an interrupted setup. A new career must always start
                // through the shared Home -> Career entry chain.
                LastScreenId = startupEntryStep is not null
                    ? state.LastScreenId
                    : state.TurnIndex == 0
                    ? "unknown"
                    : state.LastScreenId,
                ActionsCompleted = actionCount,
            };
            var entry = await _entryNavigator.NavigateAsync(
                    connection,
                    pack,
                    settings,
                    entryState,
                    logSink,
                    progressCallback: async progress =>
                    {
                        state.LastScreenId = progress.LastScreenId;
                        await checkpointStore.SaveAsync(state, cancellationToken)
                            .ConfigureAwait(false);
                    },
                    _taskLogSink,
                    cancellationToken)
                .ConfigureAwait(false);
            actionCount = entry.ActionsCompleted;
            state.LastScreenId = entry.LastScreenId;
            if (!entry.Succeeded)
            {
                await checkpointStore.SaveAsync(state, cancellationToken)
                    .ConfigureAwait(false);
                return Failure(
                    entry.Message,
                    entry.LastScreenId,
                    actionCount);
            }

            // The shared navigator intentionally stops at Final Confirmation.
            // Normal Career has a small mode/strategy setup on this page.
            state.NormalSetupStage = NormalCareerSetupStage.ConfigureMode;
            state.LastScreenId = CareerFinalConfirmationScreenId;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (!state.CareerStarted
            && IsPendingNormalSetupStage(state.NormalSetupStage))
        {
            var setupFailure = await ConfigureNormalCareerAsync(
                    connection,
                    pack,
                    settings,
                    state,
                    checkpointStore,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (setupFailure is not null)
                return setupFailure with { ActionsCompleted = actionCount };
        }

        while (actionCount < 300)
        {
            cancellationToken.ThrowIfCancellationRequested();
            careerStartTransitionExpected = !state.CareerStarted
                && (state.NormalSetupStage == NormalCareerSetupStage.AwaitCareerMain
                    || IsPersistedCareerStartTransitionExpected(state));
            var observation = await ObserveAsync(
                    connection,
                    pack,
                    state,
                    careerStartTransitionExpected,
                    cancellationToken)
                .ConfigureAwait(false);
            if (observation is null)
            {
                var setupRetryLimit = careerStartTransitionExpected ? 40 : 12;
                if (careerStartTransitionExpected
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
            state.LastScreenId = observation.ScreenId;
            scenario.ObserveScreen(state, observation.ScreenId, observation.Score);
            if (state.CareerStarted)
                state.NormalSetupStage = NormalCareerSetupStage.InCareer;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            logSink?.Add(
                "Career Training",
                $"Recognized {observation.ScreenId} with score {observation.Score:0.000}.");
            if (IsImportantCareerScreen(observation.ScreenId))
            {
                _taskLogSink?.Add(
                    "Screen",
                    $"Reached {FriendlyCareerScreen(observation.ScreenId)}.",
                    HachimiTaskLogEventKind.Detection);
            }

            if (observation.ScreenId == "home")
            {
                if (!state.CareerStarted)
                {
                    return Failure(
                        "Career start did not reach the Career screen.",
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
                    settings.PauseOnUnknownOutcome,
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

            // Persist observations so a restart can resume from the shared
            // entry flow or the current Career screen.
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            actionCount++;
        }

        await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return Failure(
            "Career training exceeded the safety action limit and was paused.",
            state.LastScreenId,
            actionCount);
    }

    private static void NormalizeSetupStage(UraCareerSessionState state)
    {
        if (state.CareerStarted)
        {
            state.NormalSetupStage = NormalCareerSetupStage.InCareer;
            return;
        }

        if (state.NormalSetupStage != NormalCareerSetupStage.EnterCareer)
            return;

        state.NormalSetupStage = state.LastScreenId.Trim().ToLowerInvariant() switch
        {
            CareerFinalConfirmationScreenId => NormalCareerSetupStage.ConfigureMode,
            CareerStartTransitionScreenId => NormalCareerSetupStage.AwaitCareerMain,
            "career_main" when state.TurnIndex > 0 => NormalCareerSetupStage.InCareer,
            _ => NormalCareerSetupStage.EnterCareer,
        };
    }

    private static bool IsPendingNormalSetupStage(NormalCareerSetupStage stage) => stage is
        NormalCareerSetupStage.ConfigureMode
            or NormalCareerSetupStage.ConfigureStrategy
            or NormalCareerSetupStage.StartCareer
            or NormalCareerSetupStage.ConfirmStart
            or NormalCareerSetupStage.SkipIntro
            or NormalCareerSetupStage.ConfigureQuickMode
            or NormalCareerSetupStage.SetQuickMode
            or NormalCareerSetupStage.ConfirmQuickMode;

    private async Task<StartupPageStage?> DetectNormalStartupPageAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        CancellationToken cancellationToken)
    {
        var frame = await _visualRuntime.CaptureGrayAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (frame is null)
            return null;

        foreach (var candidate in StartupPageStages)
        {
            var screen = pack.ScreenProfile.Find(candidate.RecognitionScreenId);
            if (screen is null || screen.Templates.Count != 1)
                continue;

            var template = await LoadTemplateCachedAsync(
                    ResolveCapture(pack, screen.Templates[0]),
                    cancellationToken)
                .ConfigureAwait(false);
            if (template is null)
                continue;

            var match = TemplateMatcher.Find(
                frame,
                template,
                screen.Recognition.Roi,
                screen.Recognition.TemplateThreshold,
                pack.ScreenProfile.ReferenceWidth,
                pack.ScreenProfile.ReferenceHeight);
            if (match.Found)
                return candidate;
        }

        return null;
    }

    private async Task<CareerTrainingResult?> ConfigureNormalCareerAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        CareerTrainingSettings settings,
        UraCareerSessionState state,
        UraCheckpointStore checkpointStore,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        if (state.NormalSetupStage == NormalCareerSetupStage.ConfigureMode)
        {
            var mode = await RunNormalSetupActionAsync(
                    connection,
                    pack,
                    "normal.select_mode",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (mode is not null)
                return mode;

            state.NormalSetupStage = NormalCareerSetupStage.ConfigureStrategy;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.NormalSetupStage == NormalCareerSetupStage.ConfigureStrategy)
        {
            if (!CareerStrategyCatalog.TryGetLineupStrategySemanticAction(
                    settings.LineupStrategy,
                    "normal",
                    out var strategyAction,
                    out _))
            {
                return Failure(
                    $"Normal Career lineup strategy '{settings.LineupStrategy}' is invalid.",
                    state.LastScreenId);
            }

            foreach (var action in new[]
            {
                CareerStrategyCatalog.StrategyChangeSemanticAction("normal"),
                strategyAction,
                CareerStrategyCatalog.StrategySaveSemanticAction("normal"),
                CareerStrategyCatalog.StrategyReturnSemanticAction("normal"),
            })
            {
                var result = await RunNormalSetupActionAsync(
                        connection,
                        pack,
                        action,
                        logSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result is not null)
                    return result;
            }

            state.NormalSetupStage = NormalCareerSetupStage.StartCareer;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.NormalSetupStage == NormalCareerSetupStage.StartCareer)
        {
            // Persist the stage before the tap so an interrupted run resumes
            // with the confirmation action instead of clicking Start twice.
            state.NormalSetupStage = NormalCareerSetupStage.ConfirmStart;
            state.LastScreenId = CareerStartTransitionScreenId;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);

            var start = await RunNormalSetupActionAsync(
                    connection,
                    pack,
                    "normal.start",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (start is not null)
                return start;
        }

        if (state.NormalSetupStage == NormalCareerSetupStage.ConfirmStart)
        {
            var ok = await RunNormalSetupActionAsync(
                    connection,
                    pack,
                    "normal.post_start.ok",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (ok is not null)
                return ok;

            state.NormalSetupStage = NormalCareerSetupStage.SkipIntro;
            state.LastScreenId = "career_intro_event";
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.NormalSetupStage == NormalCareerSetupStage.SkipIntro)
        {
            // The first post-start setup step is the skip button on the
            // opening Tazuna introduction. Persist this stage before the tap
            // so an interrupted run resumes here instead of replaying Start.
            var skipIntro = await RunNormalSetupActionAsync(
                    connection,
                    pack,
                    "normal.post_start.skip",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (skipIntro is not null)
                return skipIntro;

            state.NormalSetupStage = NormalCareerSetupStage.ConfigureQuickMode;
            state.LastScreenId = "normal_quick_mode_settings";
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.NormalSetupStage == NormalCareerSetupStage.ConfigureQuickMode)
        {
            var shortenEvents = await RunNormalQuickModeActionAsync(
                    connection,
                    pack,
                    "normal.quick_mode.shorten",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (shortenEvents is not null)
                return shortenEvents;

            state.NormalSetupStage = NormalCareerSetupStage.SetQuickMode;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.NormalSetupStage == NormalCareerSetupStage.SetQuickMode)
        {
            var skipMode = await RunNormalQuickModeActionAsync(
                    connection,
                    pack,
                    "normal.quick_mode.skip",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (skipMode is not null)
                return skipMode;

            state.NormalSetupStage = NormalCareerSetupStage.ConfirmQuickMode;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        if (state.NormalSetupStage == NormalCareerSetupStage.ConfirmQuickMode)
        {
            var confirmQuickMode = await RunNormalQuickModeActionAsync(
                    connection,
                    pack,
                    "normal.quick_mode.confirm",
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (confirmQuickMode is not null)
                return confirmQuickMode;

            state.NormalSetupStage = NormalCareerSetupStage.AwaitCareerMain;
            state.LastScreenId = CareerStartTransitionScreenId;
            await checkpointStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<CareerTrainingResult?> RunNormalSetupActionAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string actionId,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var result = await RunScreenActionAsync(
                connection,
                pack,
                CareerFinalConfirmationScreenId,
                actionId,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
        {
            logSink?.Add("Career Training", $"Normal setup action completed: {actionId}.");
        }
        return result;
    }

    private async Task<CareerTrainingResult?> RunNormalQuickModeActionAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string actionId,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var result = await RunScreenActionAsync(
                connection,
                pack,
                "normal_quick_mode_settings",
                actionId,
                logSink,
                cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
        {
            logSink?.Add("Career Training", $"Normal Quick Mode action completed: {actionId}.");
        }
        return result;
    }

    private static bool IsImportantCareerScreen(string screenId) => screenId is
        "career_main" or "career_race_result" or "career_event";

    internal static bool IsPersistedCareerStartTransitionExpected(
        UraCareerSessionState state) =>
        state.TurnIndex > 0
        && state.LastScreenId.Equals(
            CareerStartTransitionScreenId,
            StringComparison.OrdinalIgnoreCase);

    private static string FriendlyCareerScreen(string screenId) => screenId switch
    {
        "career_main" => "the Career turn screen",
        "career_race_result" => "the race result",
        "career_event" => "the event choice",
        _ => screenId,
    };

    private async Task<CareerTrainingResult?> HandleScreenAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        bool pauseOnUnknownOutcome,
        UraScenarioModule scenario,
        UraDefaultStrategy strategy,
        UraCareerSessionState state,
        UraObservation observation,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        switch (observation.ScreenId)
        {
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
                if (!pauseOnUnknownOutcome)
                    return null;

                logSink?.Add(
                    "Career Training",
                    $"Unknown or unsupported stable screen '{observation.ScreenId}'; paused.",
                    LogEntryKind.Failure);
                return Failure(
                    $"Unsupported stable screen '{observation.ScreenId}'.",
                    observation.ScreenId);
        }
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
        bool careerStartTransitionExpected,
        CancellationToken cancellationToken)
    {
        var candidates = pack.ScreenProfile.Screens
            .Where(screen => !string.Equals(screen.ScreenId, "race_live", StringComparison.OrdinalIgnoreCase))
            .Where(screen => state.CareerStarted
                || state.TurnIndex > 0
                || screen.ScreenId is "career_intro_event"
                    or "career_main"
                    or "career_races_ready")
            // Normal Career has already received its final Start click. Only
            // accept the first screens that can legitimately follow it; the
            // formation templates from the previous page must not win here.
            .Where(screen => !careerStartTransitionExpected
                || screen.ScreenId is "career_intro_event"
                    or "career_main"
                    or "career_races_ready")
            .OrderBy(screen => GetScreenRecognitionPriority(
                screen.ScreenId,
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
                options: PrepareOptions(options),
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

    private HachimiPipelineRunOptions PrepareOptions(HachimiPipelineRunOptions? options)
    {
        options ??= new HachimiPipelineRunOptions();
        options.TaskLogSink ??= _taskLogSink;
        options.SemanticProfile = HachimiTaskLogProfile.Career;
        return options;
    }

    private static string ResolveCapture(UraScenarioPack pack, string relativePath) =>
        UraScenarioResourceResolver.Resolve(pack, relativePath);


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

        var requiredTypes = SupportDeckPresetCatalog.GetRequiredTypes(supportDeckPreset);
        if (requiredTypes is null)
            return;

        if (supportCardIds.Count != 6)
        {
            throw new InvalidOperationException(
                $"Support deck preset '{supportDeckPreset}' requires exactly 6 cards.");
        }

        if (!SupportDeckPresetCatalog.IsValidDeck(
                supportDeckPreset,
                cards.Select(card => card.Type)))
        {
            throw new InvalidOperationException(
                $"Support deck does not match preset '{supportDeckPreset}'.");
        }
    }

    private void ValidateFriendSupportCard(
        CareerTrainingSettings settings,
        bool allowCustomPreset = false)
    {
        var requiredTypes = SupportDeckPresetCatalog.GetRequiredTypes(settings.SupportDeckPreset);
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

        if (!SupportDeckPresetCatalog.IsValidFriendCardType(
                settings.SupportDeckPreset,
                friendCard.Type,
                allowCustomPreset))
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
