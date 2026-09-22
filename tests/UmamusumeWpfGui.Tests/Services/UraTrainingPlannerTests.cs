using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraTrainingPlannerTests
{
    [Fact]
    public void Decide_RestWinsWhenEnergyIsLow()
    {
        var decision = UraTrainingPlanner.Decide(new UraPlannerInput(
            IsFinale: false,
            HasPendingRace: false,
            HasScenarioEvent: false,
            Energy: 20));

        Assert.Equal(UraPlannedAction.Rest, decision.Action);
    }

    [Fact]
    public void Decide_Uses_a_strictly_below_fifty_percent_threshold()
    {
        var atThreshold = UraTrainingPlanner.Decide(new UraPlannerInput(
            IsFinale: false,
            HasPendingRace: false,
            HasScenarioEvent: false,
            Energy: 50));
        var belowThreshold = UraTrainingPlanner.Decide(new UraPlannerInput(
            IsFinale: false,
            HasPendingRace: false,
            HasScenarioEvent: false,
            Energy: 49));

        Assert.Equal(UraPlannedAction.Training, atThreshold.Action);
        Assert.Equal(UraPlannedAction.Rest, belowThreshold.Action);
    }

    [Fact]
    public void Decide_FinalRaceWinsOverTraining()
    {
        var decision = UraTrainingPlanner.Decide(new UraPlannerInput(
            IsFinale: true,
            HasPendingRace: false,
            HasScenarioEvent: false,
            Energy: 100));

        Assert.Equal(UraPlannedAction.FinaleRace, decision.Action);
    }

    [Fact]
    public void Decide_RequiredRaceWinsOverTraining()
    {
        var decision = UraTrainingPlanner.Decide(new UraPlannerInput(
            IsFinale: false,
            HasPendingRace: true,
            HasScenarioEvent: false,
            Energy: 100));

        Assert.Equal(UraPlannedAction.Race, decision.Action);
    }

    [Fact]
    public void PredictedEventDoesNotForceAnEventAction()
    {
        var decision = UraTrainingPlanner.Decide(new UraPlannerInput(
            IsFinale: false,
            HasPendingRace: false,
            HasScenarioEvent: true,
            Energy: 100));

        Assert.Equal(UraPlannedAction.Training, decision.Action);
    }
}
