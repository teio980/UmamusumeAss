namespace UmamusumeWpfGui.Services.Training;

internal static class CareerRaceStreakPolicy
{
    public const int MaximumOptionalRaceStreak = 2;

    public static void InitializeForRun(
        UraCareerSessionState state,
        bool continueExistingCareer)
    {
        state.ConsecutiveRaceTurns = continueExistingCareer
            ? MaximumOptionalRaceStreak
            : 0;
        state.PendingTurnAction = null;
        state.PendingActionTurnIndex = null;
        state.PendingActionTurnsToGoal = null;
    }

    public static void BeginTurnAction(UraCareerSessionState state, UraPlannedAction action)
    {
        if (action is not (UraPlannedAction.Race or UraPlannedAction.FinaleRace
            or UraPlannedAction.Training or UraPlannedAction.Rest
            or UraPlannedAction.Recreation or UraPlannedAction.Infirmary))
            return;

        state.PendingTurnAction = action;
        state.PendingActionTurnIndex = state.TurnIndex;
        state.PendingActionTurnsToGoal = state.TurnsToGoal;
    }

    public static void ConfirmTurnAdvance(
        UraCareerSessionState state,
        int observedTurnIndex,
        int? observedTurnsToGoal = null,
        bool isPreDebut = false)
    {
        if (state.PendingActionTurnIndex is not int actionTurnIndex
            || state.PendingTurnAction is not UraPlannedAction action)
            return;

        // The Pre-Debut label maps every turn to index 0. Confirm a completed
        // non-race action there using its visible one-turn countdown instead.
        var preDebutTurnAdvanced = isPreDebut
            && actionTurnIndex == 0
            && observedTurnIndex == 0
            && action is not (UraPlannedAction.Race or UraPlannedAction.FinaleRace)
            && state.PendingActionTurnsToGoal is > 0
            && observedTurnsToGoal == state.PendingActionTurnsToGoal - 1;
        if (observedTurnIndex <= actionTurnIndex && !preDebutTurnAdvanced)
            return;

        state.ConsecutiveRaceTurns = action is UraPlannedAction.Race
            or UraPlannedAction.FinaleRace
            ? Math.Min(MaximumOptionalRaceStreak, state.ConsecutiveRaceTurns + 1)
            : 0;
        state.PendingTurnAction = null;
        state.PendingActionTurnIndex = null;
        state.PendingActionTurnsToGoal = null;
    }

    public static bool WouldBeThirdRace(UraCareerSessionState state) =>
        state.ConsecutiveRaceTurns >= MaximumOptionalRaceStreak
        || ((state.PendingTurnAction is UraPlannedAction.Race
                or UraPlannedAction.FinaleRace)
            && (state.RaceReplayFlowCompleted
                || state.PendingActionTurnIndex is int actionTurn
                    && state.TurnIndex > actionTurn));

    public static bool ShouldDeferRace(
        UraScenarioModule scenario,
        UraCareerSessionState state) =>
        state.HasPendingRace
        && WouldBeThirdRace(state)
        && !MustRaceNow(scenario, state);

    public static bool MustRaceNow(
        UraScenarioModule scenario,
        UraCareerSessionState state)
    {
        if (state.IsFinale || state.ObservedGoalKind == CareerGoalTextParser.Race)
            return true;

        if (state.ObservedGoalKind == CareerGoalTextParser.Fans)
            return state.TurnsToGoal is <= 1;

        if (state.ObservedGoalKind != CareerGoalTextParser.GradeRaceCount)
            return false;

        if (state.TurnsToGoal is <= 1)
            return true;

        var laterRaceTurns = scenario.CountLaterQualifyingRaceTurns(state);
        return laterRaceTurns is int later
            && state.GradeRaceTimesLeft is int remaining
            && later < remaining;
    }
}
