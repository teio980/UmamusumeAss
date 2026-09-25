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
    private readonly Dictionary<int, string> _raceGradesByCode;
    private readonly Dictionary<string, int> _raceCodesByGrade;
    private readonly CareerRaceGradeSchedule? _raceGradeSchedule;

    public UraScenarioModule(
        UraScenarioPack pack,
        UmaTraineeRecord? trainee = null,
        IEnumerable<UmaCareerRaceRecord>? careerRaces = null,
        bool useTraineeObjectives = true,
        CareerRaceGradeSchedule? raceGradeSchedule = null)
    {
        _pack = pack ?? throw new ArgumentNullException(nameof(pack));
        _trainee = trainee;
        _useTraineeObjectives = useTraineeObjectives;
        _raceGradeSchedule = raceGradeSchedule;
        var races = (careerRaces ?? []).ToArray();
        _careerRaces = races
            .ToDictionary(
                item => item.RaceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparer.OrdinalIgnoreCase);
        _raceGradesByCode = races
            .Where(item => item.GradeCode > 0 && !string.IsNullOrWhiteSpace(item.Grade))
            .GroupBy(item => item.GradeCode)
            .ToDictionary(group => group.Key, group => group.First().Grade);
        _raceCodesByGrade = races
            .Where(item => item.GradeCode > 0 && !string.IsNullOrWhiteSpace(item.Grade))
            .GroupBy(item => item.Grade, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().GradeCode,
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

    internal bool HasRaceGradeScheduleData => _raceGradeSchedule?.HasData == true;

    private UmaCareerObjectiveRecord? ResolveCountObjective(
        int turnIndex,
        int? turnsToGoal,
        int? racesLeft)
    {
        if (_trainee is null || turnsToGoal is null or < 0 || racesLeft is null or < 0)
            return null;

        var deadlineTurn = turnIndex + turnsToGoal.Value - 1;
        var matches = _trainee.CareerObjectives
            .Where(objective => objective.Kind.Equals("condition", StringComparison.OrdinalIgnoreCase)
                && objective.Turn == deadlineTurn
                && objective.Condition.Type == 2
                && objective.Condition.Id > 0
                && objective.Condition.Value2 > 0
                && objective.Condition.Value2 >= racesLeft)
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private bool HasQualifyingFirstCard(int turnIndex, int? requiredGradeCode)
    {
        if (requiredGradeCode is not int requiredCode)
            return false;
        var firstGrade = _raceGradeSchedule?.FirstCardGrade(turnIndex);
        return firstGrade is not null
            && _raceCodesByGrade.TryGetValue(firstGrade, out var firstCode)
            && firstCode <= requiredCode;
    }

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
            // Preserve a race-armed probe across the interim main page so
            // post-race events and the Goal Achieved page can run in order.
            if (state.LastAction != UraPlannedAction.Race
                || !state.GoalCompletionProbeArmed)
            {
                state.GoalCompletionProbePending = false;
                state.GoalCompletionProbeArmed = false;
            }
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
                state.CalendarStage = UraTurnPositionParser.GetCalendarStage(turnPosition);
                state.TurnIndexSource = UraStateSource.Observed;
                state.TurnIndexConfidence = Math.Clamp(confidence, 0, 1);
            }
            else
            {
                state.TurnIndexSource = UraStateSource.Unknown;
                state.CalendarStage = UraCalendarStage.Regular;
            }

            state.TurnsToGoal = turnsToGoal;
            state.FansToGoal = fansToGoal ?? CareerGoalTextParser.ParseFansToGo(goalText);
            state.GradeRaceTimesLeft = CareerGoalTextParser.ParseRaceCountLeft(goalText);
            state.ObservedGoalText = goalText;
            state.ObservedGoalKind = CareerGoalTextParser.Classify(goalText);
            var countObjective = state.TurnIndexSource == UraStateSource.Observed
                ? ResolveCountObjective(
                    state.TurnIndex,
                    turnsToGoal,
                    state.GradeRaceTimesLeft)
                : null;
            state.TargetRaceGradeCode = countObjective?.Condition.Id;
            state.TargetRaceGrade = state.TargetRaceGradeCode is int code
                && _raceGradesByCode.TryGetValue(code, out var grade)
                ? grade
                : null;
            if (countObjective is not null)
                state.ObservedGoalKind = CareerGoalTextParser.GradeRaceCount;
            if (state.GoalCompletionProbeArmed
                && state.LastAction == UraPlannedAction.Race
                && ((state.ObservedGoalKind != CareerGoalTextParser.GradeRaceCount
                        && state.ObservedGoalKind != CareerGoalTextParser.Unknown)
                    || (state.ObservedGoalKind == CareerGoalTextParser.GradeRaceCount
                        && state.GradeRaceTimesLeft is > 0
                        && state.TurnIndex > state.GradeRaceStartedTurnIndex)))
            {
                // A qualifying result changes the visible count to zero.
                // If the next turn still shows a positive count, the last
                // race did not satisfy the goal and another qualifying race may be tried.
                state.GoalCompletionProbeArmed = false;
            }

            if (_useTraineeObjectives)
            {
                UpdateTraineeObjective(state);
            }
            else
            {
                // The visible countdown identifies the current database
                // condition. Its grade and the race calendar select a race;
                // the visible progress controls how many results remain.
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
                else if (string.Equals(
                        state.ObservedGoalKind,
                        CareerGoalTextParser.GradeRaceCount,
                        StringComparison.OrdinalIgnoreCase))
                {
                    state.HasPendingRace = state.GradeRaceTimesLeft is > 0
                        && turnsToGoal is > 0
                        && state.TurnIndexSource == UraStateSource.Observed
                        && HasQualifyingFirstCard(
                            state.TurnIndex, state.TargetRaceGradeCode);
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
            if (!string.IsNullOrWhiteSpace(goalText))
            {
                var observedKind = CareerGoalTextParser.Classify(goalText);
                if (observedKind != CareerGoalTextParser.Unknown
                    && state.ObservedGoalKind != CareerGoalTextParser.GradeRaceCount)
                {
                    state.ObservedGoalText = goalText;
                    state.ObservedGoalKind = observedKind;
                }
                state.GradeRaceTimesLeft = CareerGoalTextParser.ParseRaceCountLeft(goalText)
                    ?? state.GradeRaceTimesLeft;
            }
            state.HasPendingRace = true;
            state.CurrentRaceId = string.Equals(
                    state.ObservedGoalKind,
                    CareerGoalTextParser.Fans,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    state.ObservedGoalKind,
                    CareerGoalTextParser.GradeRaceCount,
                    StringComparison.OrdinalIgnoreCase)
                ? null
                : CurrentRace(state)?.RaceId;
        }

        if (string.Equals(screenId, "goal_complete", StringComparison.OrdinalIgnoreCase))
        {
            state.GoalCompletionProbePending = false;
            state.GoalCompletionProbeArmed = false;
            // goal_complete is reserved for the final "All goals achieved!"
            // page. A per-objective GOAL COMPLETE banner is observed as
            // goal_objective_complete and must not start URA Finale.
            state.PhaseId = "finale_underway";
            state.FinaleStageIndex = Math.Max(0, state.FinaleStageIndex);
            state.CurrentObjectiveId = _pack.Definition.FinalSeries.Stages[
                Math.Clamp(state.FinaleStageIndex, 0, _pack.Definition.FinalSeries.Stages.Count - 1)];
            state.CurrentRaceId = CurrentRace(state)?.RaceId;
            state.HasPendingRace = true;
        }

        if (string.Equals(
                screenId,
                "goal_objective_complete",
                StringComparison.OrdinalIgnoreCase))
        {
            // Consume the expensive banner probe as soon as it matches. The
            // following summary/final pages are admitted by LastScreenId.
            state.GoalCompletionProbePending = false;
            state.GoalCompletionProbeArmed = false;
            // The visible Goal Achieved banner confirms the pending race goal;
            // no placement value is needed to release its race checkpoint.
            state.HasPendingRace = false;
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
        // The objective model has confirmed completion, so allow the visual
        // GOAL banner to be recognized after the result flow advances.
        state.GoalCompletionProbePending = false;
        state.GoalCompletionProbeArmed = true;
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
