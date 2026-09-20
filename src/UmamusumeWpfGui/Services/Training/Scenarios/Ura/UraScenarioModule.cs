using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Services.Training.Scenarios.Ura;

/// <summary>
/// Skeleton for the URA scenario module. It owns scenario constraints and
/// progression; page actions remain in the shared runtime layers.
/// </summary>
public sealed class UraScenarioModule : ICareerScenarioModule<UraScenarioState>
{
    public string ScenarioId => "ura";

    public UraScenarioState CreateInitialState() => new();

    public void Observe(
        CareerSessionState<UraScenarioState> session,
        CareerObservation observation)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(observation);

        throw new NotImplementedException(
            "URA scenario observation is intentionally not implemented in the skeleton.");
    }
}
