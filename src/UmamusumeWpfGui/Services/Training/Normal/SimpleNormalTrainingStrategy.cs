using System.IO;

namespace UmamusumeWpfGui.Services.Training;

public sealed record UraTrainingStrategyOption(string Value, string Label);

public static class UraTrainingTypeCatalog
{
    private static readonly string[] TrainingTypes =
    [
        "speed",
        "stamina",
        "power",
        "guts",
        "wit",
    ];

    public static IReadOnlyList<string> SupportedTypes => TrainingTypes;

    public static bool TryNormalize(string? trainingType, out string normalized)
    {
        normalized = trainingType?.Trim().ToLowerInvariant() ?? string.Empty;
        return TrainingTypes.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    public static bool TryGetSemanticAction(
        string? trainingType,
        out string semanticAction,
        out string normalized)
    {
        if (!TryNormalize(trainingType, out normalized))
        {
            semanticAction = string.Empty;
            return false;
        }

        semanticAction = "training." + normalized;
        return true;
    }
}

public sealed class UraDefaultStrategy
{
    private readonly string _trainingType;

    public UraDefaultStrategy(int restThreshold)
        : this("speed", restThreshold)
    {
    }

    public UraDefaultStrategy(
        string trainingType = "speed",
        int restThreshold = 50)
    {
        if (!UraTrainingTypeCatalog.TryNormalize(trainingType, out _trainingType))
        {
            throw new ArgumentException(
                $"Unsupported URA training type '{trainingType}'.",
                nameof(trainingType));
        }

        RestThreshold = Math.Clamp(restThreshold, 0, 100);
    }

    public int RestThreshold { get; }
    public string TrainingType => _trainingType;

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

        if (state.HasPendingRace)
        {
            var action = state.PhaseId.Equals("finale_underway", StringComparison.OrdinalIgnoreCase)
                ? UraPlannedAction.FinaleRace
                : UraPlannedAction.Race;
            var reason = string.Equals(
                    state.ObservedGoalKind,
                    CareerGoalTextParser.Fans,
                    StringComparison.OrdinalIgnoreCase)
                ? $"The visible goal still needs {state.FansToGoal?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} fans; opening the race list to choose a recommendation."
                : $"Required race '{state.CurrentRaceId ?? "unknown"}' is pending.";
            return new(
                action,
                state.CurrentRaceId,
                reason,
                true,
                [UraPlannedAction.Rest]);
        }

        if (state.Energy.Value is int energy && energy < RestThreshold)
        {
            return new(
                UraPlannedAction.Rest,
                null,
                $"Observed energy {energy}% is below the safety threshold {RestThreshold}%.",
                false,
                [UraPlannedAction.Training]);
        }

        return new(
            UraPlannedAction.Training,
            TrainingType,
            $"No required race is pending; strategy selected {TrainingType} training.",
            false,
            [UraPlannedAction.Rest]);
    }
}

public static class UraStrategyRegistry
{
    private static readonly IReadOnlyList<UraTrainingStrategyOption> Options =
    [
        new("default-speed-medium", "Speed focus"),
        new("default-stamina-medium", "Stamina focus"),
        new("default-power-medium", "Power focus"),
        new("default-guts-medium", "Guts focus"),
        new("default-wit-medium", "Wit focus"),
    ];

    public static IReadOnlyList<UraTrainingStrategyOption> AvailableStrategies => Options;

    public static bool IsRegistered(string? strategyId) =>
        Options.Any(item => string.Equals(
            item.Value,
            strategyId?.Trim(),
            StringComparison.OrdinalIgnoreCase));

    public static UraDefaultStrategy Create(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        return strategyId.Trim().ToLowerInvariant() switch
        {
            "default-speed-medium" => new UraDefaultStrategy("speed"),
            "default-stamina-medium" => new UraDefaultStrategy("stamina"),
            "default-power-medium" => new UraDefaultStrategy("power"),
            "default-guts-medium" => new UraDefaultStrategy("guts"),
            "default-wit-medium" => new UraDefaultStrategy("wit"),
            _ => throw new InvalidDataException(
                $"Normal training strategy '{strategyId}' is not registered for this build."),
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
