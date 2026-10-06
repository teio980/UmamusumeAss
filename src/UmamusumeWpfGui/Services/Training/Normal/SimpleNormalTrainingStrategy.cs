using System.IO;

namespace UmamusumeWpfGui.Services.Training;

public sealed record UraTrainingStrategyOption(string Value, string Label);

/// <summary>
/// User-configured relative weights for the five Normal Career trainings.
/// Values remain in the UI's 0-100 domain; <see cref="BuildCycle"/> reduces
/// them by their greatest common divisor before expanding the cycle.
/// </summary>
public sealed record UraTrainingRatio(
    int Speed,
    int Stamina,
    int Power,
    int Guts,
    int Wit)
{
    public static UraTrainingRatio Default { get; } = new(2, 1, 0, 0, 0);

    public bool IsValid =>
        Speed is >= 0 and <= 100
        && Stamina is >= 0 and <= 100
        && Power is >= 0 and <= 100
        && Guts is >= 0 and <= 100
        && Wit is >= 0 and <= 100
        && Speed + Stamina + Power + Guts + Wit > 0;

    public int Total => checked(Speed + Stamina + Power + Guts + Wit);

    public UraTrainingRatio Reduce()
    {
        if (!IsValid)
            throw new ArgumentOutOfRangeException(nameof(UraTrainingRatio),
                "A training ratio must contain five values from 0 to 100 and at least one non-zero value.");

        var divisor = Gcd(Gcd(Gcd(Gcd(Speed, Stamina), Power), Guts), Wit);
        return divisor <= 1
            ? this
            : new(Speed / divisor, Stamina / divisor, Power / divisor, Guts / divisor, Wit / divisor);
    }

    public IReadOnlyList<string> BuildCycle()
    {
        var reduced = Reduce();
        var cycle = new List<string>(reduced.Total);
        Add(cycle, "speed", reduced.Speed);
        Add(cycle, "stamina", reduced.Stamina);
        Add(cycle, "power", reduced.Power);
        Add(cycle, "guts", reduced.Guts);
        Add(cycle, "wit", reduced.Wit);
        return cycle;
    }

    private static void Add(List<string> cycle, string trainingType, int count)
    {
        for (var index = 0; index < count; index++)
            cycle.Add(trainingType);
    }

    private static int Gcd(int left, int right)
    {
        while (right != 0)
        {
            var remainder = left % right;
            left = right;
            right = remainder;
        }

        return Math.Abs(left);
    }
}

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

public class UraDefaultStrategy : ICareerTrainingStrategy<UraCareerSessionState>
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
    public virtual string TrainingType => _trainingType;

    public virtual void ConfirmTraining(
        CareerSessionState<UraCareerSessionState> session,
        string trainingType)
    {
    }

    public UraActionIntent Choose(
        CareerSessionState<UraCareerSessionState> session,
        ICareerScenarioModule<UraCareerSessionState> scenario)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(scenario);
        if (scenario is not UraScenarioModule uraScenario)
        {
            throw new ArgumentException(
                "The URA default strategy requires the URA scenario module.",
                nameof(scenario));
        }

        return ChooseTurnAction(uraScenario, session.Scenario);
    }

    public virtual UraActionIntent ChooseTurnAction(
        UraScenarioModule scenarioModule,
        UraCareerSessionState state)
    {
        ArgumentNullException.ThrowIfNull(scenarioModule);
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

        var deferRace = CareerRaceStreakPolicy.ShouldDeferRace(scenarioModule, state);
        if (state.HasPendingRace && !deferRace)
        {
            var action = state.PhaseId.Equals("finale_underway", StringComparison.OrdinalIgnoreCase)
                ? UraPlannedAction.FinaleRace
                : UraPlannedAction.Race;
            var reason = string.Equals(
                    state.ObservedGoalKind,
                    CareerGoalTextParser.Fans,
                    StringComparison.OrdinalIgnoreCase)
                ? $"The visible goal still needs {state.FansToGoal?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} fans; opening the race list to choose a recommendation."
                : state.ObservedGoalKind == CareerGoalTextParser.GradeRaceCount
                    ? $"A qualifying race is available on {state.TurnPositionLabel}; {state.GradeRaceTimesLeft} result(s) remain."
                : $"Required race '{state.CurrentRaceId ?? "unknown"}' is pending.";
            if (CareerRaceStreakPolicy.WouldBeThirdRace(state))
                reason = "The current goal requires a race despite two consecutive races. " + reason;
            return new(
                action,
                state.CurrentRaceId,
                reason,
                true,
                [UraPlannedAction.Rest]);
        }

        var breakReason = deferRace
            ? "Two consecutive races are confirmed or cannot be ruled out; delaying this race for a non-race turn. "
            : string.Empty;

        if (state.CalendarStage == UraCalendarStage.Regular
            && state.Mood.Value is CareerMood mood
            && mood <= CareerMood.Normal)
        {
            return new(
                UraPlannedAction.Recreation,
                null,
                breakReason + $"Observed mood {mood} is Normal or lower; choose Recreation.",
                false,
                [UraPlannedAction.Rest]);
        }

        if (state.Energy.Value is int energy && energy < RestThreshold)
        {
            return new(
                UraPlannedAction.Rest,
                null,
                breakReason + $"Observed energy {energy}% is below the safety threshold {RestThreshold}%.",
                false,
                [UraPlannedAction.Training]);
        }

        return new(
            UraPlannedAction.Training,
            TrainingType,
            breakReason + $"Strategy selected {TrainingType} training.",
            false,
            [UraPlannedAction.Rest]);
    }
}

/// <summary>
/// Repeats the configured reduced ratio in the fixed speed, stamina, power,
/// guts, wit order. The cursor advances only after the runtime confirms that
/// the submitted training consumed a turn.
/// </summary>
public sealed class UraRatioStrategy : UraDefaultStrategy
{
    private readonly IReadOnlyList<string> _cycle;
    private int _nextTrainingIndex;

    public UraRatioStrategy(UraTrainingRatio ratio, int restThreshold = 50)
        : base("speed", restThreshold)
    {
        Ratio = ratio ?? throw new ArgumentNullException(nameof(ratio));
        _cycle = Ratio.BuildCycle();
    }

    public UraTrainingRatio Ratio { get; }
    public IReadOnlyList<string> Cycle => _cycle;
    public int NextTrainingIndex => _nextTrainingIndex;
    public override string TrainingType => _cycle[_nextTrainingIndex];

    public override UraActionIntent ChooseTurnAction(
        UraScenarioModule scenarioModule,
        UraCareerSessionState state)
    {
        var intent = base.ChooseTurnAction(scenarioModule, state);
        return intent.Action == UraPlannedAction.Training
            ? intent with
            {
                Reason = intent.Reason
                    + $" Ratio position {_nextTrainingIndex + 1}/{_cycle.Count}."
            }
            : intent;
    }

    public override void ConfirmTraining(
        CareerSessionState<UraCareerSessionState> session,
        string trainingType)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!UraTrainingTypeCatalog.TryNormalize(trainingType, out var normalized)
            || !string.Equals(normalized, TrainingType, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _nextTrainingIndex = (_nextTrainingIndex + 1) % _cycle.Count;
    }
}

public static class UraCareerTrainingStrategyExtensions
{
    public static UraActionIntent ChooseTurnAction(
        this ICareerTrainingStrategy<UraCareerSessionState> strategy,
        UraScenarioModule module,
        UraCareerSessionState state)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        return strategy.Choose(
            new CareerSessionState<UraCareerSessionState>
            {
                Runtime = state.Runtime,
                Scenario = state,
            },
            module);
    }
}

public static class UraStrategyRegistry
{
    private static readonly IReadOnlyList<UraTrainingStrategyOption> Options =
    [
        new(UraSmartTrainingStrategy.StrategyId, "Smart balanced · safe"),
        new("default-speed-medium", "Speed focus"),
        new("default-stamina-medium", "Stamina focus"),
        new("default-power-medium", "Power focus"),
        new("default-guts-medium", "Guts focus"),
        new("default-wit-medium", "Wit focus"),
        new("custom-ratio", "Custom training ratio"),
    ];

    public static IReadOnlyList<UraTrainingStrategyOption> AvailableStrategies => Options;

    public static bool IsRegistered(string? strategyId) =>
        Options.Any(item => string.Equals(
            item.Value,
            strategyId?.Trim(),
            StringComparison.OrdinalIgnoreCase));

    public static ICareerTrainingStrategy<UraCareerSessionState> Create(
        string strategyId,
        UraTrainingRatio? ratio = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        return strategyId.Trim().ToLowerInvariant() switch
        {
            UraSmartTrainingStrategy.StrategyId => new UraSmartTrainingStrategy(),
            "default-speed-medium" => new UraDefaultStrategy("speed"),
            "default-stamina-medium" => new UraDefaultStrategy("stamina"),
            "default-power-medium" => new UraDefaultStrategy("power"),
            "default-guts-medium" => new UraDefaultStrategy("guts"),
            "default-wit-medium" => new UraDefaultStrategy("wit"),
            "custom-ratio" => new UraRatioStrategy(ratio ?? UraTrainingRatio.Default),
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
