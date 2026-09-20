using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

internal sealed class CareerRaceFlow
{
    private readonly UraRaceResultRecognizer _raceResultRecognizer;
    private readonly ICareerFlowActionRunner _actions;

    public CareerRaceFlow(
        IVisualPipelineRuntime visualRuntime,
        ICareerFlowActionRunner actions)
    {
        _raceResultRecognizer = new UraRaceResultRecognizer(
            visualRuntime ?? throw new ArgumentNullException(nameof(visualRuntime)));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    public async Task<CareerTrainingResult?> HandleAsync(CareerFlowContext context)
    {
        switch (context.Observation.ScreenId)
        {
            case "career_races_ready":
                context.State.LastScreenId = "career_start_transition";
                return await _actions.RunAsync(
                        context,
                        "career_races_ready",
                        "races")
                    .ConfigureAwait(false);
            case "race_day":
                context.State.HasPendingRace = true;
                return await _actions.RunAsync(
                        context,
                        "race_day",
                        "open_list")
                    .ConfigureAwait(false);
            case "race_list":
                return await _actions.RunAsync(
                        context,
                        "race_list",
                        "goal_entry")
                    .ConfigureAwait(false);
            case "race_details":
                return await _actions.RunAsync(
                        context,
                        "race_details",
                        "confirm")
                    .ConfigureAwait(false);
            case "race_attributes":
                return await _actions.RunAsync(
                        context,
                        "race_attributes",
                        "start_playback")
                    .ConfigureAwait(false);
            case "race_playback":
                return await _actions.RunAsync(
                        context,
                        "race_playback",
                        "play")
                    .ConfigureAwait(false);
            case "race_playback_settings":
                return await _actions.RunAsync(
                        context,
                        "race_playback_settings",
                        "playback_settings_ok")
                    .ConfigureAwait(false);
            case "race_live":
                return await _actions.RunAsync(
                        context,
                        "race_live",
                        "live_next")
                    .ConfigureAwait(false);
            case "goal_update":
                return await _actions.RunAsync(
                        context,
                        "goal_update",
                        "update_next")
                    .ConfigureAwait(false);
            case "race_result":
                return await HandleRaceResultAsync(context).ConfigureAwait(false);
            case "reward":
                return await _actions.RunAsync(
                        context,
                        "reward",
                        "next")
                    .ConfigureAwait(false);
            case "reward_support":
                return await _actions.RunAsync(
                        context,
                        "reward_support",
                        "next")
                    .ConfigureAwait(false);
            case "goal_complete":
                context.State.HasPendingRace = true;
                return await _actions.RunAsync(
                        context,
                        "goal_complete",
                        "next")
                    .ConfigureAwait(false);
            default:
                return null;
        }
    }

    private async Task<CareerTrainingResult?> HandleRaceResultAsync(
        CareerFlowContext context)
    {
        var currentRace = context.Scenario.CurrentRace(context.State);
        if (currentRace is null)
        {
            return CareerRuntimeResults.Failure(
                "Race result was shown but the scenario has no current race.",
                context.Observation.ScreenId);
        }

        var placementObservation = await _raceResultRecognizer.RecognizeAsync(
                context.Connection,
                context.Pack,
                currentRace,
                context.CancellationToken)
            .ConfigureAwait(false);
        if (placementObservation is null)
        {
            return CareerRuntimeResults.Failure(
                $"Could not confirm the placement for race '{currentRace.RaceId}' "
                + "from a data-backed result template; automation paused safely.",
                context.Observation.ScreenId);
        }

        try
        {
            context.Scenario.ApplyRaceResult(
                context.State,
                placementObservation.Placement,
                placementObservation.Confidence);
        }
        catch (UraUnknownOutcomeException ex)
        {
            return CareerRuntimeResults.Failure(
                ex.Message,
                context.Observation.ScreenId);
        }

        return await _actions.RunAsync(
                context,
                "race_result",
                "next")
            .ConfigureAwait(false);
    }
}
