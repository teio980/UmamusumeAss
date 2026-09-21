using System.Text.RegularExpressions;

namespace UmamusumeWpfGui.Services.Training;

internal static partial class CareerGoalTextParser
{
    public const string Unknown = "unknown";
    public const string Fans = "fans";
    public const string Race = "race";

    [GeneratedRegex(@"(?<!\d)(?<turns>\d{1,3})\s*(?:turn|turns)(?:\s*\(s\))?\b", RegexOptions.IgnoreCase)]
    private static partial Regex TurnsLeftRegex();

    [GeneratedRegex(@"(?<!\d)(?<fans>[\d,]{1,7})\s*fan(?:s)?(?:\s*\(s\))?\s*to\s*go\b", RegexOptions.IgnoreCase)]
    private static partial Regex FansToGoRegex();

    public static int? ParseTurnsLeft(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = TurnsLeftRegex().Match(text);
        return match.Success
            && int.TryParse(match.Groups["turns"].Value, out var turns)
            ? Math.Clamp(turns, 0, 999)
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
