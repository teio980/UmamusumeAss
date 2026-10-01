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
            case "complete_career_entry":
                return await _actions.RunAsync(
                        context,
                        "complete_career_entry",
                        "open")
                    .ConfigureAwait(false);
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
            case "career_rating_record_updated":
                return await _actions.RunAsync(
                        context,
                        "career_rating_record_updated",
                        "next")
                    .ConfigureAwait(false);
            case "career_result":
                return await _actions.RunAsync(
                        context,
                        "career_result",
                        "next")
                    .ConfigureAwait(false);
            case "career_result_close":
                return await _actions.RunAsync(
                        context,
                        "career_result_close",
                        "close")
                    .ConfigureAwait(false);
            case "follow_trainer_limit":
                return await _actions.RunAsync(
                        context,
                        "follow_trainer_limit",
                        "cancel")
                    .ConfigureAwait(false);
            case "career_epithet":
                return await _actions.RunAsync(
                        context,
                        "career_epithet",
                        "epithet_confirm")
                    .ConfigureAwait(false);
            case "rewards":
                return await _actions.RunAsync(
                        context,
                        "rewards",
                        "next")
                    .ConfigureAwait(false);
            case "event_reward":
                return await _actions.RunAsync(
                        context,
                        "event_reward",
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
            case "career_complete_close":
                return await _actions.RunAsync(
                        context,
                        "career_complete_close",
                        "close")
                    .ConfigureAwait(false);
            case "career_story_unlocked":
                return await _actions.RunAsync(
                        context,
                        "career_story_unlocked",
                        "close")
                    .ConfigureAwait(false);
            default:
                return null;
        }
    }
}
