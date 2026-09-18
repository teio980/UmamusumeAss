using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class NormalCareerTrainingPipelineTests
{
    [Fact]
    public void Zero_turn_start_transition_checkpoint_reopens_shared_entry_flow()
    {
        var state = new UraCareerSessionState
        {
            TurnIndex = 0,
            CareerStarted = false,
            LastScreenId = "career_start_transition",
        };

        Assert.False(
            AdbNormalCareerTrainingPipeline.IsPersistedCareerStartTransitionExpected(state));
    }

    [Fact]
    public void Observed_career_progress_can_resume_from_start_transition()
    {
        var state = new UraCareerSessionState
        {
            TurnIndex = 1,
            CareerStarted = false,
            LastScreenId = "career_start_transition",
        };

        Assert.True(
            AdbNormalCareerTrainingPipeline.IsPersistedCareerStartTransitionExpected(state));
    }
}
