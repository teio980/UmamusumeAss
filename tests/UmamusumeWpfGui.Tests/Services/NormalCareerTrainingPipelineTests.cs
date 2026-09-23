using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class NormalCareerTrainingPipelineTests
{
    [Fact]
    public void A_zero_turn_run_does_not_infer_a_start_transition()
    {
        var state = new UraCareerSessionState
        {
            TurnIndex = 0,
            CareerStarted = false,
            LastScreenId = "career_start_transition",
        };

        Assert.False(
            AdbNormalCareerTrainingPipeline.IsCareerStartTransitionExpected(state));
    }

    [Fact]
    public void An_in_memory_await_career_stage_expects_the_start_transition()
    {
        var state = new UraCareerSessionState
        {
            TurnIndex = 1,
            CareerStarted = false,
            NormalSetupStage = NormalCareerSetupStage.AwaitCareerMain,
        };

        Assert.True(
            AdbNormalCareerTrainingPipeline.IsCareerStartTransitionExpected(state));
    }

    [Theory]
    [InlineData("career_main")]
    [InlineData("training_selection")]
    [InlineData("race_list")]
    [InlineData("race_runner")]
    [InlineData("race_runner_result")]
    [InlineData("race_trophy_won")]
    [InlineData("race_playback_start")]
    [InlineData("race_day")]
    [InlineData("race_live")]
    [InlineData("race_result")]
    [InlineData("career_complete")]
    public void Live_career_pages_are_eligible_for_current_screen_recovery(string screenId)
    {
        Assert.True(AdbNormalCareerTrainingPipeline.IsRuntimeCareerScreen(screenId));
    }

    [Theory]
    [InlineData("career_main", CareerScreenKind.Main)]
    [InlineData("training_selection", CareerScreenKind.Turn)]
    [InlineData("race_live", CareerScreenKind.Race)]
    [InlineData("race_runner", CareerScreenKind.Race)]
    [InlineData("race_runner_result", CareerScreenKind.Race)]
    [InlineData("race_trophy_won", CareerScreenKind.Race)]
    [InlineData("race_playback_start", CareerScreenKind.Race)]
    [InlineData("event_choice", CareerScreenKind.Event)]
    [InlineData("career_result", CareerScreenKind.Settlement)]
    public void Runtime_observations_are_classified_before_flow_dispatch(
        string screenId,
        CareerScreenKind expected)
    {
        Assert.Equal(expected, AdbNormalCareerTrainingPipeline.ClassifyRuntimeScreen(screenId));
    }

    [Theory]
    [InlineData("home")]
    [InlineData("career_final_confirmation")]
    [InlineData("normal_quick_mode_settings")]
    [InlineData("unknown")]
    public void Entry_and_unknown_pages_are_not_runtime_observations(string screenId)
    {
        Assert.Equal(
            CareerScreenKind.Unknown,
            AdbNormalCareerTrainingPipeline.ClassifyRuntimeScreen(screenId));
    }

    [Theory]
    [InlineData("home")]
    [InlineData("career_continue")]
    [InlineData("scenario_select")]
    [InlineData("career_final_confirmation")]
    [InlineData("normal_quick_mode_settings")]
    public void Entry_and_setup_pages_are_not_treated_as_live_career_pages(string screenId)
    {
        Assert.False(AdbNormalCareerTrainingPipeline.IsRuntimeCareerScreen(screenId));
    }

    [Fact]
    public void Started_career_does_not_misclassify_the_support_ok_dialog_as_rest_confirmation()
    {
        var state = new UraCareerSessionState
        {
            CareerStarted = true,
            TurnIndex = 1,
        };

        Assert.False(
            CareerScreenObserver.IsEligibleForCareerPhase(
                "support_autofill_confirmation",
                state));
        Assert.True(
            CareerScreenObserver.IsEligibleForCareerPhase(
                "rest_confirmation",
                state));
    }

    [Fact]
    public void Race_list_resume_without_goal_context_uses_recommended_entry()
    {
        Assert.Equal(
            "recommended_entry",
            CareerRaceFlow.GetRaceListActionId(new UraCareerSessionState()));
    }

    [Fact]
    public void Race_list_uses_recommended_or_first_entry_even_with_a_race_goal()
    {
        var state = new UraCareerSessionState
        {
            ObservedGoalKind = CareerGoalTextParser.Race,
        };

        Assert.Equal("recommended_entry", CareerRaceFlow.GetRaceListActionId(state));
    }
}
