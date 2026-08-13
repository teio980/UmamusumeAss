namespace UmamusumeWpfGui.Services.Training;

public enum UraPlannedAction
{
    Training,
    Rest,
    Race,
    FinaleRace,
    ScenarioEvent,
    Complete,
    Pause,
}

public sealed record UraPlannerInput(
    bool IsFinale,
    bool HasPendingRace,
    bool HasScenarioEvent,
    int Energy,
    int RestThreshold = 35);

public sealed record UraPlannerDecision(
    UraPlannedAction Action,
    string Reason);

public sealed class UraTrainingPlanner
{
    public static UraPlannerDecision Decide(UraPlannerInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.IsFinale)
        {
            return new(UraPlannedAction.FinaleRace, "URA Finale stage is active.");
        }

        if (input.HasScenarioEvent)
        {
            return new(UraPlannedAction.ScenarioEvent, "A scenario event requires resolution.");
        }

        if (input.HasPendingRace)
        {
            return new(UraPlannedAction.Race, "A required race is ready.");
        }

        if (input.Energy <= input.RestThreshold)
        {
            return new(UraPlannedAction.Rest, "Energy is below the configured safety threshold.");
        }

        return new(UraPlannedAction.Training, "No higher-priority action is pending.");
    }
}
