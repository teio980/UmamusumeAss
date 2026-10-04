using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public sealed class CareerTrainingEngine : ICareerTrainingPipeline
{
    private const string CareerStartTransitionScreenId = "career_start_transition";
    private const int StableScreenRecognitionRetryLimit = 30;
    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly IUmaDatabaseService _umaDatabase;
    private readonly CareerEntryNavigator _entryNavigator;
    private readonly CareerFlowDispatcher _flowDispatcher;
    private readonly CareerScreenObserver _screenObserver;
    private readonly CareerStartupRecoveryDetector _startupRecoveryDetector;
    private readonly NormalCareerStartupFlow _startupFlow;
    private readonly DateChangedDialogRecovery? _dateChangedRecovery;
    private readonly Func<LastVerifiedConnection, int, NormalCareerSkillCache> _skillCacheFactory;
    private readonly object _runLock = new();
    private CancellationTokenSource? _runCancellation;
    private IHachimiTaskLogSink? _taskLogSink;

    public CareerTrainingEngine(
        IVisualPipelineRuntime visualRuntime,
        IUmaDatabaseService umaDatabase,
        CareerEntryNavigator entryNavigator,
        HachimiJsonPipelineRunner jsonRunner,
        DateChangedDialogRecovery? dateChangedRecovery = null)
        : this(visualRuntime, umaDatabase, entryNavigator, jsonRunner, dateChangedRecovery,
            (connection, traineeId) => new NormalCareerSkillCache(connection, traineeId))
    {
    }

    internal CareerTrainingEngine(
        IVisualPipelineRuntime visualRuntime,
        IUmaDatabaseService umaDatabase,
        CareerEntryNavigator entryNavigator,
        HachimiJsonPipelineRunner jsonRunner,
        DateChangedDialogRecovery? dateChangedRecovery,
        Func<LastVerifiedConnection, int, NormalCareerSkillCache> skillCacheFactory)
    {
        ArgumentNullException.ThrowIfNull(visualRuntime);
        ArgumentNullException.ThrowIfNull(umaDatabase);
        ArgumentNullException.ThrowIfNull(entryNavigator);
        ArgumentNullException.ThrowIfNull(jsonRunner);
        _visualRuntime = visualRuntime;
        _umaDatabase = umaDatabase;
        _dateChangedRecovery = dateChangedRecovery;
        _skillCacheFactory = skillCacheFactory ?? throw new ArgumentNullException(nameof(skillCacheFactory));
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
            using var scope = _dateChangedRecovery is not null && GameAutomationScope.Current is null
                ? new GameAutomationScope(logSink, taskLogSink, connection) : null;
            var effectiveSettings = settings;
            while (true)
            {
                try
                {
                    return await RunCoreAsync(connection, effectiveSettings, logSink, linked.Token)
                        .ConfigureAwait(false);
                }
                catch (DateChangedInterruptionException) when (_dateChangedRecovery is not null)
                {
                    await _dateChangedRecovery.RecoverAsync(connection, linked.Token).ConfigureAwait(false);
                    effectiveSettings = effectiveSettings with { ContinueExistingCareer = true };
                }
            }
        }
        catch (DateChangedRecoveryException exception)
        {
            logSink?.Add("Career Training", exception.Message, LogEntryKind.Failure);
            return Failure(exception.Message, "date_changed_recovery");
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
        var assembly = typeof(CareerTrainingEngine).Assembly;
        logSink?.Add(
            "Career Training",
            $"Runtime source: {assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration}; "
            + $"executable='{Environment.ProcessPath}'; build={assembly.ManifestModule.ModuleVersionId}; "
            + $"manifest='{pack.ManifestPath}'; "
            + $"Goal Incomplete recognition={(pack.ScreenProfile.Find("goal_incomplete") is null ? "missing" : "loaded")}.");

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
        // The visible game screen reconstructs Career progress after a restart.
        // Only skills confirmed as obtained need a small cache between runs.
        var state = scenario.CreateInitialState();
        var session = new CareerSessionState<UraCareerSessionState>
        {
            Runtime = state.Runtime,
            Scenario = state,
        };
        state.TraineeId = settings.TraineeId;
        // Normal Career does not persist its turn history. A resumed career
        // must not assume it has a clean race streak before a non-race turn.
        CareerRaceStreakPolicy.InitializeForRun(
            state, settings.ContinueExistingCareer);
        var skillCache = _skillCacheFactory(connection, settings.TraineeId);
        if (settings.ContinueExistingCareer)
        {
            state.NormalLearnedSkillIds.AddRange(
                await skillCache.LoadAsync(cancellationToken).ConfigureAwait(false));
            if (state.NormalLearnedSkillIds.Count > 0)
                logSink?.Add("Career Training",
                    $"Loaded {state.NormalLearnedSkillIds.Count} learned skills from this Career's cache.");
        }
        else
        {
            await skillCache.ClearAsync().ConfigureAwait(false);
        }

        async Task RememberNormalSkillAsync(int skillId)
        {
            try
            {
                await skillCache.SaveAsync(state.NormalLearnedSkillIds).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logSink?.Add("Career Training",
                    $"Could not save learned-skill cache after skill {skillId}: {ex.Message}",
                    LogEntryKind.Failure);
            }
        }
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
        CareerObservation? pendingResumeObservation = null;
        var resumeRecoveryPending = false;

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
                resumeRecoveryPending = true;
                state.CareerStarted = true;
                state.NormalSetupStage = NormalCareerSetupStage.InCareer;
                state.LastScreenId = observedCareer.ScreenId;
                // Goal pages and inheritance GO can be tied to a previous
                // action during a live run. On resume, dispatch the already-
                // verified screen once from the current UI before observing
                // the next screen.
                if (observedCareer.ScreenId is "inheritance_event"
                    or "goal_incomplete"
                    or "goal_objective_complete"
                    or "goal_update"
                    or "goal_complete")
                {
                    pendingResumeObservation = observedCareer;
                }
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
        var completionObserver = new CareerActionCompletionObserver(_visualRuntime, StableScreenRecognitionRetryLimit);
        var homeTabSelectionIssued = false;
        var homeTabSelectionRetryCount = 0;
        string? lastLoggedCareerStatus = null;
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

            if (CareerScreenObserver.IsRuntimeCareerScreen(entry.LastScreenId, pack.ScreenProfile))
            {
                state.CareerStarted = true;
                state.NormalSetupStage = NormalCareerSetupStage.InCareer;
                if (entry.ResumeObservation is { } resumedCareer)
                {
                    resumeRecoveryPending = true;
                    // Consume a verified overlay once. Reobserve Main so a
                    // delayed goal banner can win before a turn is selected.
                    if (resumedCareer.ScreenId is not "career_main"
                        and not "career_races_ready")
                    {
                        pendingResumeObservation = resumedCareer;
                    }
                    logSink?.Add(
                        "Career Training",
                        $"Resumed Career screen recognized as {resumedCareer.ScreenId}.");
                }
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
            state.NormalLearnedSkillIds.Clear();
            if (settings.ContinueExistingCareer)
                await skillCache.ClearAsync().ConfigureAwait(false);
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

        var bindings = new CareerRuntimeLoopBindings(
            IsStartTransitionExpected: () => !state.CareerStarted
                && state.NormalSetupStage == NormalCareerSetupStage.AwaitCareerMain,
            BeforeObservationAsync: async loopCancellation =>
            {
                var completion = await completionObserver.ObserveAsync(
                        connection, pack, state, logSink, loopCancellation)
                    .ConfigureAwait(false);
                if (completion is { Succeeded: false })
                    return Failure(completion.Message, completion.LastScreenId, actionCount);
                if (completion?.Status == CareerActionStatus.AwaitingConfirmation)
                {
                    return new CareerRuntimeStep(AwaitingTransition: true);
                }
                if (completion?.LastScreenId == "rest_confirmation")
                {
                    var eventResult = await _flowDispatcher.TryHandleEventAsync(
                            new CareerFlowContext(connection, pack, settings.PauseOnUnknownOutcome,
                                scenario, strategy, settings.LineupStrategy, state,
                                new CareerObservation("rest_confirmation", 1), logSink,
                                loopCancellation, settings.EventHandling))
                        .ConfigureAwait(false);
                    if (eventResult is not null)
                        return eventResult;
                }

                return new CareerRuntimeStep();
            },
            ObserveAsync: (startExpected, recoveryPending, loopCancellation) =>
                _screenObserver.ObserveAsync(connection, pack, state, startExpected,
                    loopCancellation, resumeRecovery: recoveryPending),
            HandleObservationAsync: async (observation, loopCancellation) =>
            {
                var performedActions = 0;
                if (observation.ScreenId == "home")
                {
                    await skillCache.ClearAsync().ConfigureAwait(false);
                    return new CareerTrainingResult(
                        true,
                        "URA career completed and returned to Home.",
                        actionCount,
                        observation.ScreenId);
                }

                if (observation.ScreenId == "home_unselected")
                {
                    if (!homeTabSelectionIssued)
                    {
                        logSink?.Add("Career Training", "Home tab is visible but not selected; selecting Home.");
                        var selectHome = await _flowDispatcher.RunScreenActionAsync(
                                connection,
                                pack,
                                "home_unselected",
                                "home.select",
                                logSink,
                                loopCancellation)
                            .ConfigureAwait(false);
                        if (selectHome is not null)
                        {
                            // The unselected template may disappear between observation
                            // and the action probe because Home became selected.
                            logSink?.Add(
                                "Career Training",
                                "Home tab tap was not confirmed; checking whether Home became selected.");
                        }
                        else
                        {
                            performedActions++;
                        }

                        homeTabSelectionIssued = true;
                        state.LastScreenId = "home_unselected";
                    }
                    else if (++homeTabSelectionRetryCount >= StableScreenRecognitionRetryLimit)
                    {
                        return Failure(
                            "Home tab did not become selected after tapping it; automation paused safely.",
                            observation.ScreenId,
                            actionCount);
                    }

                    return new CareerRuntimeStep(ActionsCompleted: performedActions, AwaitingTransition: true);
                }

                if (observation.Kind is CareerScreenKind.Unknown)
                {
                    return Failure(
                        $"Recognized unsupported Career screen '{observation.ScreenId}'; automation paused safely.",
                        observation.ScreenId,
                        actionCount);
                }

                state.LastScreenId = observation.ScreenId;
                ObserveScenario(scenario, session, observation);
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
                if (observation.ScreenId.Equals("career_main", StringComparison.OrdinalIgnoreCase))
                {
                    var careerStatus = FormatCareerStatus(observation, state);
                    if (!string.Equals(careerStatus, lastLoggedCareerStatus, StringComparison.Ordinal))
                    {
                        _taskLogSink?.Add(
                            "Career status",
                            careerStatus,
                            HachimiTaskLogEventKind.Detection);
                        lastLoggedCareerStatus = careerStatus;
                    }
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
                        loopCancellation,
                        settings.RetryFailedRaceWithAlarmClock,
                        settings.EffectiveNormalSkillIds,
                        RememberNormalSkillAsync)
                    .ConfigureAwait(false);
                if (terminal is not null)
                {
                    return terminal;
                }

                // The first resumed action now supplies normal flow history.
                // Keep recovery bounded to this handoff, not the whole Career.
                return new CareerRuntimeStep(ActionsCompleted: 1);
            },
            SaveRecognitionFailureAsync: loopCancellation => SaveRecognitionFailureAsync(
                connection, pack, state, !state.CareerStarted
                    && state.NormalSetupStage == NormalCareerSetupStage.AwaitCareerMain,
                logSink, loopCancellation),
            DelayAsync: (milliseconds, loopCancellation) =>
                _visualRuntime.DelayAsync(milliseconds, loopCancellation));
        return await CareerRuntimeLoop.RunAsync(state.Runtime, bindings, actionCount,
                pendingResumeObservation, resumeRecoveryPending, cancellationToken,
                recognitionRetryLimit: StableScreenRecognitionRetryLimit)
            .ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859",
        Justification = "This boundary accepts scenario modules through the shared runtime contract.")]
    private static void ObserveScenario<TScenarioState>(
        ICareerScenarioModule<TScenarioState> module,
        CareerSessionState<TScenarioState> session,
        CareerObservation observation) => module.Observe(session, observation);

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
        "career_race_result" or "career_event";

    private static string FormatCareerStatus(
        CareerObservation observation,
        UraCareerSessionState state)
    {
        var date = state.TurnIndexSource == UraStateSource.Observed
            ? state.TurnPositionLabel
            : null;
        if (date is not null && state.CalendarStage == UraCalendarStage.SummerCamp)
            date += " [Summer Camp]";
        var mood = state.Mood.Value?.ToString();
        var energy = observation.EnergyPercent is int percent
            ? $"{percent.ToString(CultureInfo.InvariantCulture)}%"
            : "Unknown";
        return string.Join("\n",
            $"Date: {Display(date)}",
            $"Turns left: {observation.TurnsToGoal?.ToString(CultureInfo.InvariantCulture) ?? "Unknown"}",
            $"Goal: {Display(observation.GoalText)}",
            $"Mood: {Display(mood)}",
            $"Energy: {energy}");
    }

    private static string Display(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();

    internal static bool IsCareerStartTransitionExpected(
        UraCareerSessionState state) =>
        !state.CareerStarted
        && state.NormalSetupStage == NormalCareerSetupStage.AwaitCareerMain;

    private static string FriendlyCareerScreen(string screenId) => screenId switch
    {
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

    private async Task SaveRecognitionFailureAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        UraCareerSessionState state,
        bool careerStartTransitionExpected,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(
            HachimiResourcePaths.GetDebugDirectory("career"),
            "unrecognized-" + DateTimeOffset.UtcNow.ToString(
                "yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(directory);
            var assembly = typeof(CareerTrainingEngine).Assembly;
            var diagnostics = new
            {
                Configuration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
                Executable = Environment.ProcessPath,
                AssemblyBuild = assembly.ManifestModule.ModuleVersionId,
                AppBase = AppContext.BaseDirectory,
                ResourceBase = ResourcePathRuntime.BaseDirectory,
                pack.ManifestPath,
                ScreenProfiles = pack.Manifest.Screens.Select(path => Path.GetFullPath(Path.Combine(pack.RootDirectory, path))),
                Executions = pack.Manifest.Execution.Select(path => Path.GetFullPath(Path.Combine(pack.RootDirectory, path))),
                ResourceCatalog = Path.GetFullPath(Path.Combine(pack.RootDirectory, pack.Manifest.ResourceCatalog)),
                state.LastScreenId,
                LastAction = state.LastAction.ToString(),
                state.CareerStarted,
                state.TurnIndex,
                NormalSetupStage = state.NormalSetupStage.ToString(),
                state.RaceReplayFlowCompleted,
                state.GoalCompletionProbeArmed,
                state.RaceRetryDeclined,
                state.InheritanceEventPending,
                CareerStartTransitionExpected = careerStartTransitionExpected,
                ReturningHome = CareerScreenObserver.IsReturningHome(state),
                GoalIncompleteRecognition = pack.ScreenProfile.Find("goal_incomplete")?.Recognition,
                GoalIncompleteEligible = CareerScreenObserver.IsEligibleForCareerPhase(
                    "goal_incomplete", state, pack.ScreenProfile),
                CandidateScreenIds = _screenObserver.LastCandidateScreenIds,
                CandidateSources = _screenObserver.LastCandidateScreenIds.ToDictionary(
                    screenId => screenId,
                    screenId => pack.ScreenProfile.Find(screenId)?.SourceFile),
                FrameScreenIds = _screenObserver.LastFrameScreenIds,
                CaptureErrors = _screenObserver.LastCaptureErrors,
                Frames = _screenObserver.LastFrames.Select(frame => new { frame.Width, frame.Height }),
            };
            var path = Path.Combine(directory, "state.json");
            await File.WriteAllTextAsync(
                    path, JsonSerializer.Serialize(diagnostics, DiagnosticJsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);
            logSink?.Add("Career Training",
                $"Recognition failure after '{state.LastScreenId}'; diagnostics: '{path}'.");
            for (var index = 0; index < _screenObserver.LastFrames.Count; index++)
            {
                var frame = _screenObserver.LastFrames[index];
                var rgba = frame.RgbaPixels;
                if (rgba is null)
                {
                    rgba = new byte[checked(frame.Width * frame.Height * 4)];
                    for (var pixel = 0; pixel < frame.Pixels.Length; pixel++)
                    {
                        rgba[pixel * 4] = frame.Pixels[pixel];
                        rgba[pixel * 4 + 1] = frame.Pixels[pixel];
                        rgba[pixel * 4 + 2] = frame.Pixels[pixel];
                        rgba[pixel * 4 + 3] = 255;
                    }
                }
                GrayImageCodec.SaveScreenshot(
                    new AdbScreenshotResult(AdbScreenshotMethod.Raw, [], TimeSpan.Zero,
                        new AdbRawScreenshot(frame.Width, frame.Height, rgba)),
                    Path.Combine(directory, $"sample-{index + 1}.png"));
            }
            if (_screenObserver.LastFrames.Count == 0)
            {
                await _visualRuntime.SaveScreenshotAsync(
                        connection, directory, "screen", cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            and not DateChangedInterruptionException and not DateChangedRecoveryException)
        {
            // Failure evidence is best-effort and must not replace the original
            // recognition failure if a screenshot or file cannot be saved.
            logSink?.Add("Career Training",
                $"Could not save recognition failure evidence: {exception.Message}");
        }
    }

    private static CareerTrainingResult Failure(
        string message,
        string lastScreenId,
        int actionsCompleted = 0) =>
        new(false, message, actionsCompleted, lastScreenId);
}
