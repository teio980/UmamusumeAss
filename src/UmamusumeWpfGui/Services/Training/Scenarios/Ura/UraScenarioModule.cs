using System.IO;
using UmamusumeWpfGui.Models;

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
    private readonly UmaTraineeRecord? _trainee;
    private readonly bool _useTraineeObjectives;
    private readonly Dictionary<string, UmaCareerRaceRecord> _careerRaces;
    private readonly Dictionary<string, UraObjectiveDefinition> _traineeObjectives;

    public UraScenarioModule(
        UraScenarioPack pack,
        UmaTraineeRecord? trainee = null,
        IEnumerable<UmaCareerRaceRecord>? careerRaces = null,
        bool useTraineeObjectives = true)
    {
        _pack = pack ?? throw new ArgumentNullException(nameof(pack));
        _trainee = trainee;
        _useTraineeObjectives = useTraineeObjectives;
        _careerRaces = (careerRaces ?? [])
            .ToDictionary(
                item => item.RaceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparer.OrdinalIgnoreCase);
        _traineeObjectives = useTraineeObjectives
            ? BuildTraineeObjectives(trainee)
            : new Dictionary<string, UraObjectiveDefinition>(StringComparer.OrdinalIgnoreCase);
    }

    public UraCareerSessionState CreateInitialState()
    {
        var firstObjective = _traineeObjectives.Values
            .OrderBy(item => item.Order)
            .FirstOrDefault()
            ?? _pack.Objectives.Objectives.FirstOrDefault()
            ?? throw new InvalidDataException("URA objective chain is empty.");
        return new UraCareerSessionState
        {
            ScenarioId = _pack.Manifest.ScenarioId,
            TraineeId = _trainee?.TraineeId,
            PhaseId = _pack.Definition.Phases
                .OrderBy(item => item.Order)
                .FirstOrDefault()?.PhaseId ?? "career",
            CurrentObjectiveId = firstObjective.ObjectiveId,
            CurrentRaceId = firstObjective.RaceId,
        };
    }

    public UraObjectiveDefinition? CurrentObjective(UraCareerSessionState state) =>
        FindObjective(state.CurrentObjectiveId);

    public UraRaceDefinition? CurrentRace(UraCareerSessionState state)
    {
        var objective = CurrentObjective(state);
        var raceId = state.CurrentRaceId
            ?? objective?.RaceId
            ?? (objective?.Kind.Equals("race_result_count", StringComparison.OrdinalIgnoreCase) == true
                ? objective.ObservedRaceIds.FirstOrDefault(item => !state.RacePlacements.ContainsKey(item))
                : null)
            ?? (_traineeObjectives.ContainsKey(state.CurrentObjectiveId)
                ? null
                : FindNextRaceObjectiveId(objective));
        if (raceId is null && state.PhaseId.Equals("finale_underway", StringComparison.OrdinalIgnoreCase))
            raceId = state.CurrentObjectiveId;
        if (raceId is null)
            return null;

        var scenarioRace = _pack.Races.Find(raceId);
        if (scenarioRace is not null)
            return scenarioRace;

        if (_careerRaces.TryGetValue(raceId, out var careerRace))
        {
            return new UraRaceDefinition
            {
                RaceId = raceId,
                Name = careerRace.NameEn,
                Grade = careerRace.Grade,
                Course = new UraRaceCourse
                {
                    Surface = careerRace.Surface,
                    Distance = careerRace.Distance,
                    DistanceBand = careerRace.DistanceBand,
                },
                RewardFans = careerRace.FansGained,
            };
        }

        // Keep the target ID visible even when the global race catalog is not
        // available. Result recognition still fails closed because this
        // synthetic race has no observed capture asset.
        return _traineeObjectives.ContainsKey(state.CurrentObjectiveId)
            ? new UraRaceDefinition { RaceId = raceId, Name = raceId }
            : null;
    }

    private string? FindNextRaceObjectiveId(UraObjectiveDefinition? objective)
    {
        var next = objective?.NextObjectiveId;
        while (next is not null)
        {
            var candidate = FindObjective(next);
            if (candidate is null)
                return null;
            if (!string.IsNullOrWhiteSpace(candidate.RaceId))
                return candidate.RaceId;
            next = candidate.NextObjectiveId;
        }

        return null;
    }

    private UraObjectiveDefinition? FindObjective(string objectiveId) =>
        _traineeObjectives.TryGetValue(objectiveId, out var traineeObjective)
            ? traineeObjective
            : _pack.Objectives.Find(objectiveId);

    private void UpdateTraineeObjective(UraCareerSessionState state)
    {
        if (_traineeObjectives.Count == 0
            || !state.PhaseId.Equals("career", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // A failed target race may advance the observed turn label while the
        // same race remains retryable. Do not let the schedule-based lookup
        // skip that still-pending objective.
        if (state.HasPendingRace
            && _traineeObjectives.ContainsKey(state.CurrentObjectiveId))
        {
            return;
        }

        var objective = _traineeObjectives.Values
            .OrderBy(item => item.Order)
            .FirstOrDefault(item => item.Turn >= state.TurnIndex);
        if (objective is null)
        {
            state.PhaseId = "finale_underway";
            state.FinaleStageIndex = 0;
            state.CurrentObjectiveId = _pack.Definition.FinalSeries.Stages[0];
            state.CurrentRaceId = CurrentRace(state)?.RaceId;
            state.HasPendingRace = true;
            return;
        }

        state.TraineeId = _trainee?.TraineeId;
        state.CurrentObjectiveId = objective.ObjectiveId;
        state.CurrentRaceId = objective.RaceId;
        state.HasPendingRace = objective.RaceId is not null
            && state.TurnIndex >= objective.Turn;
    }

    private static Dictionary<string, UraObjectiveDefinition> BuildTraineeObjectives(
        UmaTraineeRecord? trainee)
    {
        if (trainee is null || trainee.CareerObjectives.Count == 0)
        {
            return new Dictionary<string, UraObjectiveDefinition>(
                StringComparer.OrdinalIgnoreCase);
        }

        var ordered = trainee.CareerObjectives
            .OrderBy(item => item.Order)
            .ToArray();
        var result = new Dictionary<string, UraObjectiveDefinition>(
            StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < ordered.Length; index++)
        {
            var objective = ordered[index];
            var nextObjectiveId = index + 1 < ordered.Length
                ? ordered[index + 1].ObjectiveId
                : "career_goals_complete";
            result[objective.ObjectiveId] = new UraObjectiveDefinition
            {
                ObjectiveId = objective.ObjectiveId,
                Order = objective.Order,
                Turn = objective.Turn,
                Kind = objective.Kind,
                RaceId = objective.RaceIds.FirstOrDefault(),
                ObservedRaceIds = objective.RaceIds.ToList(),
                NextObjectiveId = nextObjectiveId,
                Target = new UraObjectiveTarget
                {
                    PlacementAtMost = objective.Target.PlacementAtMost,
                    Minimum = objective.Target.Minimum,
                },
            };
        }

        return result;
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
        string? turnPositionText = null,
        int? turnsToGoal = null,
        string? goalText = null,
        int? fansToGoal = null)
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

            state.TurnsToGoal = turnsToGoal;
            state.FansToGoal = fansToGoal ?? CareerGoalTextParser.ParseFansToGo(goalText);
            state.ObservedGoalText = goalText;
            state.ObservedGoalKind = CareerGoalTextParser.Classify(goalText);

            if (_useTraineeObjectives)
            {
                UpdateTraineeObjective(state);
            }
            else
            {
                // The first direct-OCR integration deliberately does not
                // infer target races from the downloaded career database.
                // Direct OCR mode uses only the visible objective. A fan goal
                // needs an optional race while fans remain; no database race
                // ID is inferred for that choice.
                state.HasPendingRace = string.Equals(
                        state.ObservedGoalKind,
                        CareerGoalTextParser.Race,
                        StringComparison.OrdinalIgnoreCase)
                    && turnsToGoal is <= 0;
                if (string.Equals(
                        state.ObservedGoalKind,
                        CareerGoalTextParser.Fans,
                        StringComparison.OrdinalIgnoreCase))
                {
                    state.HasPendingRace = state.FansToGoal is > 0;
                }
                state.CurrentRaceId = null;
            }
        }

        if (string.Equals(screenId, "race_day", StringComparison.OrdinalIgnoreCase)
            || string.Equals(screenId, "race_list", StringComparison.OrdinalIgnoreCase)
            || string.Equals(screenId, "race_runner", StringComparison.OrdinalIgnoreCase)
            || string.Equals(screenId, "race_details", StringComparison.OrdinalIgnoreCase)
            || string.Equals(screenId, "race_attributes", StringComparison.OrdinalIgnoreCase))
        {
            state.HasPendingRace = true;
            state.CurrentRaceId = string.Equals(
                    state.ObservedGoalKind,
                    CareerGoalTextParser.Fans,
                    StringComparison.OrdinalIgnoreCase)
                ? null
                : CurrentRace(state)?.RaceId;
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
