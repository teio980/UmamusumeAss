namespace UmamusumeWpfGui.Services.Training;

internal sealed class CareerSettlementFlow
{
    private readonly ICareerFlowActionRunner _actions;

    public CareerSettlementFlow(ICareerFlowActionRunner actions)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    public async Task<CareerTrainingResult?> HandleAsync(CareerFlowContext context)
    {
        switch (context.Observation.ScreenId)
        {
            case "complete_career":
                return await _actions.RunAsync(
                        context,
                        "complete_career",
                        "finish")
                    .ConfigureAwait(false);
            case "career_rank":
                return await _actions.RunAsync(
                        context,
                        "career_rank",
                        "next")
                    .ConfigureAwait(false);
            case "career_result":
                return await _actions.RunAsync(
                        context,
                        "career_result",
                        "next")
                    .ConfigureAwait(false);
            case "rewards":
                return await _actions.RunAsync(
                        context,
                        "rewards",
                        "next")
                    .ConfigureAwait(false);
            case "sparks":
                return await _actions.RunAsync(
                        context,
                        "sparks",
                        "confirm")
                    .ConfigureAwait(false);
            case "sparks_confirmation":
                return await _actions.RunAsync(
                        context,
                        "sparks_confirmation",
                        "keep")
                    .ConfigureAwait(false);
            case "career_complete":
                return await _actions.RunAsync(
                        context,
                        "career_complete",
                        "to_home")
                    .ConfigureAwait(false);
            default:
                return null;
        }
    }
}
