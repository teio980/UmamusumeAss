using System.Collections.ObjectModel;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Top-level area of a stable Career runtime screen.
/// The area is used to validate an observation before any runtime state is
/// updated or a flow is dispatched.
/// </summary>
public enum CareerScreenKind
{
    Unknown,
    Main,
    Turn,
    Race,
    Event,
    Settlement,
}

/// <summary>
/// Resolves runtime categories from the scenario screen catalog. The
/// profile-specific overload must be used by runtime code so different
/// scenario packs never share classification state.
/// </summary>
internal static class CareerScreenClassification
{
    private static readonly Lazy<IReadOnlyDictionary<string, CatalogEntry>> BuiltInKinds =
        new(LoadBuiltInKinds, LazyThreadSafetyMode.ExecutionAndPublication);

    public static CareerScreenKind Classify(string? screenId) =>
        !string.IsNullOrWhiteSpace(screenId)
        && BuiltInKinds.Value.TryGetValue(screenId, out var entry)
            ? entry.Kind
            : CareerScreenKind.Unknown;

    public static CareerScreenKind Classify(
        string? screenId,
        UraScreenProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(screenId))
            return CareerScreenKind.Unknown;

        var screen = profile.Find(screenId);
        if (screen is null)
            return CareerScreenKind.Unknown;

        // Screens written before Flow metadata was introduced retain the
        // built-in classification for compatibility. A present Flow always
        // wins, including setup and other non-runtime flows.
        return string.IsNullOrWhiteSpace(screen.Flow)
            ? Classify(screenId)
            : ClassifyFlow(screen.Flow);
    }

    public static int GetRecognitionPriority(UraScreenDefinition screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        if (screen.Recognition.PrioritySpecified)
            return screen.Recognition.Priority;

        // The default value also represents an omitted priority in legacy
        // profiles. Reuse the built-in metadata snapshot for those profiles;
        // new profiles carry an explicit value for every screen.
        return !string.IsNullOrWhiteSpace(screen.ScreenId)
            && BuiltInKinds.Value.TryGetValue(screen.ScreenId, out var entry)
                ? entry.Priority
                : screen.Recognition.Priority;
    }

    public static bool IsRuntimeScreen(string? screenId) =>
        IsRuntimeKind(Classify(screenId));

    public static bool IsRuntimeScreen(
        string? screenId,
        UraScreenProfile profile) =>
        IsRuntimeKind(Classify(screenId, profile));

    internal static CareerScreenKind ClassifyFlow(string? flow) =>
        flow?.Trim().ToLowerInvariant() switch
        {
            "main" => CareerScreenKind.Main,
            "turn" => CareerScreenKind.Turn,
            "race" => CareerScreenKind.Race,
            "event" => CareerScreenKind.Event,
            "settlement" => CareerScreenKind.Settlement,
            // Setup and non-Normal career flows are deliberately outside the
            // stable Career runtime. Their owners are expressed by Flow too,
            // but they keep the historical Unknown runtime classification.
            _ => CareerScreenKind.Unknown,
        };

    private static bool IsRuntimeKind(CareerScreenKind kind) =>
        kind is CareerScreenKind.Main
            or CareerScreenKind.Turn
            or CareerScreenKind.Race
            or CareerScreenKind.Event
            or CareerScreenKind.Settlement;

    private static ReadOnlyDictionary<string, CatalogEntry> LoadBuiltInKinds()
    {
        var profile = UraScenarioPackLoader.LoadScreenProfileAsync(
                UmamusumeWpfGui.Services.HachimiResourcePaths.UraManifest)
            .GetAwaiter()
            .GetResult();
        var kinds = profile.Screens
            .Where(screen => !string.IsNullOrWhiteSpace(screen.ScreenId))
            .GroupBy(screen => screen.ScreenId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new CatalogEntry(
                    ClassifyFlow(group.Last().Flow),
                    group.Last().Recognition.Priority),
                StringComparer.OrdinalIgnoreCase);
        return new ReadOnlyDictionary<string, CatalogEntry>(kinds);
    }

    private sealed record CatalogEntry(CareerScreenKind Kind, int Priority);
}
