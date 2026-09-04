using System.Text.Json.Nodes;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Persists Career task settings. Runtime pipelines should never need to know
/// about the JSON shape used by the task profile.
/// </summary>
public static class CareerTaskSettingsSerializer
{
    public static JsonObject Export(CareerTrainingTaskSettingsViewModel settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new JsonObject
        {
            ["scenarioId"] = settings.ScenarioId,
            ["manifestPath"] = settings.ManifestPath,
            ["traineeId"] = settings.TraineeId,
            ["careerMode"] = settings.CareerMode,
            ["independentTrainingFocus"] = settings.IndependentTrainingFocus,
            ["independentLineupStrategy"] = settings.IndependentLineupStrategy,
            ["independentAgendaSelections"] = new JsonArray(settings.ParseIndependentAgendaSelections()
                .Select(item => (JsonNode?)JsonValue.Create(item.Key)).ToArray()),
            ["independentSkillIds"] = new JsonArray(settings.ParseIndependentSkillIds()
                .Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
            ["continueExistingCareer"] = settings.ContinueExistingCareer,
            ["supportDeckMode"] = settings.SupportDeckMode,
            ["supportDeckPreset"] = settings.SupportDeckPreset,
            ["friendSupportCardId"] = settings.FriendSupportCardId is { } friendSupportCardId
                ? JsonValue.Create(friendSupportCardId)
                : null,
            ["supportCardIds"] = new JsonArray(settings.ParseSupportCardIds()
                .Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["strategyId"] = settings.StrategyId,
            ["pauseOnUnknownOutcome"] = settings.PauseOnUnknownOutcome,
            ["allowOptionalRaces"] = settings.AllowOptionalRaces,
            ["legacySelectionMode"] = settings.LegacySelectionMode,
            ["useLegacyGuest"] = settings.UseLegacyGuest,
            ["useCachedLegacy"] = settings.UseCachedLegacy,
            ["legacyAttributeSparks"] = new JsonArray(settings.ParseLegacyAttributeSparks()
                .Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
            ["legacyAptitudeSparks"] = new JsonArray(settings.ParseLegacyAptitudeSparks()
                .Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
        };
    }

    public static void Import(CareerTrainingTaskSettingsViewModel settings, JsonObject values)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(values);

        var manifestPath = ReadString(values, "manifestPath");
        settings.ManifestPath = MigrateManifestPath(manifestPath ?? settings.ManifestPath);
        settings.ScenarioId = ReadString(values, "scenarioId") ?? settings.ScenarioId;
        settings.TraineeId = ReadNullableInt(values, "traineeId") ?? settings.TraineeId;
        settings.CareerMode = ReadString(values, "careerMode") ?? settings.CareerMode;
        settings.IndependentTrainingFocus = ReadString(values, "independentTrainingFocus")
            ?? settings.IndependentTrainingFocus;
        settings.IndependentLineupStrategy = ReadString(values, "independentLineupStrategy")
            ?? settings.IndependentLineupStrategy;
        settings.IndependentAgendaSelectionsText = string.Join(
            Environment.NewLine,
            ReadStringArray(values, "independentAgendaSelections"));
        settings.IndependentSkillIdsText = string.Join(",", ReadIntArray(values, "independentSkillIds"));
        settings.ContinueExistingCareer = ReadBool(
            values,
            "continueExistingCareer",
            settings.ContinueExistingCareer);

        var supportCardIds = values["supportCardIds"] is JsonArray cards
            ? string.Join(",", cards.Select(item => item?.GetValue<int>()).Where(item => item is > 0))
            : string.Empty;
        settings.SupportCardIdsText = supportCardIds;
        settings.SupportDeckMode = ReadString(values, "supportDeckMode")
            ?? (string.IsNullOrWhiteSpace(supportCardIds) ? "auto" : "selected");
        settings.SupportDeckPreset = ReadString(values, "supportDeckPreset") ?? settings.SupportDeckPreset;
        settings.FriendSupportCardId = ReadNullableInt(values, "friendSupportCardId");
        settings.StrategyId = ReadString(values, "strategyId") ?? settings.StrategyId;
        settings.PauseOnUnknownOutcome = ReadBool(values, "pauseOnUnknownOutcome", settings.PauseOnUnknownOutcome);
        settings.AllowOptionalRaces = ReadBool(values, "allowOptionalRaces", settings.AllowOptionalRaces);
        settings.LegacySelectionMode = ReadString(values, "legacySelectionMode") ?? settings.LegacySelectionMode;
        settings.UseLegacyGuest = ReadBool(values, "useLegacyGuest", settings.UseLegacyGuest);
        settings.UseCachedLegacy = ReadBool(values, "useCachedLegacy", settings.UseCachedLegacy);
        settings.SetLegacySparkSelections(
            ReadStringArray(values, "legacyAttributeSparks"),
            ReadStringArray(values, "legacyAptitudeSparks"));
    }

    private static string? ReadString(JsonObject values, string key)
    {
        try { return values[key]?.GetValue<string>(); }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }

    private static int? ReadNullableInt(JsonObject values, string key)
    {
        try
        {
            var value = values[key];
            return value is null ? null : Math.Max(1, value.GetValue<int>());
        }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }

    private static bool ReadBool(JsonObject values, string key, bool fallback)
    {
        try { return values[key]?.GetValue<bool>() ?? fallback; }
        catch (InvalidOperationException) { return fallback; }
        catch (FormatException) { return fallback; }
    }

    private static string[] ReadStringArray(JsonObject values, string key)
    {
        if (values[key] is not JsonArray array)
            return [];
        return array.Select(item =>
            {
                try { return item?.GetValue<string>(); }
                catch (InvalidOperationException) { return null; }
                catch (FormatException) { return null; }
            })
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int[] ReadIntArray(JsonObject values, string key)
    {
        if (values[key] is not JsonArray array)
            return [];
        return array.Select(item =>
            {
                try { return item?.GetValue<int>() ?? 0; }
                catch (InvalidOperationException) { return 0; }
                catch (FormatException) { return 0; }
            })
            .Where(item => item > 0)
            .Distinct()
            .ToArray();
    }

    private static string MigrateManifestPath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        const string legacyPrefix = "resource/uma/scenarios/ura/";
        var isLegacyRelative = normalized.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase);
        var isLegacyAbsolute = normalized.EndsWith(
            "/resource/uma/scenarios/ura/manifest.json",
            StringComparison.OrdinalIgnoreCase);
        return isLegacyRelative || isLegacyAbsolute
            ? CareerTrainingTaskSettingsViewModel.DefaultManifestPath
            : path.Trim();
    }
}
