namespace UmamusumeWpfGui.Services.Training;

public enum CareerMood
{
    Awful = 0,
    Bad = 1,
    Normal = 2,
    Good = 3,
    Great = 4,
}

internal static class CareerMoodParser
{
    public static CareerMood? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var letters = new string(text
            .ToUpperInvariant()
            .Where(char.IsLetter)
            .ToArray());
        if (letters.Contains("AWFUL", StringComparison.Ordinal))
            return CareerMood.Awful;
        if (letters.Contains("BAD", StringComparison.Ordinal))
            return CareerMood.Bad;
        if (letters.Contains("NORMAL", StringComparison.Ordinal))
            return CareerMood.Normal;
        if (letters.Contains("GOOD", StringComparison.Ordinal))
            return CareerMood.Good;
        if (letters.Contains("GREAT", StringComparison.Ordinal))
            return CareerMood.Great;
        return null;
    }
}
