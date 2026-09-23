namespace UmamusumeWpfGui.Services.Training;

internal static class CareerRestReturnGate
{
    public static bool HasReachedNextTurn(
        UraCareerSessionState state,
        CareerObservation observation)
    {
        if (!state.AwaitingRestReturn)
            return true;

        if (!observation.ScreenId.Equals("career_main", StringComparison.OrdinalIgnoreCase)
            || !UraTurnPositionParser.TryParse(observation.TurnPositionText, out var position)
            || position.TurnIndex <= state.RestStartedTurnIndex
            || observation.EnergyPercent is not int energy
            || state.RestStartedEnergyPercent is not int previousEnergy
            || energy <= previousEnergy)
        {
            return false;
        }

        state.AwaitingRestReturn = false;
        return true;
    }
}
