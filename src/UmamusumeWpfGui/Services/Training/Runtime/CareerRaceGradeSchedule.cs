namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Resolves the first available Race List card for each Career turn from the
/// shipped race calendar. The shared fan-race action selects that card.
/// </summary>
public sealed class CareerRaceGradeSchedule
{
    private readonly Dictionary<int, string> _firstCardGrades;

    public CareerRaceGradeSchedule(IEnumerable<IndependentTrainingRace> races)
    {
        ArgumentNullException.ThrowIfNull(races);
        _firstCardGrades = races
            .Where(race => race.IsGameAvailable && race.GameOrder == 0)
            .Select(race => (Index: TryGetTurnIndex(race.Year, race.Turn), race.Grade))
            .Where(item => item.Index is not null && !string.IsNullOrWhiteSpace(item.Grade))
            .GroupBy(item => item.Index!.Value)
            .ToDictionary(group => group.Key, group => group.First().Grade,
                EqualityComparer<int>.Default);
    }

    public bool HasData => _firstCardGrades.Count > 0;

    public string? FirstCardGrade(int turnIndex) =>
        _firstCardGrades.TryGetValue(turnIndex, out var grade) ? grade : null;

    public bool HasFirstCardGrade(int turnIndex, string? grade) =>
        !string.IsNullOrWhiteSpace(grade)
        && _firstCardGrades.TryGetValue(turnIndex, out var firstGrade)
        && firstGrade.Equals(grade, StringComparison.OrdinalIgnoreCase);

    private static int? TryGetTurnIndex(string year, string turn)
    {
        var yearOffset = year.Trim().ToLowerInvariant() switch
        {
            "first year" => 0,
            "second year" => 24,
            "third year" => 48,
            _ => -1,
        };
        var parts = turn.Split('_');
        if (yearOffset < 0
            || parts.Length != 2
            || !int.TryParse(parts[0], out var month)
            || !int.TryParse(parts[1], out var half)
            || month is < 1 or > 12
            || half is < 1 or > 2)
        {
            return null;
        }

        return yearOffset + (month - 1) * 2 + half;
    }
}
