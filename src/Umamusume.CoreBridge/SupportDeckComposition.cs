using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Umamusume.CoreBridge;

public static class SupportDeckComposition
{
    public static IReadOnlyList<string> Types { get; } = ["Speed", "Stamina", "Power", "Guts", "Wit", "Friend"];
    public const string Default = "types-speed3-stamina3";

    // Keep the original preset strings readable when loading existing profiles.
    public static Dictionary<string, int>? Parse(string preset)
    {
        var value = preset.ToLowerInvariant();
        if (value.StartsWith("types-", StringComparison.Ordinal))
            value = value[6..];
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in value.Split('-'))
        {
            if (part.Length < 2 || part[^1] is < '0' or > '6')
                return null;
            var type = Types.FirstOrDefault(type => type.Equals(part[..^1], StringComparison.OrdinalIgnoreCase));
            if (type is null || !result.TryAdd(type, part[^1] - '0'))
                return null;
        }
        return result;
    }

    public static string Encode(IReadOnlyDictionary<string, int> counts)
    {
        var parts = Types.Where(type => counts.GetValueOrDefault(type) > 0)
            .Select(type => type.ToLowerInvariant() + counts[type].ToString(CultureInfo.InvariantCulture));
        var value = string.Join('-', parts);
        return "types-" + (value.Length == 0 ? "speed0" : value);
    }
}
