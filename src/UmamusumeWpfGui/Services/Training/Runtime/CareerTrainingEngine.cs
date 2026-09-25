using System.Globalization;
using System.Threading;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public sealed class CareerTrainingEngine : ICareerTrainingPipeline
{
    private const string CareerStartTransitionScreenId = "career_start_transition";
    private const int StableScreenRecognitionRetryLimit = 30;

    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly IUmaDatabaseService _umaDatabase;
    private readonly CareerEntryNavigator _entryNavigator;
    private readonly CareerFlowDispatcher _flowDispatcher;
    private readonly CareerScreenObserver _screenObserver;
    private readonly CareerStartupRecoveryDetector _startupRecoveryDetector;
    private readonly NormalCareerStartupFlow _startupFlow;
    private readonly object _runLock = new();
    private CancellationTokenSource? _runCancellation;
    private IHachimiTaskLogSink? _taskLogSink;

    public CareerTrainingEngine(
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
        _flowDispatcher = new CareerFlowDispatcher(visualRuntime, jsonRunner);
        _screenObserver = new CareerScreenObserver(visualRuntime);
        _startupRecoveryDetector = new CareerStartupRecoveryDetector(visualRuntime);
        _startupFlow = new NormalCareerStartupFlow(
            (connection, pack, screenId, actionId, logSink, cancellationToken) =>
                _flowDispatcher.RunScreenActionAsync(
                    connection,
                    pack,
                    screenId,
                    actionId,
                    logSink,
                    cancellationToken),
            (connection, pack, state, careerStartTransitionExpected, cancellationToken) =>
                _screenObserver.ObserveAsync(
                    connection,
                    pack,
                    state,
                    careerStartTransitionExpected,
                    cancellationToken),
            (connection, pack, cancellationToken) =>
                _startupRecoveryDetector.DetectAsync(
                    connection,
                    pack,
                    cancellationToken));
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
            _flowDispatcher.SetTaskLogSink(taskLogSink);
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

        // The running objective is reconstructed from the visible Career UI.
        // Only graded race dates come from the local race calendar.
        var scenario = new UraScenarioModule(
            pack,
            trainee: null,
            careerRaces: null,
            useTraineeObjectives: false,
            raceGradeSchedule: new CareerRaceGradeSchedule(IndependentTrainingCatalog.Load().Races));
        var strategy = UraStrategyRegistry.Create(settings.StrategyId);
        if (!CareerStrategyCatalog.TryGetLineupStrategyUiMapping(
                settings.LineupStrategy,
                out _))
        {
            return Failure(
                $"Normal Career lineup strategy '{settings.LineupStrategy}' is invalid.",
                "career_final_confirmation");
        }
        // Normal Career progress is intentionally run-scoped. The current
        // game screen is the only source of truth after a restart.
        var state = scenario.CreateInitialState();
        state.TraineeId = settings.TraineeId;
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
            settings.ContinueExistingCareer
                ? "The current game screen will determine the Resume entry point."
                : "Starting a new URA career session from the current game screen.");
        logSink?.Add(
            "Career Training",
            settings.ContinueExistingCareer
                ? "Existing Career handling selected: Resume."
                : "Existing Career handling selected: Delete Data.");

        CareerEntryNavigationStep? startupEntryStep = null;

        // Recover the first supported mid-flow page before invoking the shared
        // Home -> Career navigator. Each recoverable page intentionally uses
        // one small, stable recognition template and maps to a setup stage;
        // future pages can be added to CareerStartupRecoveryDetector without changing the
        // entry navigator or the turn engine.
        if (!state.CareerStarted
            && state.NormalSetupStage == NormalCareerSetupStage.EnterCareer)
        {
            CareerObservation? currentCareer = null;
            if (settings.ContinueExistingCareer)
            {
                currentCareer = await _screenObserver.ObserveAsync(
                        connection,
                        pack,
                        state,
                        careerStartTransitionExpected: false,
                        cancellationToken: cancellationToken,
                        careerOnly: true)
                    .ConfigureAwait(false);
            }

            if (currentCareer is { } observedCareer)
            {
                state.CareerStarted = true;
                state.NormalSetupStage = NormalCareerSetupStage.InCareer;
                state.LastScreenId = observedCareer.ScreenId;
                logSink?.Add(
                    "Career Training",
                    $"Current Career screen recognized as {observedCareer.ScreenId}; "
                    + "continuing without reopening Home.");
            }
            else if (await _startupRecoveryDetector.DetectAsync(
                         connection,
                         pack,
                         cancellationToken)
                     .ConfigureAwait(false) is { } detected)
            {
                state.NormalSetupStage = detected.SetupStage;
                state.LastScreenId = detected.ResumeScreenId;
                startupEntryStep = detected.EntryStep;
                logSink?.Add(
                    "Career Training",
                    $"Startup page recognized as {detected.RecognitionScreenId}; "
                    + $"resuming from {detected.ResumeScreenId}.");
            }
        }

        var actionCount = 0;
        var setupObservationRetryCount = 0;
        var restOkProbeCount = 0;
        GrayImage? restOkTemplate = null;
        var recreationOkProbeCount = 0;
        GrayImage? recreationOkTemplate = null;
        string? lastLoggedTurnPosition = null;
        var careerStartTransitionExpected = !state.CareerStarted
            && state.NormalSetupStage == NormalCareerSetupStage.AwaitCareerMain;
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
                LastScreenId = startupEntryStep is not null
                    ? state.LastScreenId
                    : "unknown",
                ActionsCompleted = actionCount,
                ResumeDirectlyToCareer = settings.ContinueExistingCareer,
            };
            var entry = await _entryNavigator.NavigateAsync(
                    connection,
                    pack,
                    settings,
                    entryState,
                    logSink,
                    progressCallback: null,
                    _taskLogSink,
                    cancellationToken)
                .ConfigureAwait(false);
            actionCount = entry.ActionsCompleted;
            state.LastScreenId = entry.LastScreenId;
            if (!entry.Succeeded)
            {
                return Failure(
                    entry.Message,
                    entry.LastScreenId,
                    actionCount);
            }

            if (entry.LastScreenId is "career_main" or "career_races_ready")
            {
                state.CareerStarted = true;
                state.NormalSetupStage = NormalCareerSetupStage.InCareer;
            }
            else
            {
                // The shared navigator stops at Final Confirmation for a new
                // Career. Normal setup continues entirely in this run.
                state.NormalSetupStage = NormalCareerSetupStage.ConfigureMode;
                state.LastScreenId = "career_final_confirmation";
            }
        }

        if (!state.CareerStarted
            && IsPendingNormalSetupStage(state.NormalSetupStage))
        {
            var setupFailure = await _startupFlow.ConfigureAsync(
                    connection,
                    pack,
                    settings,
                    state,
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);
            if (setupFailure is not null)
                return setupFailure with { ActionsCompleted = actionCount };
        }

        while (actionCount < 300)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.AwaitingRecreationConfirmationGone)
            {
                if (!pack.ExecutionDefinition.TryGetTask(
                        "recreation_confirmation_ok", out var recreationConfirmTask)
                    || recreationConfirmTask is null
                    || string.IsNullOrWhiteSpace(recreationConfirmTask.Template))
                {
                    return Failure(
                        "Recreation OK template is missing; automation paused safely.",
                        "recreation_confirmation",
                        actionCount);
                }

                recreationOkTemplate ??= await _visualRuntime.LoadTemplateAsync(
                        recreationConfirmTask.Template,
                        pack.ExecutionDefinition.BaseDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (recreationOkTemplate is null)
                {
                    return Failure(
                        "Recreation OK template could not be loaded; automation paused safely.",
                        "recreation_confirmation",
                        actionCount);
                }

                GrayImage? recreationFrame;
                try
                {
                    recreationFrame = await _visualRuntime.CaptureGrayAsync(
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
                    recreationFrame = null;
                }
                if (recreationFrame is not null
                    && !TemplateMatcher.FindColor(
                        recreationFrame,
                        recreationOkTemplate,
                        recreationConfirmTask.Roi,
                        recreationConfirmTask.TemplateThreshold,
                        pack.ExecutionDefinition.ReferenceWidth,
                        pack.ExecutionDefinition.ReferenceHeight,
                        requireTextContrast: true).Found)
                {
                    state.AwaitingRecreationConfirmationGone = false;
                    recreationOkProbeCount = 0;
                    logSink?.Add(
                        "Career Training",
                        "Recreation OK button disappeared; continuing to the next screen.");
                }
                else
                {
                    recreationOkProbeCount++;
                    if (recreationOkProbeCount >= StableScreenRecognitionRetryLimit)
                    {
                        return Failure(
                            "Recreation OK button did not disappear; automation paused safely.",
                            "recreation_confirmation",
                            actionCount);
                    }

                    await _visualRuntime.DelayAsync(250, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }
            }
            if (state.AwaitingRestConfirmationGone)
            {
                if (!pack.ExecutionDefinition.TryGetTask(
                        "rest_confirmation_rest_confirm", out var confirmTask)
                    || confirmTask is null
                    || string.IsNullOrWhiteSpace(confirmTask.Template))
                {
                    return Failure(
                        "Rest OK template is missing; automation paused safely.",
                        "rest_confirmation",
                        actionCount);
                }

                restOkTemplate ??= await _visualRuntime.LoadTemplateAsync(
                        confirmTask.Template,
                        pack.ExecutionDefinition.BaseDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (restOkTemplate is null)
                {
                    return Failure(
                        "Rest OK template could not be loaded; automation paused safely.",
                        "rest_confirmation",
                        actionCount);
                }

                if (restOkProbeCount == 0)
                {
                    logSink?.Add(
                        "Career Training",
                        "Rest OK was clicked; waiting for its button to disappear.");
                }

                GrayImage? restFrame;
                try
                {
                    restFrame = await _visualRuntime.CaptureGrayAsync(
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
                    restFrame = null;
                }

                if (restFrame is not null
                    && CareerRestConfirmationGate.HasOkDisappeared(
                        restFrame,
                        restOkTemplate,
                        confirmTask,
                        pack.ExecutionDefinition.ReferenceWidth,
                        pack.ExecutionDefinition.ReferenceHeight))
                {
                    state.AwaitingRestConfirmationGone = false;
                    restOkProbeCount = 0;
                    logSink?.Add(
                        "Career Training",
                        "Rest OK button disappeared; continuing to the next screen.");

                    var restEventResult = await _flowDispatcher.TryHandleEventAsync(
                            new CareerFlowContext(
                                connection,
                                pack,
                                settings.PauseOnUnknownOutcome,
                                scenario,
                                strategy,
                                settings.LineupStrategy,
                                state,
                                new CareerObservation("rest_confirmation", 1),
                                logSink,
                                cancellationToken,
                                settings.EventHandling))
                        .ConfigureAwait(false);
                    if (restEventResult is not null)
                        return restEventResult with { ActionsCompleted = actionCount };
                }
                else
                {
                    restOkProbeCount++;
                    if (restOkProbeCount >= StableScreenRecognitionRetryLimit)
                    {
                        return Failure(
                            "Rest OK button did not disappear; automation paused safely.",
                            "rest_confirmation",
                            actionCount);
                    }

                    await _visualRuntime.DelayAsync(250, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }
            }

            careerStartTransitionExpected = !state.CareerStarted
                && state.NormalSetupStage == NormalCareerSetupStage.AwaitCareerMain;
            var observation = await _screenObserver.ObserveAsync(
                    connection,
                    pack,
                    state,
                    careerStartTransitionExpected,
                    cancellationToken)
                .ConfigureAwait(false);
            if (observation is null)
            {
                var setupRetryLimit = careerStartTransitionExpected
                    ? 40
                    : StableScreenRecognitionRetryLimit;
                if (setupObservationRetryCount < setupRetryLimit)
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
            if (observation.Kind is CareerScreenKind.Unknown)
            {
                return Failure(
                    $"Recognized unsupported Career screen '{observation.ScreenId}'; automation paused safely.",
                    observation.ScreenId,
                    actionCount);
            }

            state.LastScreenId = observation.ScreenId;
            scenario.ObserveScreen(
                state,
                observation.ScreenId,
                observation.Score,
                observation.EnergyPercent,
                observation.EnergyConfidence,
                observation.TurnPositionText,
                observation.TurnsToGoal,
                observation.GoalText,
                observation.FansToGoal,
                observation.MoodText);
            if (observation.ScreenId == "career_main"
                && !string.IsNullOrWhiteSpace(observation.GoalText)
                && state.ObservedGoalKind == CareerGoalTextParser.GradeRaceCount)
            {
                logSink?.Add(
                    "Career Training",
                    $"Grade race goal: grade={state.TargetRaceGrade}, turn={state.TurnIndex}, "
                    + $"remaining={state.GradeRaceTimesLeft?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, "
                    + $"racePending={state.HasPendingRace}.");
            }
            if (state.CareerStarted)
                state.NormalSetupStage = NormalCareerSetupStage.InCareer;
            if (observation.ScreenId.Equals("career_main", StringComparison.OrdinalIgnoreCase)
                && state.TurnPositionLabel is { Length: > 0 } turnPosition
                && !string.Equals(
                    turnPosition,
                    lastLoggedTurnPosition,
                    StringComparison.OrdinalIgnoreCase))
            {
                _taskLogSink?.Add(
                    "Turn",
                    $"Current turn: {turnPosition}"
                        + (state.CalendarStage == UraCalendarStage.SummerCamp
                            ? " [Summer Camp]"
                            : string.Empty),
                    HachimiTaskLogEventKind.Detection);
                lastLoggedTurnPosition = turnPosition;
            }
            if (observation.ScreenId.Equals("career_main", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(observation.GoalText))
            {
                _taskLogSink?.Add(
                    "Goal",
                    $"Observed goal: {observation.GoalText}"
                        + (observation.TurnsToGoal is int turns
                            ? $" ({turns} turn(s) left)"
                            : string.Empty),
                    HachimiTaskLogEventKind.Detection);
            }
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

                return new CareerTrainingResult(
                    true,
                    "URA career completed and returned to Home.",
                    actionCount,
                    observation.ScreenId);
            }

            var terminal = await _flowDispatcher.DispatchAsync(
                    connection,
                    pack,
                    settings.PauseOnUnknownOutcome,
                    scenario,
                    strategy,
                    settings.LineupStrategy,
                    state,
                    observation,
                    logSink,
                    settings.EventHandling,
                    cancellationToken)
                .ConfigureAwait(false);
            if (terminal is not null)
            {
                return terminal with { ActionsCompleted = actionCount };
            }

            actionCount++;
        }

        return Failure(
            "Career training exceeded the safety action limit and was paused.",
            state.LastScreenId,
            actionCount);
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

    internal static bool IsRuntimeCareerScreen(string screenId) =>
        CareerScreenObserver.IsRuntimeCareerScreen(screenId);

    private static bool IsImportantCareerScreen(string screenId) => screenId is
        "career_main" or "career_race_result" or "career_event";

    internal static bool IsCareerStartTransitionExpected(
        UraCareerSessionState state) =>
        !state.CareerStarted
        && state.NormalSetupStage == NormalCareerSetupStage.AwaitCareerMain;

    private static string FriendlyCareerScreen(string screenId) => screenId switch
    {
        "career_main" => "the Career turn screen",
        "career_race_result" => "the race result",
        "career_event" => "the event choice",
        _ => screenId,
    };

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
}
