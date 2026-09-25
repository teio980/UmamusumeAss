namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Maps each Career turn to the grade of the first Race List card selected
/// by the existing race flow.
/// </summary>
public sealed class CareerRaceGradeSchedule
{
    private readonly Dictionary<int, int> _firstCardGradeRanks;

    public CareerRaceGradeSchedule(IEnumerable<IndependentTrainingRace> races)
    {
        ArgumentNullException.ThrowIfNull(races);
        _firstCardGradeRanks = races
            .Where(race => race.IsGameAvailable && race.GameOrder == 0)
            .Select(race => (TurnIndex: TryGetTurnIndex(race.Year, race.Turn),
                GradeRank: GradeRank(race.Grade)))
            .Where(item => item.TurnIndex is not null && item.GradeRank is not null)
            .GroupBy(item => item.TurnIndex!.Value)
            .ToDictionary(group => group.Key, group => group.First().GradeRank!.Value);
    }

    public bool HasData => _firstCardGradeRanks.Count > 0;

    public bool HasQualifyingFirstCard(int turnIndex, string? goalGrade) =>
        GradeRank(goalGrade) is int requiredRank
        && _firstCardGradeRanks.TryGetValue(turnIndex, out var availableRank)
        && availableRank <= requiredRank;

    private static int? GradeRank(string? grade) => grade?.ToUpperInvariant() switch
    {
        "G1" => 1,
        "G2" => 2,
        "G3" => 3,
        _ => null,
    };

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
