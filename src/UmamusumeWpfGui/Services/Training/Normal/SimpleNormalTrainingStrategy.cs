using UmamusumeWpfGui.Services.Training.Scenarios.Ura;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Skeleton for the replaceable first Normal Training strategy.
/// </summary>
public sealed class SimpleNormalTrainingStrategy : ICareerTrainingStrategy<UraScenarioState>
{
    public CareerDecision Choose(CareerSessionState<UraScenarioState> session)
    {
        ArgumentNullException.ThrowIfNull(session);

        throw new NotImplementedException(
            "The documented Normal strategy rules are intentionally not implemented in the skeleton.");
    }
}
