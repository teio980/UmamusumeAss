using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

public sealed class MissionCollectionTaskModule : IGrassTaskModule
{
    public MissionCollectionTaskModule() => Settings = new MissionCollectionTaskSettingsViewModel();

    public GrassTaskDefinition Definition { get; } = new(
        "mission-collection", "GrassTaskMissionCollection", "GrassTaskMissionCollectionDescription",
        "Mission collection", "Collect available mission rewards");

    public MissionCollectionTaskSettingsViewModel Settings { get; }

    object IGrassTaskModule.Settings => Settings;

    public JsonObject ExportSettings() => new() { ["definitionPath"] = Settings.DefinitionPath };

    public void ImportSettings(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (ReadString(settings, "definitionPath") is { } path)
            Settings.DefinitionPath = HachimiResourcePaths.MigrateLegacyDefinitionPath(path);
    }

    public IGrassTaskModule CreateInstance() => new MissionCollectionTaskModule();

    private static string? ReadString(JsonObject settings, string key)
    {
        try { return settings[key]?.GetValue<string>(); }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }
}
