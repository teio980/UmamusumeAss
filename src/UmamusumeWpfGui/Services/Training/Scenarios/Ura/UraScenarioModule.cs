using System.IO;

namespace UmamusumeWpfGui.Services.Training;

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
        double confidence,
        int? energyPercent = null,
        double energyConfidence = 0,
        string? turnPositionText = null)
    {
        state.LastScreenId = screenId;
        if (string.Equals(screenId, "career_main", StringComparison.OrdinalIgnoreCase))
        {
            state.CareerStarted = true;
            state.Energy = energyPercent is int observedEnergy
                ? UraObservedValueFactory.FromObservation(
                    Math.Clamp(observedEnergy, 0, 100),
                    energyConfidence)
                : UraObservedValueFactory.Unknown<int>();

            if (UraTurnPositionParser.TryParse(turnPositionText, out var turnPosition))
            {
                state.TurnIndex = turnPosition.TurnIndex;
                state.TurnPositionLabel = turnPosition.Label;
                state.TurnIndexSource = UraStateSource.Observed;
                state.TurnIndexConfidence = Math.Clamp(confidence, 0, 1);
            }
        }

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
            AdvanceTurnFromResult(state);
        }
        else if (string.Equals(screenId, "rest_result", StringComparison.OrdinalIgnoreCase))
        {
            if (energyPercent is int observedEnergy)
            {
                state.Energy = UraObservedValueFactory.FromObservation(
                    Math.Clamp(observedEnergy, 0, 100),
                    energyConfidence);
            }
            AdvanceTurnFromResult(state);
        }
    }

    public void ApplyRaceResult(
        UraCareerSessionState state,
        int placement,
        double confidence)
    {
        AdvanceTurnFromResult(state);
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

    private static void AdvanceTurnFromResult(UraCareerSessionState state)
    {
        state.TurnIndex++;
        state.TurnIndexSource = UraStateSource.Derived;
        state.TurnIndexConfidence = 0.5;
    }
}
