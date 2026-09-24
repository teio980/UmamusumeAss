using UmamusumeWpfGui.Models;
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
                context.State.RaceReplayFlowCompleted = false;
                context.State.LastScreenId = "career_start_transition";
                return await _actions.RunAsync(
                        context,
                        "career_races_ready",
                        "races")
                    .ConfigureAwait(false);
            case "race_day":
                context.State.RaceReplayFlowCompleted = false;
                context.State.HasPendingRace = true;
                context.State.LastAction = UraPlannedAction.Race;
                if (string.IsNullOrWhiteSpace(context.State.ObservedGoalKind))
                {
                    // A process restart rebuilds state from the visible page.
                    // Keep the objective-race context for goal completion
                    // handling, while Race List uses the shared
                    // Recommended-or-first race selection below.
                    context.State.ObservedGoalKind = CareerGoalTextParser.Race;
                }
                return await _actions.RunAsync(
                        context,
                        "race_day",
                        "open_list")
                    .ConfigureAwait(false);
            case "race_list":
                if (string.IsNullOrWhiteSpace(context.State.ObservedGoalKind))
                {
                    // A new run reconstructs its state from the current
                    // screen. Race List itself does not expose whether the
                    // previous Career Main goal was a fan goal. Preserve the
                    // existing fallback classification for this resume path.
                    context.State.ObservedGoalKind = CareerGoalTextParser.Fans;
                }

                context.LogSink?.Add(
                    "Career Training",
                    context.State.ObservedGoalKind == CareerGoalTextParser.GradeRaceCount
                        ? "The race calendar marks this turn's first Race List card as qualifying; selecting it."
                        : "Selecting the Recommended race, or the first available race if none is marked.",
                    LogEntryKind.Info);
                return await _actions.RunAsync(
                        context,
                        "race_list",
                        GetRaceListActionId(context.State))
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
            case "race_playback_start":
                context.State.RaceReplayFlowCompleted = false;
                context.State.HasPendingRace = true;
                context.LogSink?.Add(
                    "Career Training",
                    "Race! checkpoint recognized; resuming from the Race! button.",
                    LogEntryKind.Info);
                return await _actions.RunAsync(
                        context,
                        "race_playback_start",
                        "race.play")
                    .ConfigureAwait(false);
            case "race_trophy_won":
                context.State.HasPendingRace = true;
                context.LogSink?.Add(
                    "Career Training",
                    "Optional Trophy Won overlay recognized; closing it before resuming the runner flow.",
                    LogEntryKind.Info);
                return await _actions.RunAsync(
                        context,
                        "race_trophy_won",
                        "trophy.close")
                    .ConfigureAwait(false);
            case "race_runner_result":
                if (context.State.RaceReplayFlowCompleted)
                {
                    context.LogSink?.Add(
                        "Career Training",
                        "Ignoring a stale Replay marker; the Next flow already completed for this race.",
                        LogEntryKind.Info);
                    return null;
                }

                context.State.HasPendingRace = true;
                context.State.LastAction = UraPlannedAction.Race;
                context.LogSink?.Add(
                    "Career Training",
                    "Race Replay checkpoint recognized; continuing with Next and Final Next.",
                    LogEntryKind.Info);
                return await HandleRaceRunnerResultAsync(context).ConfigureAwait(false);
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
                context.LogSink?.Add(
                    "Career Training",
                    "Next objective is displayed; continuing with Next.",
                    LogEntryKind.Info);
                return await _actions.RunAsync(
                        context,
                        "goal_update",
                        "update_next")
                    .ConfigureAwait(false);
            case "goal_objective_complete":
                context.LogSink?.Add(
                    "Career Training",
                    "A single Career objective is complete; opening its objective summary.",
                    LogEntryKind.Info);
                return await _actions.RunAsync(
                        context,
                        "goal_objective_complete",
                        "next")
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
                context.LogSink?.Add(
                    "Career Training",
                    "All normal objectives are complete; continuing to URA Finale.",
                    LogEntryKind.Info);
                return await _actions.RunAsync(
                        context,
                        "goal_complete",
                        "next")
                    .ConfigureAwait(false);
            default:
                return null;
        }
    }

    internal static string GetRaceListActionId(UraCareerSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.ObservedGoalKind == CareerGoalTextParser.GradeRaceCount
            ? "fans_entry"
            : "recommended_entry";
    }

    private async Task<CareerTrainingResult?> HandleRaceRunnerResultAsync(
        CareerFlowContext context)
    {
        // The runner task performs Next and Final Next as one action. Only
        // after it succeeds do we enable the goal probe, so any post-race
        // event is handled first by the higher-priority event screens.
        var result = await _actions.RunAsync(
                context,
                "race_runner_result",
                "result.next")
            .ConfigureAwait(false);
        if (result is null)
            MarkReplayFlowCompleted(context.State);

        return result;
    }

    internal static void MarkReplayFlowCompleted(UraCareerSessionState state)
    {
        state.RaceReplayFlowCompleted = true;
        if (state.ObservedGoalKind == CareerGoalTextParser.Race
            || (state.ObservedGoalKind == CareerGoalTextParser.GradeRaceCount
                && state.GradeRaceTimesLeft is <= 1))
        {
            state.GoalCompletionProbePending = false;
            state.GoalCompletionProbeArmed = true;
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

        // The game can show the objective completion banner immediately
        // after leaving the race result/reward pages. Arm only now, instead
        // of matching the GOAL template throughout the race playback.
        CareerTurnFlow.ArmPendingGoalProbe(context.State);

        return await _actions.RunAsync(
                context,
                "race_result",
                "next")
            .ConfigureAwait(false);
    }
}
