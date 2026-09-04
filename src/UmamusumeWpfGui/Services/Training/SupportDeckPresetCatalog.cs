using System.Collections.ObjectModel;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// The single source of truth for support-deck presets and support-card type
/// filter keys. Runtime selectors and UI validation should consume this
/// catalog instead of maintaining their own preset switch statements.
/// </summary>
public sealed record SupportDeckPresetDefinition(
    string Value,
    string Label,
    IReadOnlyDictionary<string, int> RequiredTypes)
{
    public bool IsCustom => Value.Equals(
        SupportDeckPresetCatalog.CustomPreset,
        StringComparison.OrdinalIgnoreCase);

    public int RequiredCardCount => RequiredTypes.Values.Sum();
}

public static class SupportDeckPresetCatalog
{
    public const string CustomPreset = "custom";

    private static readonly ReadOnlyDictionary<string, string> FilterKeys =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["speed"] = "speed",
                ["stamina"] = "stamina",
                ["power"] = "power",
                ["guts"] = "guts",
                ["wit"] = "wit",
                ["friend"] = "friend",
            });

    private static readonly IReadOnlyDictionary<string, int> EmptyTypes =
        new ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

    private static readonly IReadOnlyList<SupportDeckPresetDefinition> AllPresets =
    [
        new(CustomPreset, "Custom", EmptyTypes),
        new(
            "speed3-stamina3",
            "3 Speed / 3 Stamina",
            Types(("Speed", 3), ("Stamina", 3))),
        new(
            "speed3-stamina2-wit1",
            "3 Speed / 2 Stamina / 1 Wit",
            Types(("Speed", 3), ("Stamina", 2), ("Wit", 1))),
        new(
            "speed2-stamina2-power1-wit1",
            "2 Speed / 2 Stamina / 1 Power / 1 Wit",
            Types(("Speed", 2), ("Stamina", 2), ("Power", 1), ("Wit", 1))),
        new(
            "speed2-stamina1-power1-wit1-friend1",
            "2 Speed / 1 Stamina / 1 Power / 1 Wit / 1 Friend",
            Types(
                ("Speed", 2),
                ("Stamina", 1),
                ("Power", 1),
                ("Wit", 1),
                ("Friend", 1))),
    ];

    public static IReadOnlyList<SupportDeckPresetDefinition> Presets => AllPresets;

    public static SupportDeckPresetDefinition? Find(string? preset)
    {
        var normalized = NormalizePreset(preset);
        return AllPresets.FirstOrDefault(item => item.Value.Equals(
            normalized,
            StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyDictionary<string, int>? GetRequiredTypes(string? preset) =>
        Find(preset)?.RequiredTypes is { Count: > 0 } requiredTypes
            ? requiredTypes
            : null;

    public static bool IsValidDeck(
        string? preset,
        IEnumerable<string?> cardTypes)
    {
        ArgumentNullException.ThrowIfNull(cardTypes);

        var requiredTypes = GetRequiredTypes(preset);
        if (requiredTypes is null)
            return true;

        var actualTypes = cardTypes
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .GroupBy(type => type!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.OrdinalIgnoreCase);

        if (actualTypes.Values.Sum() != requiredTypes.Values.Sum())
            return false;

        return requiredTypes.All(required =>
            actualTypes.TryGetValue(required.Key, out var actual)
            && actual == required.Value);
    }

    public static bool IsValidFriendCardType(
        string? preset,
        string? friendType,
        bool allowCustomPreset = false)
    {
        var requiredTypes = GetRequiredTypes(preset);
        if (requiredTypes is null)
            return allowCustomPreset;

        if (requiredTypes.ContainsKey("Friend"))
            return true;

        return !string.IsNullOrWhiteSpace(friendType)
            && requiredTypes.TryGetValue(friendType.Trim(), out var requiredCount)
            && requiredCount > 0;
    }

    public static string? GetFilterKey(string? supportType)
    {
        var normalized = supportType?.Trim().ToLowerInvariant();
        return normalized is not null && FilterKeys.TryGetValue(normalized, out var filterKey)
            ? filterKey
            : null;
    }

    public static string NormalizePreset(string? preset) =>
        string.IsNullOrWhiteSpace(preset)
            ? CustomPreset
            : preset.Trim().ToLowerInvariant();

    private static ReadOnlyDictionary<string, int> Types(
        params (string Type, int Count)[] entries) =>
        new ReadOnlyDictionary<string, int>(
            entries.ToDictionary(
                entry => entry.Type,
                entry => entry.Count,
                StringComparer.OrdinalIgnoreCase));
}
