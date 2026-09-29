using System.Globalization;
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
            case "inheritance_event":
            {
                var inheritanceResult = await _actions.RunAsync(
                        context,
                        "inheritance_event",
                        "go")
                    .ConfigureAwait(false);
                if (inheritanceResult is null)
                    context.State.InheritanceEventPending = false;
                return inheritanceResult;
            }
            case "rest_confirmation":
            case "summer_rest_confirmation":
                context.State.LastAction = UraPlannedAction.Rest;
                ArmPendingGoalProbe(context.State);
                var restConfirmationResult = await _actions.RunAsync(
                        context,
                        context.Observation.ScreenId,
                        "confirm")
                    .ConfigureAwait(false);
                if (restConfirmationResult is null)
                    context.State.AwaitingRestConfirmationGone = true;
                return restConfirmationResult;
            case "recreation_selection":
                context.State.LastAction = UraPlannedAction.Recreation;
                ArmPendingGoalProbe(context.State);
                return await _actions.RunAsync(
                        context,
                        "recreation_selection",
                        "trainee")
                    .ConfigureAwait(false);
            case "recreation_confirmation":
                context.State.LastAction = UraPlannedAction.Recreation;
                ArmPendingGoalProbe(context.State);
                var recreationConfirmationResult = await _actions.RunAsync(
                        context,
                        "recreation_confirmation",
                        "confirm")
                    .ConfigureAwait(false);
                if (recreationConfirmationResult is null)
                    context.State.AwaitingRecreationConfirmationGone = true;
                return recreationConfirmationResult;
            case "infirmary_confirmation":
                context.State.LastAction = UraPlannedAction.Infirmary;
                ArmPendingGoalProbe(context.State);
                var infirmaryResult = await _actions.RunAsync(
                        context,
                        "infirmary_confirmation",
                        "confirm")
                    .ConfigureAwait(false);
                if (infirmaryResult is null)
                    context.LogSink?.Add("Career Training", "Infirmary treatment confirmed.");
                return infirmaryResult;
            default:
                return null;
        }
    }

    private async Task<CareerTrainingResult?> HandleCareerMainAsync(
        CareerFlowContext context)
    {
        if (context.State.InheritanceEventPending)
            return null;

        // The next confirmed main page opens a new turn. Its training picker
        // must not inherit the previous turn's tap guard.
        context.State.TrainingClickIssuedType = null;
        context.State.TrainingClickTargetGone = false;

        if (context.State.RaceUnavailableTurnIndex is int raceUnavailableTurn)
        {
            if (raceUnavailableTurn <= 0 || raceUnavailableTurn == context.State.TurnIndex)
            {
                context.State.HasPendingRace = false;
                context.State.RaceUnavailableTurnIndex = context.State.TurnIndex;
            }
            else
                context.State.RaceUnavailableTurnIndex = null;
        }

        // The race-result flow has completed. Wait here while post-race event
        // and Goal Achieved pages settle instead of starting another turn or
        // entering the same race again.
        if (context.State.GoalCompletionProbeArmed
            && context.State.LastAction == UraPlannedAction.Race)
        {
            return null;
        }

        if (context.Observation.InfirmaryAvailable)
        {
            context.State.LastAction = UraPlannedAction.Infirmary;
            context.State.GoalCompletionProbePending = ShouldProbeGoalAfterAction(
                context.State);
            context.State.GoalCompletionProbeArmed = false;
            context.LogSink?.Add(
                "Career Training",
                "Infirmary is available; treating illness before the next turn action.");
            var infirmaryEntryResult = await _actions.RunAsync(
                    context,
                    "career_main",
                    "infirmary")
                .ConfigureAwait(false);
            if (infirmaryEntryResult is null)
                CareerRaceStreakPolicy.BeginTurnAction(
                    context.State, UraPlannedAction.Infirmary);
            return infirmaryEntryResult;
        }

        if (string.Equals(
                context.State.ObservedGoalKind,
                CareerGoalTextParser.Race,
                StringComparison.OrdinalIgnoreCase))
        {
            if (CareerGoalTextParser.ParseRaceGrade(context.State.ObservedGoalText) is not null
                && context.State.ObservedGoalText?.Contains("time", StringComparison.OrdinalIgnoreCase) == true
                && context.State.GradeRaceTimesLeft is null)
            {
                LogOcrFallback(
                    context,
                    "the remaining count for the graded race goal");
            }

            if (context.State.TurnsToGoal is null
                && !context.State.HasPendingRace)
            {
                LogOcrFallback(
                    context,
                    "the remaining-turn countdown for the race goal");
            }

            if (context.State.TurnsToGoal <= 0
                && !context.State.HasPendingRace)
            {
                LogOcrFallback(
                    context,
                    "the race-goal deadline, so continuing with the available strategy");
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
                LogOcrFallback(
                    context,
                    "one or more graded race goal details; continuing with the available strategy");
            }

            if (context.State.TurnsToGoal <= 0
                && context.State.GradeRaceTimesLeft > 0)
            {
                LogOcrFallback(
                    context,
                    $"a possible deadline for the {context.State.TargetRaceGrade} race goal; "
                    + "continuing with the available strategy");
            }
        }

        if (context.State.ObservedGoalKind != CareerGoalTextParser.GradeRaceCount
            && context.State.ObservedGoalKind != CareerGoalTextParser.Fans
            && context.State.TurnsToGoal is > 0
            && CareerGoalTextParser.ParseRaceCountLeft(context.State.ObservedGoalText) is > 0)
        {
            LogOcrFallback(
                context,
                $"the grade for race-count goal OCR '{context.State.ObservedGoalText ?? "(empty)"}'; "
                + "continuing with the available strategy");
        }

        var energyPercent = context.Observation.EnergyPercent;
        if (energyPercent is null)
        {
            context.LogSink?.Add(
                "Career Training",
                "Could not read the career energy bar by OCR; continuing with a cautious fallback if needed.");
        }

        var decision = context.Strategy.ChooseTurnAction(
            context.Scenario,
            context.State);
        var availableActions = context.Scenario.GetAvailableActions(
            context.State,
            "career_main");
        if (energyPercent is null && decision.Action == UraPlannedAction.Training)
        {
            if (availableActions.Contains(UraPlannedAction.Rest))
            {
                decision = new UraActionIntent(
                    UraPlannedAction.Rest,
                    null,
                    "Energy could not be read by OCR; chose Rest as a cautious fallback.",
                    false,
                    [UraPlannedAction.Training]);
            }
            else
            {
                LogOcrFallback(
                    context,
                    "energy, and Rest is unavailable; continuing with the configured action");
            }
        }

        if (!availableActions.Contains(decision.Action))
        {
            return CareerRuntimeResults.Failure(
                $"URA strategy selected unavailable action '{decision.Action}' "
                + $"in phase '{context.State.PhaseId}'.",
                "career_main");
        }

        context.LogSink?.Add(
            "URA Strategy",
            $"Observed energy {energyPercent?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} before choosing an action. {decision.Reason}");
        var actionId = decision.Action switch
        {
            UraPlannedAction.Rest when context.State.CalendarStage == UraCalendarStage.SummerCamp
                => "summer_rest",
            UraPlannedAction.Rest => "rest",
            UraPlannedAction.Recreation => "recreation",
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
        if (IsInheritancePrecedingTurn(context.State))
        {
            context.State.InheritanceEventPending = true;
            context.LogSink?.Add(
                "Career Training",
                $"{context.State.TurnPositionLabel}: waiting for the inheritance GO event after this turn and its event finish.");
        }
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
        var actionResult = await _actions.RunAsync(
                context,
                "career_main",
                actionId)
            .ConfigureAwait(false);
        if (actionResult is null)
            CareerRaceStreakPolicy.BeginTurnAction(context.State, decision.Action);
        return actionResult;
    }

    private static bool ShouldProbeGoalAfterAction(UraCareerSessionState state)
    {
        // A goal can complete after any turn-consuming action, even when the
        // visible countdown or progress OCR has not reached its deadline.
        // Race results arm their own probe after the result flow completes.
        return state.LastAction is UraPlannedAction.Training
            or UraPlannedAction.Rest
            or UraPlannedAction.Recreation
            or UraPlannedAction.Infirmary;
    }

    private static bool IsInheritancePrecedingTurn(UraCareerSessionState state)
    {
        if (state.TurnIndexSource != UraStateSource.Observed
            || !UraTurnPositionParser.TryParse(state.TurnPositionLabel, out var position))
        {
            return false;
        }

        return position.Month == 3
            && string.Equals(position.Phase, "late", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(position.Year, "classic", StringComparison.OrdinalIgnoreCase)
                || string.Equals(position.Year, "senior", StringComparison.OrdinalIgnoreCase));
    }

    internal static void ArmPendingGoalProbe(UraCareerSessionState state)
    {
        if (!state.GoalCompletionProbePending)
            return;

        state.GoalCompletionProbePending = false;
        state.GoalCompletionProbeArmed = true;
    }

    private static void LogOcrFallback(CareerFlowContext context, string detail) =>
        context.LogSink?.Add(
            "Career Training",
            $"OCR could not reliably read {detail}; continuing instead of stopping.");
}
