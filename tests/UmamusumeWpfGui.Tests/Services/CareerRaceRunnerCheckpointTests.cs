using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerRaceRunnerCheckpointTests
{
    [Fact]
    public async Task Configured_strategy_continues_to_view_results_in_the_same_checkpoint()
    {
        var actions = new RecordingActions();
        var state = new UraCareerSessionState
        {
            ObservedGoalKind = CareerGoalTextParser.GradeRaceCount,
            GradeRaceTimesLeft = 2,
        };

        var result = await new CareerRaceRunnerCheckpointHandler(actions)
            .HandleAsync(CreateContext(state));

        Assert.Null(result);
        Assert.Equal(
            ["race_runner.strategy.apply.pace", "race_runner.entry.view_results"],
            actions.Calls);
        Assert.True(state.RaceStrategyConfigured);
        Assert.True(state.RaceReplayFlowCompleted);
        Assert.False(state.GoalCompletionProbeArmed);
    }

    [Fact]
    public async Task Resumed_runner_reuses_result_flow_and_arms_the_final_count_goal()
    {
        var actions = new RecordingActions();
        var state = new UraCareerSessionState
        {
            RaceStrategyConfigured = true,
            ObservedGoalKind = CareerGoalTextParser.GradeRaceCount,
            GradeRaceTimesLeft = 1,
        };

        var result = await new CareerRaceRunnerCheckpointHandler(actions)
            .HandleAsync(CreateContext(state));

        Assert.Null(result);
        Assert.Equal(["race_runner.entry.view_results"], actions.Calls);
        Assert.True(state.RaceReplayFlowCompleted);
        Assert.True(state.GoalCompletionProbeArmed);
    }

    [Fact]
    public async Task Failed_result_flow_does_not_mark_the_race_complete()
    {
        var actions = new RecordingActions
        {
            FailureAction = "entry.view_results",
        };
        var state = new UraCareerSessionState
        {
            RaceStrategyConfigured = true,
            ObservedGoalKind = CareerGoalTextParser.GradeRaceCount,
            GradeRaceTimesLeft = 1,
        };

        var result = await new CareerRaceRunnerCheckpointHandler(actions)
            .HandleAsync(CreateContext(state));

        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.False(state.RaceReplayFlowCompleted);
        Assert.False(state.GoalCompletionProbeArmed);
    }

    private static CareerFlowContext CreateContext(UraCareerSessionState state) =>
        new(
            null!, null!, false, null!, null!, "pace", state,
            new CareerObservation("race_runner", 1), null,
            CancellationToken.None);

    private sealed class RecordingActions : ICareerFlowActionRunner
    {
        public List<string> Calls { get; } = [];
        public string? FailureAction { get; init; }

        public Task<CareerTrainingResult?> RunAsync(
            CareerFlowContext context,
            string screenId,
            string actionId,
            HachimiPipelineRunOptions? options = null)
        {
            Calls.Add($"{screenId}.{actionId}");
            return Task.FromResult<CareerTrainingResult?>(
                actionId == FailureAction
                    ? CareerRuntimeResults.Failure("Race action failed.", screenId)
                    : null);
        }
    }
}
