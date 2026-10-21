using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

public sealed class MailCollectionTaskModule : IGrassTaskModule
{
    public MailCollectionTaskModule() => Settings = new MailCollectionTaskSettingsViewModel();

    public GrassTaskDefinition Definition { get; } = new(
        "mail-collection", "GrassTaskRewardsCollection", "GrassTaskRewardsCollectionDescription",
        "Mail collection", "Collect available presents from the in-game mailbox");

    public MailCollectionTaskSettingsViewModel Settings { get; }

    object IGrassTaskModule.Settings => Settings;

    public JsonObject ExportSettings() => new() { ["definitionPath"] = Settings.DefinitionPath };

    public void ImportSettings(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (ReadString(settings, "definitionPath") is { } path)
            Settings.DefinitionPath = HachimiResourcePaths.MigrateLegacyDefinitionPath(path);
    }

    public IGrassTaskModule CreateInstance() => new MailCollectionTaskModule();

    private static string? ReadString(JsonObject settings, string key)
    {
        try { return settings[key]?.GetValue<string>(); }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }
}
