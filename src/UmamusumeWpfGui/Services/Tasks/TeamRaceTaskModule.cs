using System.Globalization;
using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

public sealed class TeamRaceTaskModule : IGrassTaskModule
{
    public TeamRaceTaskModule() => Settings = new TeamRaceTaskSettingsViewModel();

    public GrassTaskDefinition Definition { get; } = new(
        "team-race", "GrassTaskTeamRace", "GrassTaskTeamRaceDescription",
        "Team Race", "Run the Team Race queue task");

    public TeamRaceTaskSettingsViewModel Settings { get; }

    object IGrassTaskModule.Settings => Settings;

    public JsonObject ExportSettings() => new()
    {
        ["definitionPath"] = Settings.DefinitionPath,
        ["raceCount"] = Settings.RaceCount,
        ["stopWhenTicketsEmpty"] = Settings.StopWhenTicketsEmpty,
    };

    public void ImportSettings(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var definitionPath = ReadString(settings, "definitionPath");
        if (definitionPath is not null)
            Settings.DefinitionPath = HachimiResourcePaths.MigrateLegacyDefinitionPath(definitionPath);
        Settings.RaceCountText = ReadInt(settings, "raceCount", Settings.RaceCount)
            .ToString(CultureInfo.InvariantCulture);
        Settings.StopWhenTicketsEmpty = ReadBool(settings, "stopWhenTicketsEmpty", Settings.StopWhenTicketsEmpty);
    }

    public IGrassTaskModule CreateInstance() => new TeamRaceTaskModule();

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
                TeamRaceTaskSettingsViewModel.MinimumRaceCount,
                TeamRaceTaskSettingsViewModel.MaximumRaceCount);
        }
        catch (InvalidOperationException) { return fallback; }
        catch (FormatException) { return fallback; }
    }

    private static bool ReadBool(JsonObject settings, string key, bool fallback)
    {
        try { return settings[key]?.GetValue<bool>() ?? fallback; }
        catch (InvalidOperationException) { return fallback; }
        catch (FormatException) { return fallback; }
    }
}
