using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public sealed class CareerFlowDispatcher : ICareerFlowActionRunner
{
    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly CareerJsonActionExecutor _actionExecutor;
    private readonly UraTrainingSelectionHeightDetector _trainingSelectionDetector;
    private readonly UraSmartTrainingSelectionFlow _smartTrainingSelectionFlow;
    private readonly CareerTurnFlow _turnFlow;
    private readonly CareerBreakFlow _breakFlow;
    private readonly CareerRaceFlow _raceFlow;
    private readonly CareerRaceRunnerCheckpointHandler _raceRunnerHandler;
    private readonly CareerSettlementFlow _settlementFlow;
    private readonly CareerClawMachineFlow _clawMachineFlow;
    private readonly ICareerEventHandler _eventHandler;
    private IHachimiTaskLogSink? _taskLogSink;

    public CareerFlowDispatcher(
        IVisualPipelineRuntime visualRuntime,
        HachimiJsonPipelineRunner jsonRunner)
        : this(visualRuntime, jsonRunner, eventHandler: null)
    {
    }

    internal CareerFlowDispatcher(
        IVisualPipelineRuntime visualRuntime,
        HachimiJsonPipelineRunner jsonRunner,
        ICareerEventHandler? eventHandler)
    {
        ArgumentNullException.ThrowIfNull(visualRuntime);
        _visualRuntime = visualRuntime;
        _actionExecutor = new CareerJsonActionExecutor(jsonRunner);
        _eventHandler = eventHandler ?? new CareerEventHandler(visualRuntime, this);
        _trainingSelectionDetector = new UraTrainingSelectionHeightDetector(visualRuntime);
        _smartTrainingSelectionFlow = new UraSmartTrainingSelectionFlow(
            visualRuntime, this, () => _taskLogSink);
        _turnFlow = new CareerTurnFlow(this);
        _breakFlow = new CareerBreakFlow(visualRuntime);
        _raceFlow = new CareerRaceFlow(visualRuntime, this);
        _raceRunnerHandler = new CareerRaceRunnerCheckpointHandler(this);
        _settlementFlow = new CareerSettlementFlow(this);
        _clawMachineFlow = new CareerClawMachineFlow(visualRuntime);
    }

    internal void SetTaskLogSink(IHachimiTaskLogSink? taskLogSink)
    {
        _taskLogSink = taskLogSink;
        _actionExecutor.SetTaskLogSink(taskLogSink);
    }

    internal Task<CareerTrainingResult?> TryHandleEventAsync(
        CareerFlowContext context) =>
        _eventHandler.TryRecognizeAndHandleAsync(context);

    internal Task<CareerTrainingResult?> RunScreenActionAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string screenId,
        string actionId,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken,
        HachimiPipelineRunOptions? options = null) =>
        RunScreenActionCoreAsync(
            connection,
            pack,
            screenId,
            actionId,
            logSink,
            options,
            cancellationToken);

    internal async Task<CareerTrainingResult?> RunConfirmedSmartTrainingSelectionAsync(
        CareerFlowContext context,
        string trainingType,
        UraTrainingSelectionHeightResult? verifiedSelection = null)
    {
        if (context.State.TrainingClickIssuedType is not null)
            return null;
        if (context.State.TrainingTurnCommitPending || context.State.TrainingTurnCommitType is not null)
            return CareerRuntimeResults.Failure(
                "A previous smart training confirmation has an unknown result; no second tap was sent.",
                "training_selection");
        if (!UraTrainingTypeCatalog.TryGetSemanticAction(
                trainingType, out var actionId, out var normalized))
        {
            return CareerRuntimeResults.Failure(
                $"Smart strategy selected unsupported training type '{trainingType}'.",
                "training_selection");
        }

        var tap = verifiedSelection is null ? null
            : UraSmartTrainingConfirmationTap.Create(context.Pack, context.Connection, verifiedSelection, normalized);
        if (verifiedSelection is not null && !UraSmartTrainingConfirmationTap.IsWinnerVerified(verifiedSelection, normalized))
            return CareerRuntimeResults.Failure(
                "The fresh smart training confirmation frame has no verified winner.",
                "training_selection");
        context.State.PendingTrainingType = normalized;
        context.State.LastAction = UraPlannedAction.Training;
        // Arm this persisted guard before the tap. If cancellation or a
        // dropped result happens after input was sent, a resumed picker must
        // pause as an unknown outcome instead of sending a second tap.
        context.State.TrainingTurnCommitPending = true;
        context.State.TrainingTurnCommitType = normalized;
        context.State.TrainingTurnCommitTurnIndex = context.State.TurnIndex;
        if (context.State.TraineeId is not { } traineeId)
        {
            return CareerRuntimeResults.Failure(
                "Smart training confirmation has no trainee identity for its safety guard.",
                "training_selection");
        }

        try
        {
            await UraSmartTrainingConfirmationStore.SaveAsync(
                    context.Connection,
                    new UraSmartTrainingPendingConfirmation(
                        traineeId,
                        normalized,
                        context.State.TrainingTurnCommitTurnIndex ?? context.State.TurnIndex,
                        DateTimeOffset.UtcNow),
                    context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CareerRuntimeResults.Failure(
                "Smart training confirmation was cancelled before its safety guard was persisted.",
                "training_selection");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return CareerRuntimeResults.Failure(
                $"Smart training confirmation was blocked because its safety guard could not be persisted: {exception.Message}",
                "training_selection");
        }

        // The guard is durable before the first input is sent. If the tap or
        // its result is interrupted, the next run fails closed.
        var clickTask = context.Pack.ExecutionDefinition.GetTask($"training_selection_{normalized}_raised_click");
        if (tap is not null && clickTask.Algorithm == "MatchTemplateColor" && clickTask.Action == "ClickSelf"
            && clickTask.PreDelay == 0 && clickTask.PostDelay == 0)
        {
            // The smart flow just checked all five logos on a fresh frame. Reuse
            // that match and the original JSON offset instead of capturing and
            // matching the same picker three more times. Runtime input guard stays active.
            await _visualRuntime.TapMatchAsync(context.Connection, tap,
                $"training_selection_{normalized}_raised_click", context.CancellationToken).ConfigureAwait(false);
            context.State.TrainingClickIssuedType = normalized;
            context.State.TrainingClickTargetGone = false;
            CareerRaceStreakPolicy.BeginTurnAction(context.State, UraPlannedAction.Training);
            context.LogSink?.Add("URA Strategy", $"Smart training confirmation sent once: {normalized}; reused fresh verified match.");
            return null;
        }
        return await RunAsync(context, "training_selection", actionId)
            .ConfigureAwait(false);
    }

    internal async Task<CareerTrainingResult?> RunSmartTrainingFallbackBackAsync(
        CareerFlowContext context)
    {
        var result = await RunAsync(context, "training_selection", "training.back")
            .ConfigureAwait(false);
        if (result is not null)
            return result;

        var main = context.Pack.ScreenProfile.Find("career_main");
        if (main is null || main.Templates.Count == 0
            || string.IsNullOrWhiteSpace(main.Recognition.RequiredTemplate))
            return CareerRuntimeResults.Failure(
                "Smart training fallback returned without a career-main verification template.",
                "training_selection");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await _visualRuntime.DelayAsync(180, context.CancellationToken)
                .ConfigureAwait(false);
            var frame = await _visualRuntime.CaptureGrayAsync(
                    context.Connection, context.CancellationToken)
                .ConfigureAwait(false);
            if (frame is not null
                && await CareerMainScreenDetector.MatchAsync(frame, context.Pack,
                        (path, token) => _visualRuntime.LoadTemplateAsync(path, string.Empty, token),
                        context.CancellationToken)
                    .ConfigureAwait(false) is not null)
            {
                // The next career_main observation lets the strategy consume
                // its SmartTrainingFallbackPending flag and choose Rest.
                return null;
            }
        }

        return CareerRuntimeResults.Failure(
            "Smart training fallback tapped Back but could not verify the career main screen; scanning is paused.",
            "training_selection");
    }

    internal async Task<CareerTrainingResult> RunTrainingPreviewSelectionAsync(
        CareerFlowContext context,
        string trainingType)
    {
        if (!UraTrainingTypeCatalog.TryNormalize(trainingType, out var normalized))
        {
            return CareerRuntimeResults.Failure(
                $"Smart strategy selected unsupported preview type '{trainingType}'.",
                "training_selection");
        }

        var taskName = $"training_selection_{normalized}_preview";
        var result = await _actionExecutor.RunTaskAsync(
                context.Connection,
                context.Pack,
                taskName,
                context.LogSink,
                context.CancellationToken,
                new HachimiPipelineRunOptions
                {
                    TaskLogSink = _taskLogSink,
                    SemanticProfile = HachimiTaskLogProfile.Career,
                })
            .ConfigureAwait(false);
        return result.Succeeded
            ? new CareerTrainingResult(true, $"Preview selected {normalized}.", 0, "training_selection")
            : CareerRuntimeResults.Failure(
                $"Could not execute preview task '{taskName}': {result.Message}",
                "training_selection");
    }

    internal async Task<CareerTrainingResult?> DispatchAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        bool pauseOnUnknownOutcome,
        UraScenarioModule scenario,
        ICareerTrainingStrategy<UraCareerSessionState> strategy,
        string lineupStrategy,
        UraCareerSessionState state,
        CareerObservation observation,
        IGrassTaskLogSink? logSink,
        string eventHandling,
        CancellationToken cancellationToken,
        bool retryFailedRaceWithAlarmClock = false,
        IReadOnlyList<int>? normalSkillIds = null,
        Func<int, Task>? rememberNormalSkillAsync = null)
    {
        if (state.RaceRetryDeclined
            && observation.ScreenId is ("career_main" or "training_selection"
                or "race_runner" or "race_day" or "race_list"
                or "race_list_empty"
                or "race_streak_warning"
                or "race_details" or "race_attributes" or "race_playback"
                or "race_playback_start"))
        {
            return CareerRuntimeResults.Failure(
                $"Race retry was declined, but '{observation.ScreenId}' appeared instead of settlement; automation paused safely.",
                observation.ScreenId);
        }

        if (observation.ScreenId != "race_retry_dialog")
        {
            state.RaceRetryDialogActionIssued = false;
            state.RaceRetryDialogWaitCount = 0;
        }

        if (observation.ScreenId != "race_streak_warning")
            state.RaceStreakWarningActionIssued = false;

        // The guard belongs to one visit of the runner checkpoint. Any
        // transition away from that page opens the same reusable step for a
        // later race instead of carrying the previous race's configuration.
        if (!string.Equals(
                observation.ScreenId,
                CareerRaceRunnerCheckpointHandler.ScreenId,
                StringComparison.OrdinalIgnoreCase))
        {
            state.RaceStrategyConfigured = false;
        }

        var context = new CareerFlowContext(
            connection,
            pack,
            pauseOnUnknownOutcome,
            scenario,
            strategy,
            lineupStrategy,
            state,
            observation,
            logSink,
            cancellationToken,
            eventHandling,
            retryFailedRaceWithAlarmClock,
            normalSkillIds,
            rememberNormalSkillAsync);

        var kind = CareerScreenClassification.Classify(observation.ScreenId, pack.ScreenProfile);
        var isSupportedScreen = kind != CareerScreenKind.Unknown;
        var result = observation.ScreenId switch
        {
            "inheritance_event" => await _turnFlow.HandleAsync(context).ConfigureAwait(false),
            "claw_machine" => await _clawMachineFlow.HandleAsync(context).ConfigureAwait(false),
            "claw_machine_result" => await _clawMachineFlow.HandleResultAsync(context).ConfigureAwait(false),
            "race_runner" => await _raceRunnerHandler.HandleAsync(context).ConfigureAwait(false),
            "training_selection" when strategy is UraSmartTrainingStrategy smartStrategy =>
                await _smartTrainingSelectionFlow.HandleAsync(context, smartStrategy).ConfigureAwait(false),
            _ => kind switch
            {
                CareerScreenKind.Main or CareerScreenKind.Turn =>
                    await _turnFlow.HandleAsync(context).ConfigureAwait(false),
                CareerScreenKind.Race => await _raceFlow.HandleAsync(context).ConfigureAwait(false),
                CareerScreenKind.Event => await _eventHandler.TryRecognizeAndHandleAsync(context).ConfigureAwait(false),
                CareerScreenKind.Settlement => await _settlementFlow.HandleAsync(context).ConfigureAwait(false),
                _ => null,
            },
        };

        if (result is not null || isSupportedScreen || !pauseOnUnknownOutcome)
            return result;

        logSink?.Add(
            "Career Training",
            $"Unknown or unsupported stable screen '{observation.ScreenId}'; paused.",
            LogEntryKind.Failure);
        return CareerRuntimeResults.Failure(
            $"Unsupported stable screen '{observation.ScreenId}'.",
            observation.ScreenId);
    }

    internal Task<CareerTrainingResult?> RunAsync(
        CareerFlowContext context,
        string screenId,
        string actionId,
        HachimiPipelineRunOptions? options = null) =>
        (screenId == "career_main" && actionId is "action.rest" or "action.summer_rest" or "action.recreation")
            || (screenId is "rest_confirmation" or "summer_rest_confirmation" && actionId == "rest.confirm")
            || (screenId == "recreation_selection" && actionId == "recreation.trainee")
            || (screenId == "recreation_confirmation" && actionId == "recreation.confirm")
            ? _breakFlow.RunAsync(context, screenId, actionId)
            : RunScreenActionCoreAsync(
            context.Connection,
            context.Pack,
            screenId,
            actionId,
            context.LogSink,
            options,
            context.CancellationToken,
            context.State);

    Task<CareerTrainingResult?> ICareerFlowActionRunner.RunAsync(
        CareerFlowContext context,
        string screenId,
        string actionId,
        HachimiPipelineRunOptions? options) =>
        RunAsync(context, screenId, actionId, options);

    private async Task<CareerTrainingResult?> RunScreenActionCoreAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        string screenId,
        string actionId,
        IGrassTaskLogSink? logSink,
        HachimiPipelineRunOptions? options,
        CancellationToken cancellationToken,
        UraCareerSessionState? state = null)
    {
        var screen = pack.ScreenProfile.Find(screenId);
        if (screen is null)
        {
            return CareerRuntimeResults.Failure(
                $"Screen '{screenId}' is missing from the Career screen fragments.",
                screenId);
        }

        var action = screen.FindAction(actionId);
        if (action is null || string.IsNullOrWhiteSpace(action.Task))
        {
            return CareerRuntimeResults.Failure(
                $"Screen action '{screenId}.{actionId}' is missing from the Career screen fragments.",
                screenId);
        }

        options ??= new HachimiPipelineRunOptions();
        options.TaskLogSink ??= _taskLogSink;
        options.SemanticProfile = HachimiTaskLogProfile.Career;
        var entryTask = action.Task;
        var isTrainingEntry = string.Equals(screenId, "career_main", StringComparison.OrdinalIgnoreCase)
            && string.Equals(actionId, "action.training", StringComparison.OrdinalIgnoreCase);
        if (state is not null && isTrainingEntry)
            state.TrainingSelectionEntryConfirmed = false;

        if (string.Equals(screenId, "training_selection", StringComparison.OrdinalIgnoreCase)
            && actionId.StartsWith("training.", StringComparison.OrdinalIgnoreCase)
            && UraTrainingTypeCatalog.TryNormalize(actionId["training.".Length..],
                out var targetTrainingType))
        {
            if (state?.TrainingClickIssuedType is { } clickedType)
            {
                if (!state.TrainingClickTargetGone)
                {
                    var clickedItem = await _trainingSelectionDetector.FindTrainingTypeAsync(
                            connection,
                            pack,
                            clickedType,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (clickedItem is { Found: false })
                    {
                        state.TrainingClickTargetGone = true;
                        logSink?.Add(
                            "Career Training",
                            $"Clicked training item '{clickedType}' is no longer visible; observing the next screen.");
                    }
                }

                // The tap was already sent for this turn. A busy game may
                // leave the picker header visible, so never recheck the other
                // four logos or send a second tap here.
                return null;
            }

            var selection = await _trainingSelectionDetector.DetectAsync(
                    connection,
                    pack,
                    cancellationToken)
                .ConfigureAwait(false);
            if (selection.ScreenChanged)
            {
                logSink?.Add(
                    "Career Training",
                    "Training selection changed before the next click; observing the current screen again.");
                return null;
            }

            if (!selection.Succeeded)
            {
                return CareerRuntimeResults.Failure(
                    $"Could not identify the raised training item: {selection.Error}",
                    screenId);
            }

            logSink?.Add(
                "Career Training",
                $"Raised training item: {selection.RaisedType}; "
                + string.Join(", ", selection.Matches.Select(item =>
                    $"{item.TrainingType}=y{item.Match.CenterY}/score{item.Match.Score:0.000}")));
            if (string.Equals(selection.RaisedType, targetTrainingType,
                    StringComparison.OrdinalIgnoreCase))
            {
                entryTask = $"training_selection_{targetTrainingType}_raised_probe";
            }
        }

        var result = await _actionExecutor.RunTaskAsync(
                connection,
                pack,
                entryTask,
                logSink,
                cancellationToken,
                options)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return CareerRuntimeResults.Failure(
                $"Could not execute JSON task '{entryTask}' for '{screenId}.{actionId}': {result.Message}",
                screenId);
        }

        if (state is not null && isTrainingEntry)
            state.TrainingSelectionEntryConfirmed = result.Outcome == "training.selection.confirmed";

        if (state is not null
            && screenId == CareerRaceRunnerCheckpointHandler.ScreenId
            && actionId == "entry.view_results"
            && result.Outcome == "race.replay.completed")
        {
            CareerRaceFlow.MarkReplayFlowCompleted(state);
        }

        if (state is not null && screenId == "race_retry_dialog")
        {
            state.RaceRetryDeclined = result.Outcome == "race.retry.declined";
        }

        if (state is not null
            && string.Equals(screenId, "training_selection", StringComparison.OrdinalIgnoreCase)
            && actionId.StartsWith("training.", StringComparison.OrdinalIgnoreCase)
            && UraTrainingTypeCatalog.TryNormalize(actionId["training.".Length..],
                out var clickedTrainingType))
        {
            state.TrainingClickIssuedType = clickedTrainingType;
            state.TrainingClickTargetGone = false;
            // A resumed training-selection page may not have passed through
            // CareerTurnFlow's main-page entry action. Establish the same
            // turn baseline here before waiting for result/date confirmation.
            CareerRaceStreakPolicy.BeginTurnAction(state, UraPlannedAction.Training);
            state.TrainingTurnCommitPending = true;
            state.TrainingTurnCommitType = clickedTrainingType;
            state.TrainingTurnCommitTurnIndex = state.TurnIndex;
        }

        return null;
    }
}
