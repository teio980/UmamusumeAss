using System.IO;
using System.Text.Json;

namespace UmamusumeWpfGui.Services.Training;

public enum UraStateSource
{
    Observed,
    Derived,
    Estimated,
    Unknown,
}

public sealed record UraObservedValue<T>(
    T? Value,
    UraStateSource Source,
    double Confidence,
    DateTimeOffset ObservedAt)
    where T : struct
{
}

public static class UraObservedValueFactory
{
    public static UraObservedValue<T> Unknown<T>()
        where T : struct =>
        new(default, UraStateSource.Unknown, 0, DateTimeOffset.UtcNow);

    public static UraObservedValue<T> FromObservation<T>(T value, double confidence)
        where T : struct =>
        new(value, UraStateSource.Observed, Math.Clamp(confidence, 0, 1), DateTimeOffset.UtcNow);

    public static UraObservedValue<T> FromDerived<T>(T value, double confidence)
        where T : struct =>
        new(value, UraStateSource.Derived, Math.Clamp(confidence, 0, 1), DateTimeOffset.UtcNow);
}

public sealed class UraCareerSessionState
{
    public string ScenarioId { get; set; } = "ura";
    public string PhaseId { get; set; } = "career";
    public int TurnIndex { get; set; }
    public string CurrentObjectiveId { get; set; } = "debut_race";
    public int FinaleStageIndex { get; set; } = -1;
    public string? CurrentRaceId { get; set; }
    public int RetryCount { get; set; }
    public bool HasPendingRace { get; set; }
    public bool HasScenarioEvent { get; set; }
    public bool IsCompleted { get; set; }
    public bool CareerEntryOpened { get; set; }
    public UraPlannedAction LastAction { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFinale => PhaseId.Equals("finale_underway", StringComparison.OrdinalIgnoreCase);
    public string LastScreenId { get; set; } = "unknown";
    public UraObservedValue<int> Energy { get; set; } =
        UraObservedValueFactory.FromObservation(100, 0.5);
    public UraObservedValue<int> Fans { get; set; } =
        UraObservedValueFactory.FromObservation(0, 0.2);
    public UraObservedValue<int> LastRacePlacement { get; set; } =
        UraObservedValueFactory.Unknown<int>();
    public Dictionary<string, int> RacePlacements { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<string> CompletedObjectiveIds { get; set; } = [];

    public string Serialize() => JsonSerializer.Serialize(this);

    public static UraCareerSessionState Deserialize(string json) =>
        JsonSerializer.Deserialize<UraCareerSessionState>(json)
        ?? throw new InvalidDataException("URA checkpoint is empty.");
}

public sealed record UraActionIntent(
    UraPlannedAction Action,
    string? TargetId,
    string Reason,
    bool HighRisk,
    IReadOnlyList<UraPlannedAction> FallbackActions);

public sealed class UraScenarioModule
{
    private readonly UraScenarioPack _pack;

    public UraScenarioModule(UraScenarioPack pack)
    {
        _pack = pack ?? throw new ArgumentNullException(nameof(pack));
    }

    public UraCareerSessionState CreateInitialState() => new()
    {
        ScenarioId = _pack.Manifest.ScenarioId,
        PhaseId = _pack.Definition.Phases
            .OrderBy(item => item.Order)
            .FirstOrDefault()?.PhaseId ?? "career",
        CurrentObjectiveId = _pack.Objectives.Objectives.FirstOrDefault()?.ObjectiveId
            ?? throw new InvalidDataException("URA objective chain is empty."),
    };

    public UraObjectiveDefinition? CurrentObjective(UraCareerSessionState state) =>
        _pack.Objectives.Find(state.CurrentObjectiveId);

    public UraRaceDefinition? CurrentRace(UraCareerSessionState state)
    {
        var objective = CurrentObjective(state);
        var raceId = state.CurrentRaceId
            ?? objective?.RaceId
            ?? (objective?.Kind.Equals("race_result_count", StringComparison.OrdinalIgnoreCase) == true
                ? objective.ObservedRaceIds.FirstOrDefault(item => !state.RacePlacements.ContainsKey(item))
                : null)
            ?? FindNextRaceObjectiveId(objective);
        if (raceId is null && state.PhaseId.Equals("finale_underway", StringComparison.OrdinalIgnoreCase))
            raceId = state.CurrentObjectiveId;
        return raceId is null ? null : _pack.Races.Find(raceId);
    }

    private string? FindNextRaceObjectiveId(UraObjectiveDefinition? objective)
    {
        var next = objective?.NextObjectiveId;
        while (next is not null)
        {
            var candidate = _pack.Objectives.Find(next);
            if (candidate is null)
                return null;
            if (!string.IsNullOrWhiteSpace(candidate.RaceId))
                return candidate.RaceId;
            next = candidate.NextObjectiveId;
        }

        return null;
    }

    public IReadOnlyList<UraPlannedAction> GetAvailableActions(
        UraCareerSessionState state,
        string screenId)
    {
        var phase = _pack.Definition.Phases.FirstOrDefault(item =>
            string.Equals(item.PhaseId, state.PhaseId, StringComparison.OrdinalIgnoreCase));
        var actions = phase?.AllowedActions
            .Select(ParseAction)
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .ToList() ?? [];

        if (string.Equals(screenId, "race_day", StringComparison.OrdinalIgnoreCase)
            || string.Equals(screenId, "race_list", StringComparison.OrdinalIgnoreCase)
            || string.Equals(screenId, "race_details", StringComparison.OrdinalIgnoreCase)
            || string.Equals(screenId, "race_attributes", StringComparison.OrdinalIgnoreCase))
        {
            return actions
                .Where(item => item is UraPlannedAction.Race or UraPlannedAction.FinaleRace)
                .ToArray();
        }

        return actions;
    }

    public void ObserveScreen(
        UraCareerSessionState state,
        string screenId,
        double confidence)
    {
        state.LastScreenId = screenId;
        if (string.Equals(screenId, "race_day", StringComparison.OrdinalIgnoreCase)
            || string.Equals(screenId, "race_list", StringComparison.OrdinalIgnoreCase)
            || string.Equals(screenId, "race_details", StringComparison.OrdinalIgnoreCase)
            || string.Equals(screenId, "race_attributes", StringComparison.OrdinalIgnoreCase))
        {
            state.HasPendingRace = true;
            state.CurrentRaceId = CurrentRace(state)?.RaceId;
        }

        if (string.Equals(screenId, "goal_complete", StringComparison.OrdinalIgnoreCase))
        {
            state.PhaseId = "finale_underway";
            state.FinaleStageIndex = Math.Max(0, state.FinaleStageIndex);
            state.CurrentObjectiveId = _pack.Definition.FinalSeries.Stages[
                Math.Clamp(state.FinaleStageIndex, 0, _pack.Definition.FinalSeries.Stages.Count - 1)];
            state.CurrentRaceId = CurrentRace(state)?.RaceId;
            state.HasPendingRace = true;
        }

        if (string.Equals(screenId, "training_result", StringComparison.OrdinalIgnoreCase))
        {
            var current = state.Energy.Value ?? 100;
            state.Energy = UraObservedValueFactory.FromDerived(
                Math.Max(0, current - 20),
                confidence * 0.5);
            state.TurnIndex++;
        }
        else if (string.Equals(screenId, "rest_result", StringComparison.OrdinalIgnoreCase))
        {
            state.Energy = UraObservedValueFactory.FromObservation(100, confidence);
            state.TurnIndex++;
        }
    }

    public void ApplyRaceResult(
        UraCareerSessionState state,
        int placement,
        double confidence)
    {
        var race = CurrentRace(state)
            ?? throw new InvalidDataException("A race result was observed without a current race.");
        state.LastRacePlacement = UraObservedValueFactory.FromObservation(placement, confidence);
        state.RacePlacements[race.RaceId] = placement;
        if (race.RewardFans is int rewardFans)
        {
            state.Fans = UraObservedValueFactory.FromObservation(
                (state.Fans.Value ?? 0) + rewardFans,
                confidence * 0.8);
        }

        var objective = CurrentObjective(state)
            ?? throw new InvalidDataException("A race result was observed without a current objective.");
        if (objective.Kind.Equals("fans", StringComparison.OrdinalIgnoreCase)
            && (state.Fans.Value ?? 0) >= (objective.Target.Minimum ?? int.MaxValue))
        {
            CompleteObjective(state, objective);
            AdvanceNonRaceObjectives(state);
            return;
        }
        if (!IsObjectiveSatisfied(objective, race, state))
        {
            if (objective.Kind.Equals("race_result_count", StringComparison.OrdinalIgnoreCase)
                && objective.ObservedRaceIds.Any(item => !state.RacePlacements.ContainsKey(item)))
            {
                state.CurrentRaceId = objective.ObservedRaceIds
                    .First(item => !state.RacePlacements.ContainsKey(item));
                state.HasPendingRace = true;
                state.TurnIndex++;
                return;
            }

            state.RetryCount++;
            if (!race.RetryPolicy.Enabled
                || state.RetryCount > (race.RetryPolicy.MaxRetryCount ?? 0))
            {
                throw new UraUnknownOutcomeException(
                    $"Race '{race.RaceId}' finished {placement}, but objective '{objective.ObjectiveId}' "
                    + "was not satisfied and no retry is available.");
            }

            state.HasPendingRace = true;
            return;
        }

        state.RetryCount = 0;
        state.HasPendingRace = false;
        if (!state.CompletedObjectiveIds.Contains(objective.ObjectiveId, StringComparer.OrdinalIgnoreCase))
            state.CompletedObjectiveIds.Add(objective.ObjectiveId);

        var next = objective.NextObjectiveId;
        if (state.PhaseId.Equals("finale_underway", StringComparison.OrdinalIgnoreCase))
        {
            state.FinaleStageIndex++;
            if (state.FinaleStageIndex >= _pack.Definition.FinalSeries.Stages.Count)
            {
                state.PhaseId = "finished";
                state.CurrentObjectiveId = "scenario_complete";
                state.CurrentRaceId = null;
                state.IsCompleted = true;
                return;
            }

            next = _pack.Definition.FinalSeries.Stages[state.FinaleStageIndex];
        }

        state.CurrentObjectiveId = next ?? "scenario_complete";
        state.CurrentRaceId = null;
        state.CurrentRaceId = CurrentRace(state)?.RaceId;
        state.HasScenarioEvent = _pack.Events.Events.Any(item =>
            string.Equals(item.Trigger.AfterRaceId, race.RaceId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Trigger.AfterObjectiveId, state.CurrentObjectiveId, StringComparison.OrdinalIgnoreCase));
        AdvanceNonRaceObjectives(state);
    }

    private static void CompleteObjective(
        UraCareerSessionState state,
        UraObjectiveDefinition objective)
    {
        state.HasPendingRace = false;
        if (!state.CompletedObjectiveIds.Contains(objective.ObjectiveId, StringComparer.OrdinalIgnoreCase))
            state.CompletedObjectiveIds.Add(objective.ObjectiveId);
        state.CurrentObjectiveId = objective.NextObjectiveId ?? "scenario_complete";
        state.CurrentRaceId = null;
    }

    private void AdvanceNonRaceObjectives(UraCareerSessionState state)
    {
        while (true)
        {
            var objective = CurrentObjective(state);
            if (objective is null)
                throw new InvalidDataException(
                    $"URA objective '{state.CurrentObjectiveId}' cannot be resolved.");

            if (objective.Kind.Equals("fans", StringComparison.OrdinalIgnoreCase)
                && (state.Fans.Value ?? 0) >= (objective.Target.Minimum ?? int.MaxValue))
            {
                CompleteObjective(state, objective);
                continue;
            }

            if (objective.Kind.Equals("chain_complete", StringComparison.OrdinalIgnoreCase))
            {
                state.PhaseId = "finale_underway";
                state.FinaleStageIndex = 0;
                state.CurrentObjectiveId = _pack.Definition.FinalSeries.Stages[0];
                state.CurrentRaceId = CurrentRace(state)?.RaceId;
                state.HasPendingRace = true;
            }

            state.CurrentRaceId ??= CurrentRace(state)?.RaceId;
            return;
        }
    }

    private static bool IsObjectiveSatisfied(
        UraObjectiveDefinition objective,
        UraRaceDefinition race,
        UraCareerSessionState state)
    {
        if (objective.Target.Placement is int exact && state.LastRacePlacement.Value != exact)
            return false;
        if (objective.Target.PlacementAtMost is int atMost
            && (state.LastRacePlacement.Value is null || state.LastRacePlacement.Value > atMost))
        {
            return false;
        }

        if (objective.Kind.Equals("race_result_count", StringComparison.OrdinalIgnoreCase))
        {
            var count = state.RacePlacements.Count(item =>
                objective.ObservedRaceIds.Contains(item.Key, StringComparer.OrdinalIgnoreCase)
                && item.Value <= (objective.Target.PlacementAtMost ?? int.MaxValue));
            return count >= (objective.Target.Count ?? 0);
        }

        return true;
    }

    private static UraPlannedAction? ParseAction(string action) =>
        action.Trim().ToLowerInvariant() switch
        {
            "training" => UraPlannedAction.Training,
            "rest" => UraPlannedAction.Rest,
            "race" => UraPlannedAction.Race,
            "finale_race" => UraPlannedAction.FinaleRace,
            "scenario_event" => UraPlannedAction.ScenarioEvent,
            _ => null,
        };
}

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
