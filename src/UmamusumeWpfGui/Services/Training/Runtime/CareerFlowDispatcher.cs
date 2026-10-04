using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public sealed class CareerFlowDispatcher : ICareerFlowActionRunner
{
    private readonly CareerJsonActionExecutor _actionExecutor;
    private readonly UraTrainingSelectionHeightDetector _trainingSelectionDetector;
    private readonly CareerTurnFlow _turnFlow;
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
        _actionExecutor = new CareerJsonActionExecutor(jsonRunner);
        _eventHandler = eventHandler ?? new CareerEventHandler(visualRuntime, this);
        _trainingSelectionDetector = new UraTrainingSelectionHeightDetector(visualRuntime);
        _turnFlow = new CareerTurnFlow(this);
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

    internal async Task<CareerTrainingResult?> DispatchAsync(
        LastVerifiedConnection connection,
        UraScenarioPack pack,
        bool pauseOnUnknownOutcome,
        UraScenarioModule scenario,
        UraDefaultStrategy strategy,
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
        RunScreenActionCoreAsync(
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
        }

        return null;
    }
}
