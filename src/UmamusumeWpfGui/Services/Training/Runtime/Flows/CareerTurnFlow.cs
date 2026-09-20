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
                return await _actions.RunAsync(
                        context,
                        "training_selection",
                        "speed")
                    .ConfigureAwait(false);
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

        context.LogSink?.Add("URA Strategy", decision.Reason);
        var actionId = decision.Action switch
        {
            UraPlannedAction.Rest => "rest",
            UraPlannedAction.FinaleRace => "finale_races",
            _ => "training",
        };
        context.State.LastAction = decision.Action;
        return await _actions.RunAsync(
                context,
                "career_main",
                actionId)
            .ConfigureAwait(false);
    }
}
