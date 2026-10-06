using System.Globalization;
using System.Text.RegularExpressions;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>Shared skill-page number parsing; field-specific ranges stay with callers.</summary>
internal static class CareerOcrNumberParser
{
    public static int? ParseSingleNumber(ScreenTextRecognitionResult? result) =>
        result is null ? null : ParseSingleNumber(result.Detections.Select(item => item.Text));

    public static int? ParseSingleNumber(IEnumerable<string> text)
    {
        var numbers = text
            .Select(item => item.Replace('O', '0').Replace('o', '0').Replace('l', '1').Replace('I', '1'))
            .SelectMany(item => Regex.Matches(item, @"\b\d{1,4}\b").Select(match => match.Value))
            .Select(item => int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture,
                out var parsed) ? parsed : -1)
            .Where(value => value >= 0)
            .Distinct()
            .ToArray();
        return numbers.Length == 1 ? numbers[0] : null;
    }

    public static int? ParseTrainingNumber(IEnumerable<string> text, int maximum)
    {
        var items = text.ToArray();
        // Keep skill-page parsing unchanged; negative training gains are not supported.
        if (items.Any(item => Regex.IsMatch(item, @"[-−]\s*\d")))
            return null;
        var tokens = new List<string>();
        foreach (var item in items)
        {
            var normalized = Regex.Replace(item, @"\bFailure\b", "", RegexOptions.IgnoreCase)
                .Trim().Replace('O', '0').Replace('o', '0').Replace('l', '1').Replace('I', '1');
            if (!normalized.Any(char.IsAsciiDigit))
                continue;
            // Reject text fragments such as '+fi]9', and require the percent glyph so
            // OCR's '010' interpretation of '0%' cannot become a failure rate of 10.
            var pattern = maximum == 100 ? @"^\d{1,3}\s*%$" : @"^[+*#]\s*\d{1,4}$";
            if (!Regex.IsMatch(normalized, pattern))
                return null;
            tokens.Add(normalized);
        }
        var value = ParseSingleNumber(tokens);
        return value is >= 0 && value <= maximum ? value : null;
    }
}
