using System.Text.RegularExpressions;

namespace UmamusumeWpfGui.Services.Training;

public sealed record UraTurnPosition(
    string Label,
    int TurnIndex,
    string Year,
    string Phase,
    int? Month);

/// <summary>
/// Parses the stable career-position label shown below the Career header.
/// This is deliberately text based: the background and character artwork
/// change every turn, while the year/month label has a stable vocabulary.
/// </summary>
public static partial class UraTurnPositionParser
{
    public static UraCalendarStage GetCalendarStage(UraTurnPosition position) =>
        (position.Year is "classic" or "senior")
        && (position.Month is 7 or 8)
            ? UraCalendarStage.SummerCamp
            : UraCalendarStage.Regular;

    private static readonly Dictionary<string, int> YearOffsets =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["junior"] = 0,
            ["classic"] = 24,
            ["senior"] = 48,
        };

    private static readonly Dictionary<string, int> MonthNumbers =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["jan"] = 1, ["january"] = 1,
            ["feb"] = 2, ["february"] = 2,
            ["mar"] = 3, ["march"] = 3,
            ["apr"] = 4, ["april"] = 4,
            ["may"] = 5,
            ["jun"] = 6, ["june"] = 6,
            ["jul"] = 7, ["july"] = 7,
            ["aug"] = 8, ["august"] = 8,
            ["sep"] = 9, ["sept"] = 9, ["september"] = 9,
            ["oct"] = 10, ["october"] = 10,
            ["nov"] = 11, ["november"] = 11,
            ["dec"] = 12, ["december"] = 12,
        };

    public static bool TryParse(string? text, out UraTurnPosition position)
    {
        position = null!;
        var normalized = Normalize(text);
        if (normalized.Length == 0)
            return false;

        if (PreDebutPattern().IsMatch(normalized))
        {
            position = new UraTurnPosition(
                "Junior Year Pre-Debut",
                0,
                "junior",
                "pre-debut",
                null);
            return true;
        }

        var match = DatedPattern().Match(normalized);
        if (!match.Success)
            return false;

        var year = match.Groups["year"].Value.ToLowerInvariant();
        var phase = match.Groups["phase"].Value.ToLowerInvariant();
        var monthToken = match.Groups["month"].Value;
        if (!YearOffsets.TryGetValue(year, out var yearOffset)
            || !MonthNumbers.TryGetValue(monthToken, out var month))
        {
            return false;
        }

        var phaseOffset = phase.Equals("late", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var turnIndex = 1 + yearOffset + ((month - 1) * 2) + phaseOffset;
        var label = $"{char.ToUpperInvariant(year[0])}{year[1..]} Year "
            + $"{char.ToUpperInvariant(phase[0])}{phase[1..]} {monthToken[..3]}";
        position = new UraTurnPosition(label, turnIndex, year, phase, month);
        return true;
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text
            .Replace('–', '-')
            .Replace('—', '-')
            .Replace('’', '\'');
        normalized = CommonOcrTypos().Replace(normalized, "Jul");
        normalized = NonLetters().Replace(normalized, " ");
        normalized = Whitespace().Replace(normalized, " ").Trim();
        return normalized;
    }

    [GeneratedRegex(@"\bJunior\s+Year\s+Pre\s*Debut\b", RegexOptions.IgnoreCase)]
    private static partial Regex PreDebutPattern();

    [GeneratedRegex(
        @"\b(?<year>Junior|Classic|Senior)\s+Year\s+(?<phase>Early|Late)\s+(?<month>[A-Za-z]+)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex DatedPattern();

    [GeneratedRegex(@"\b(?:Jui|Iul|Juiy)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CommonOcrTypos();

    [GeneratedRegex(@"[^A-Za-z]+")]
    private static partial Regex NonLetters();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
