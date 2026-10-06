using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerTrainingRatioCommitTests
{
    [Fact]
    public void Result_confirmation_advances_ratio_once_through_interface()
    {
        var state = PendingTrainingState();
        var session = Session(state);
        ICareerTrainingStrategy<UraCareerSessionState> strategy =
            new UraRatioStrategy(new UraTrainingRatio(1, 1, 0, 0, 0));

        CareerTrainingEngine.ConfirmTrainingIfConsumed(
            strategy,
            session,
            state,
            new CareerObservation("training_result", 1));

        Assert.False(state.TrainingTurnCommitPending);
        Assert.Equal(1, ((UraRatioStrategy)strategy).NextTrainingIndex);

        // A duplicated result observation has no pending commit and must not
        // consume the next ratio item.
        CareerTrainingEngine.ConfirmTrainingIfConsumed(
            strategy,
            session,
            state,
            new CareerObservation("training_result", 1));
        Assert.Equal(1, ((UraRatioStrategy)strategy).NextTrainingIndex);
    }

    [Fact]
    public void Ratio_cursor_wraps_after_the_reduced_cycle()
    {
        var strategy = new UraRatioStrategy(new UraTrainingRatio(1, 0, 0, 0, 0));
        var state = new UraCareerSessionState();
        var session = Session(state);

        strategy.ConfirmTraining(session, "speed");

        Assert.Equal(0, strategy.NextTrainingIndex);
    }

    [Fact]
    public void Staying_on_training_page_or_same_turn_main_page_does_not_commit()
    {
        var state = PendingTrainingState();
        var session = Session(state);
        ICareerTrainingStrategy<UraCareerSessionState> strategy =
            new UraRatioStrategy(new UraTrainingRatio(1, 1, 0, 0, 0));

        CareerTrainingEngine.ConfirmTrainingIfConsumed(
            strategy,
            session,
            state,
            new CareerObservation("training_selection", 1));
        CareerTrainingEngine.ConfirmTrainingIfConsumed(
            strategy,
            session,
            state,
            new CareerObservation("career_main", 1));
        CareerTrainingEngine.ConfirmTrainingIfConsumed(
            strategy,
            session,
            state,
            new CareerObservation("rest_confirmation", 1));
        CareerTrainingEngine.ConfirmTrainingIfConsumed(
            strategy,
            session,
            state,
            new CareerObservation("race_day", 1));

        Assert.True(state.TrainingTurnCommitPending);
        Assert.Equal(0, ((UraRatioStrategy)strategy).NextTrainingIndex);
    }

    [Fact]
    public async Task Main_page_with_a_new_observed_turn_commits_when_result_was_missed()
    {
        var state = PendingTrainingState();
        state.PendingActionTurnIndex = 0;
        state.PendingActionTurnsToGoal = 3;
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var scenario = new UraScenarioModule(pack);
        scenario.ObserveScreen(
            state,
            "career_main",
            0.98,
            turnPositionText: "Junior Year Pre-Debut",
            turnsToGoal: 2,
            goalText: "Debut race");
        Assert.Null(state.PendingTurnAction);
        Assert.Equal(0, state.TurnIndex);
        var session = Session(state);
        ICareerTrainingStrategy<UraCareerSessionState> strategy =
            new UraRatioStrategy(new UraTrainingRatio(1, 1, 0, 0, 0));

        CareerTrainingEngine.ConfirmTrainingIfConsumed(
            strategy,
            session,
            state,
            new CareerObservation("career_main", 1));

        Assert.False(state.TrainingTurnCommitPending);
        Assert.Equal(1, ((UraRatioStrategy)strategy).NextTrainingIndex);
    }

    private static UraCareerSessionState PendingTrainingState() => new()
    {
        TurnIndex = 0,
        TurnIndexSource = UraStateSource.Observed,
        PendingTurnAction = UraPlannedAction.Training,
        TrainingTurnCommitPending = true,
        TrainingTurnCommitType = "speed",
        TrainingTurnCommitTurnIndex = 0,
    };

    private static CareerSessionState<UraCareerSessionState> Session(
        UraCareerSessionState state) => new()
        {
            Runtime = state.Runtime,
            Scenario = state,
        };
}
