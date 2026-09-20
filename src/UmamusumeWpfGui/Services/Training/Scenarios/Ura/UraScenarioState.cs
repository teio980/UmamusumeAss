namespace UmamusumeWpfGui.Services.Training.Scenarios.Ura;

/// <summary>
/// Scenario-owned URA state reserved for the generic career session.
/// </summary>
public sealed class UraScenarioState
{
    public string? CurrentObjectiveId { get; set; }
    public string? CurrentRaceId { get; set; }
    public int FinaleStageIndex { get; set; }
    public Dictionary<string, int> RacePlacements { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public int RetryCount { get; set; }
}
