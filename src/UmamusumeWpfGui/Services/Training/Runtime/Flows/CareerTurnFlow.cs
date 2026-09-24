using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

internal sealed class CareerTurnFlow
{
    private readonly ICareerFlowActionRunner _actions;

    public CareerTurnFlow(ICareerFlowActionRunner actions)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    public async Task<CareerTrainingResult?> HandleAsync(CareerFlowContext context)
    {
        switch (context.Observation.ScreenId)
        {
            case "career_main":
                return await HandleCareerMainAsync(context).ConfigureAwait(false);
            case "training_selection":
                context.State.LastAction = UraPlannedAction.Training;
                if (!UraTrainingTypeCatalog.TryNormalize(
                        context.State.PendingTrainingType,
                        out var resumedTrainingType))
                {
                    // A process restart can leave the emulator on the
                    // training picker before the in-memory pending type was
                    // persisted. Re-evaluate the configured strategy so the
                    // picker is a valid mid-career checkpoint.
                    var resumedDecision = context.Strategy.ChooseTurnAction(
                        context.Scenario,
                        context.State);
                    if (resumedDecision.Action != UraPlannedAction.Training
                        || !UraTrainingTypeCatalog.TryNormalize(
                            resumedDecision.TargetId,
                            out resumedTrainingType))
                    {
                        return CareerRuntimeResults.Failure(
                            "The training-selection checkpoint could not recover a training type.",
                            "training_selection");
                    }

                    context.State.PendingTrainingType = resumedTrainingType;
                }

                // The main-page action only opened the picker. Arm the
                // one-shot GOAL probe now, when the actual training action is
                // about to be selected.
                ArmPendingGoalProbe(context.State);

                if (!UraTrainingTypeCatalog.TryGetSemanticAction(
                        resumedTrainingType,
                        out var trainingAction,
                        out var trainingType))
                {
                    return CareerRuntimeResults.Failure(
                        "The selected training strategy did not provide a supported training type.",
                        "training_selection");
                }

                var trainingResult = await _actions.RunAsync(
                        context,
                        "training_selection",
                        trainingAction)
                    .ConfigureAwait(false);
                if (trainingResult is null)
                    context.State.PendingTrainingType = trainingType;
                return trainingResult;
            case "training_result":
                return await _actions.RunAsync(
                        context,
                        context.Observation.ScreenId,
                        "advance")
                    .ConfigureAwait(false);
            case "rest_confirmation":
                context.State.LastAction = UraPlannedAction.Rest;
                context.State.AwaitingRestReturn = true;
                ArmPendingGoalProbe(context.State);
                return await _actions.RunAsync(
                        context,
                        "rest_confirmation",
                        "confirm")
                    .ConfigureAwait(false);
            default:
                return null;
        }
    }

    private async Task<CareerTrainingResult?> HandleCareerMainAsync(
        CareerFlowContext context)
    {
        // The next confirmed main page opens a new turn. Its training picker
        // must not inherit the previous turn's tap guard.
        context.State.TrainingClickIssuedType = null;
        context.State.TrainingClickTargetGone = false;

        // The race-result flow has completed. Wait here while post-race event
        // and Goal Achieved pages settle instead of starting another turn or
        // entering the same race again.
        if (context.State.GoalCompletionProbeArmed
            && context.State.LastAction == UraPlannedAction.Race)
        {
            return null;
        }

        if (string.Equals(
                context.State.ObservedGoalKind,
                CareerGoalTextParser.Race,
                StringComparison.OrdinalIgnoreCase))
        {
            if (context.State.TurnsToGoal is null
                && !context.State.HasPendingRace)
            {
                return CareerRuntimeResults.Failure(
                    "A race goal is visible, but its remaining-turn countdown could not be read; "
                    + "automation paused safely before choosing an action.",
                    "career_main");
            }

            if (context.State.TurnsToGoal <= 0
                && !context.State.HasPendingRace)
            {
                return CareerRuntimeResults.Failure(
                    $"Race goal reached: '{context.State.ObservedGoalText}', but no required race is available.",
                    "career_main");
            }
        }

        if (string.Equals(
                context.State.ObservedGoalKind,
                CareerGoalTextParser.GradeRaceCount,
                StringComparison.OrdinalIgnoreCase))
        {
            if (!context.Scenario.HasRaceGradeScheduleData
                || context.State.TurnIndexSource != UraStateSource.Observed
                || context.State.TurnsToGoal is null
                || context.State.GradeRaceTimesLeft is null)
            {
                return CareerRuntimeResults.Failure(
                    "The grade race goal needs a readable date, countdown, remaining count, and race calendar; automation paused safely.",
                    "career_main");
            }

            if (context.State.TurnsToGoal <= 0
                && context.State.GradeRaceTimesLeft > 0)
            {
                return CareerRuntimeResults.Failure(
                    $"The {context.State.TargetRaceGrade} goal still needs {context.State.GradeRaceTimesLeft} qualifying race(s), but its deadline has arrived.",
                    "career_main");
            }
        }

        if (context.State.ObservedGoalKind != CareerGoalTextParser.GradeRaceCount
            && context.State.ObservedGoalKind != CareerGoalTextParser.Fans
            && context.State.TurnsToGoal is > 0
            && CareerGoalTextParser.ParseRaceCountLeft(context.State.ObservedGoalText) is > 0)
        {
            return CareerRuntimeResults.Failure(
                "A race-count goal is visible, but no matching grade condition was found in the trainee database; automation paused safely.",
                "career_main");
        }

        if (context.Observation.EnergyPercent is not int energyPercent)
        {
            context.LogSink?.Add(
                "Career Training",
                "Could not observe the career energy bar; pausing safely before choosing an action.",
                LogEntryKind.Failure);
            return CareerRuntimeResults.Failure(
                "Could not observe a stable career energy bar.",
                "career_main");
        }

        var decision = context.Strategy.ChooseTurnAction(
            context.Scenario,
            context.State);
        var availableActions = context.Scenario.GetAvailableActions(
            context.State,
            "career_main");
        if (!availableActions.Contains(decision.Action))
        {
            return CareerRuntimeResults.Failure(
                $"URA strategy selected unavailable action '{decision.Action}' "
                + $"in phase '{context.State.PhaseId}'.",
                "career_main");
        }

        context.LogSink?.Add(
            "URA Strategy",
            $"Observed energy {energyPercent}% before choosing an action. {decision.Reason}");
        var actionId = decision.Action switch
        {
            UraPlannedAction.Rest => "rest",
            UraPlannedAction.Race => "races",
            UraPlannedAction.FinaleRace => "finale_races",
            UraPlannedAction.Training => "training",
            _ => string.Empty,
        };
        if (actionId.Length == 0)
        {
            return CareerRuntimeResults.Failure(
                $"URA strategy selected unsupported action '{decision.Action}'.",
                "career_main");
        }

        string? trainingType = null;
        if (decision.Action == UraPlannedAction.Training)
        {
            if (!UraTrainingTypeCatalog.TryNormalize(
                    decision.TargetId,
                    out var normalizedTrainingType))
            {
                return CareerRuntimeResults.Failure(
                    $"URA strategy selected unsupported training type '{decision.TargetId ?? "(missing)"}'.",
                    "career_main");
            }

            trainingType = normalizedTrainingType;
        }

        context.State.PendingTrainingType = trainingType;
        context.State.LastAction = decision.Action;
        if (decision.Action is UraPlannedAction.Race or UraPlannedAction.FinaleRace)
            context.State.RaceReplayFlowCompleted = false;
        if (decision.Action == UraPlannedAction.Race
            && context.State.ObservedGoalKind == CareerGoalTextParser.GradeRaceCount)
        {
            context.State.GradeRaceStartedTurnIndex = context.State.TurnIndex;
        }
        context.State.GoalCompletionProbePending = ShouldProbeGoalAfterAction(
            context.State);
        context.State.GoalCompletionProbeArmed = false;
        if (decision.Action == UraPlannedAction.Rest)
        {
            context.State.RestStartedTurnIndex = context.State.TurnIndex;
            context.State.RestStartedEnergyPercent = energyPercent;
        }
        return await _actions.RunAsync(
                context,
                "career_main",
                actionId)
            .ConfigureAwait(false);
    }

    private static bool ShouldProbeGoalAfterAction(UraCareerSessionState state)
    {
        // Race goals are checked after the race flow, not after the final
        // training/rest action that leads into Race Day.
        if (state.ObservedGoalKind is CareerGoalTextParser.Race or CareerGoalTextParser.GradeRaceCount)
        {
            return false;
        }

        return state.TurnsToGoal is <= 1
            || (string.Equals(
                    state.ObservedGoalKind,
                    CareerGoalTextParser.Fans,
                    StringComparison.OrdinalIgnoreCase)
                && state.FansToGoal is <= 0);
    }

    internal static void ArmPendingGoalProbe(UraCareerSessionState state)
    {
        if (!state.GoalCompletionProbePending)
            return;

        state.GoalCompletionProbePending = false;
        state.GoalCompletionProbeArmed = true;
    }
}
