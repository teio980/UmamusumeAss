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
            case "career_intro_event":
                context.State.LastScreenId = "career_start_transition";
                return await _actions.RunAsync(
                        context,
                        "career_intro_event",
                        "advance")
                    .ConfigureAwait(false);
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
            case "training_event":
            case "rest_result":
                context.State.HasScenarioEvent = false;
                return await _actions.RunAsync(
                        context,
                        context.Observation.ScreenId,
                        "advance")
                    .ConfigureAwait(false);
            case "event_choice":
                return await _actions.RunAsync(
                        context,
                        "event_choice",
                        "choice_first")
                    .ConfigureAwait(false);
            case "rest_confirmation":
                context.State.LastAction = UraPlannedAction.Rest;
                return await _actions.RunAsync(
                        context,
                        "rest_confirmation",
                        "confirm")
                    .ConfigureAwait(false);
            case "scenario_event":
                context.State.HasScenarioEvent = false;
                return await _actions.RunAsync(
                        context,
                        "scenario_event",
                        "advance")
                    .ConfigureAwait(false);
            default:
                return null;
        }
    }

    private async Task<CareerTrainingResult?> HandleCareerMainAsync(
        CareerFlowContext context)
    {
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
        return await _actions.RunAsync(
                context,
                "career_main",
                actionId)
            .ConfigureAwait(false);
    }
}
