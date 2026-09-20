using System.IO;

namespace UmamusumeWpfGui.Services.Training;

public sealed class UraDefaultStrategy
{
    public UraDefaultStrategy(int restThreshold = 35)
    {
        RestThreshold = Math.Clamp(restThreshold, 0, 100);
    }

    public int RestThreshold { get; }

    public UraActionIntent ChooseTurnAction(
        UraScenarioModule module,
        UraCareerSessionState state)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(state);

        if (state.IsCompleted)
        {
            return new(
                UraPlannedAction.Complete,
                null,
                "The scenario objective chain is complete.",
                false,
                []);
        }

        if (state.HasScenarioEvent)
        {
            return new(
                UraPlannedAction.ScenarioEvent,
                null,
                "A scenario event is pending and must be resolved before the next turn.",
                false,
                [UraPlannedAction.Rest]);
        }

        if (state.HasPendingRace)
        {
            var action = state.PhaseId.Equals("finale_underway", StringComparison.OrdinalIgnoreCase)
                ? UraPlannedAction.FinaleRace
                : UraPlannedAction.Race;
            return new(
                action,
                state.CurrentRaceId,
                $"Required race '{state.CurrentRaceId ?? "unknown"}' is pending.",
                true,
                [UraPlannedAction.Rest]);
        }

        if (state.Energy.Value is int energy && energy <= RestThreshold)
        {
            return new(
                UraPlannedAction.Rest,
                null,
                $"Observed energy {energy} is at or below the safety threshold {RestThreshold}.",
                false,
                [UraPlannedAction.Training]);
        }

        return new(
            UraPlannedAction.Training,
            null,
            "No required race or event is pending; choose a strategy training action.",
            false,
            [UraPlannedAction.Rest]);
    }
}

public static class UraStrategyRegistry
{
    public static bool IsRegistered(string? strategyId) =>
        string.Equals(
            strategyId?.Trim(),
            "default-speed-medium",
            StringComparison.OrdinalIgnoreCase);

    public static UraDefaultStrategy Create(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        return strategyId.Trim().ToLowerInvariant() switch
        {
            "default-speed-medium" => new UraDefaultStrategy(),
            _ => throw new InvalidDataException(
                $"URA strategy '{strategyId}' is not registered for this build."),
        };
    }
}

public sealed class UraUnknownOutcomeException : InvalidOperationException
{
    public UraUnknownOutcomeException(string message)
        : base(message)
    {
    }
}
