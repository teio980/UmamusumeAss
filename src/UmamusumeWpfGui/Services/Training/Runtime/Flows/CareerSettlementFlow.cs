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
            case "goal_incomplete":
                context.LogSink?.Add(
                    "Career Training",
                    "Goal Incomplete recognized; selecting Next and continuing to Career settlement.");
                return await _actions.RunAsync(
                        context,
                        "goal_incomplete",
                        "goal.next")
                    .ConfigureAwait(false);
            case "complete_career_entry":
                return await _actions.RunAsync(
                        context,
                        "complete_career_entry",
                        "career.open")
                    .ConfigureAwait(false);
            case "complete_career":
                return await _actions.RunAsync(
                        context,
                        "complete_career",
                        "career.finish")
                    .ConfigureAwait(false);
            case "career_rank":
                return await _actions.RunAsync(
                        context,
                        "career_rank",
                        "career.next")
                    .ConfigureAwait(false);
            case "career_rating_record_updated":
                return await _actions.RunAsync(
                        context,
                        "career_rating_record_updated",
                        "career.rating_record.next")
                    .ConfigureAwait(false);
            case "career_result":
                return await _actions.RunAsync(
                        context,
                        "career_result",
                        "career.next")
                    .ConfigureAwait(false);
            case "career_result_close":
                return await _actions.RunAsync(
                        context,
                        "career_result_close",
                        "career.close")
                    .ConfigureAwait(false);
            case "follow_trainer_limit":
                return await _actions.RunAsync(
                        context,
                        "follow_trainer_limit",
                        "career.follow_limit.cancel")
                    .ConfigureAwait(false);
            case "career_epithet":
                return await _actions.RunAsync(
                        context,
                        "career_epithet",
                        "career.epithet_confirm")
                    .ConfigureAwait(false);
            case "rewards":
                return await _actions.RunAsync(
                        context,
                        "rewards",
                        "rewards.next")
                    .ConfigureAwait(false);
            case "event_reward":
                return await _actions.RunAsync(
                        context,
                        "event_reward",
                        "event_reward.next")
                    .ConfigureAwait(false);
            case "rewards_collected":
                return await _actions.RunAsync(
                        context,
                        "rewards_collected",
                        "rewards_collected.close")
                    .ConfigureAwait(false);
            case "sparks":
                return await _actions.RunAsync(
                        context,
                        "sparks",
                        "sparks.confirm")
                    .ConfigureAwait(false);
            case "sparks_confirmation":
                return await _actions.RunAsync(
                        context,
                        "sparks_confirmation",
                        "sparks.keep")
                    .ConfigureAwait(false);
            case "career_complete":
            case "career_story_unlocked_to_home":
                return await _actions.RunAsync(
                        context,
                        context.Observation.ScreenId,
                        context.Observation.ScreenId == "career_story_unlocked_to_home"
                            ? "career.story.to_home"
                            : "career.to_home")
                    .ConfigureAwait(false);
            case "career_complete_close":
                return await _actions.RunAsync(
                        context,
                        "career_complete_close",
                        "career.close")
                    .ConfigureAwait(false);
            case "career_story_unlocked":
            case "career_story_unlocked_compact":
                return await _actions.RunAsync(
                        context,
                        context.Observation.ScreenId,
                        context.Observation.ScreenId == "career_story_unlocked_compact"
                            ? "career.story.compact.close"
                            : "career.story.close")
                    .ConfigureAwait(false);
            default:
                return null;
        }
    }
}
