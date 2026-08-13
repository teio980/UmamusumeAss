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
}
