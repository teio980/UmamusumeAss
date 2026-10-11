using System.Globalization;
using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

public sealed class DailyRaceTaskModule : IGrassTaskModule
{
    private readonly IUmaDatabaseService? _umaDatabase;

    public DailyRaceTaskModule(IUmaDatabaseService? umaDatabase = null)
    {
        _umaDatabase = umaDatabase;
        Settings = new DailyRaceTaskSettingsViewModel(umaDatabase);
    }

    public GrassTaskDefinition Definition { get; } = new(
        "daily-race", "GrassTaskDailyRace", "GrassTaskDailyRaceDescription",
        "Daily Race", "Run Daily Race for Monies or Support Points");

    public DailyRaceTaskSettingsViewModel Settings { get; }

    object IGrassTaskModule.Settings => Settings;

    public JsonObject ExportSettings() => new()
    {
        ["definitionPath"] = Settings.DefinitionPath,
        ["mode"] = Settings.Mode,
        ["difficulty"] = Settings.Difficulty,
        ["raceCount"] = Settings.RaceCount,
        ["traineeId"] = Settings.TraineeId,
    };

    public void ImportSettings(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var definitionPath = ReadString(settings, "definitionPath");
        if (definitionPath is not null)
            Settings.DefinitionPath = HachimiResourcePaths.MigrateLegacyDefinitionPath(definitionPath);
        Settings.Mode = ReadString(settings, "mode") ?? Settings.Mode;
        Settings.Difficulty = ReadString(settings, "difficulty") ?? Settings.Difficulty;
        Settings.RaceCountText = ReadInt(settings, "raceCount", Settings.RaceCount)
            .ToString(CultureInfo.InvariantCulture);
        Settings.TraineeId = ReadNullableInt(settings, "traineeId");
    }

    public IGrassTaskModule CreateInstance() => new DailyRaceTaskModule(_umaDatabase);

    private static string? ReadString(JsonObject settings, string key)
    {
        try { return settings[key]?.GetValue<string>(); }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }

    private static int ReadInt(JsonObject settings, string key, int fallback)
    {
        try
        {
            return Math.Clamp(settings[key]?.GetValue<int>() ?? fallback,
                DailyRaceTaskSettingsViewModel.MinimumRaceCount,
                DailyRaceTaskSettingsViewModel.MaximumRaceCount);
        }
        catch (InvalidOperationException) { return fallback; }
        catch (FormatException) { return fallback; }
    }

    private static int? ReadNullableInt(JsonObject settings, string key)
    {
        try
        {
            var value = settings[key];
            return value is null ? null : Math.Max(1, value.GetValue<int>());
        }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }
}
