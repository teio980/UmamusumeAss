using System.Text.RegularExpressions;

namespace UmamusumeWpfGui.Services.Training;

internal static partial class CareerGoalTextParser
{
    public const string Unknown = "unknown";
    public const string Fans = "fans";
    public const string Race = "race";
    public const string GradeRaceCount = "grade_race_count";

    [GeneratedRegex(@"(?<!\d)(?<turns>\d+)\s*(?:turn|turns)(?:\s*\(s\))?\b", RegexOptions.IgnoreCase)]
    private static partial Regex TurnsLeftRegex();

    // The Career countdown is rendered as a large number on one line and
    // "turn(s) left" on the next. Windows OCR can return only the number for
    // that dedicated ROI, so accept a standalone numeric detection as a safe
    // fallback. The caller uses this parser only for objective.turns_left.
    [GeneratedRegex(@"^\s*(?<turns>\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneTurnsRegex();

    [GeneratedRegex(@"(?<!\d)(?<fans>[\d,]{1,7})\s*fan(?:s)?(?:\s*\(s\))?\s*to\s*go\b", RegexOptions.IgnoreCase)]
    private static partial Regex FansToGoRegex();

    [GeneratedRegex(@"(?<!\d)(?<count>\d+)\s*time(?:\(s\)|s)?\s*left\b", RegexOptions.IgnoreCase)]
    private static partial Regex RaceCountLeftRegex();

    public static int? ParseTurnsLeft(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = TurnsLeftRegex().Match(text);
        if (match.Success
            && int.TryParse(match.Groups["turns"].Value, out var turns))
        {
            return turns;
        }

        var standalone = StandaloneTurnsRegex().Match(text);
        return standalone.Success
            && int.TryParse(standalone.Groups["turns"].Value, out var standaloneTurns)
            ? standaloneTurns
            : null;
    }

    public static int? ParseFansToGo(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = FansToGoRegex().Match(text);
        if (!match.Success)
            return null;

        var digits = match.Groups["fans"].Value.Replace(",", string.Empty, StringComparison.Ordinal);
        return int.TryParse(digits, out var fans)
            ? Math.Clamp(fans, 0, 9_999_999)
            : null;
    }

    public static int? ParseRaceCountLeft(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = RaceCountLeftRegex().Match(text);
        return match.Success && int.TryParse(match.Groups["count"].Value, out var count)
            ? count
            : null;
    }

    public static string Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Unknown;

        var normalized = text.Trim().ToLowerInvariant();
        if (normalized.Contains("fan", StringComparison.Ordinal))
            return Fans;

        if (normalized.Contains("race", StringComparison.Ordinal)
            || normalized.Contains("place", StringComparison.Ordinal)
            || normalized.Contains("win", StringComparison.Ordinal)
            || normalized.Contains("participat", StringComparison.Ordinal)
            || normalized.Contains("g1", StringComparison.Ordinal)
            || normalized.Contains("g2", StringComparison.Ordinal)
            || normalized.Contains("g3", StringComparison.Ordinal)
            || normalized.Contains("pre-op", StringComparison.Ordinal))
        {
            return Race;
        }

        return Unknown;
    }
}
