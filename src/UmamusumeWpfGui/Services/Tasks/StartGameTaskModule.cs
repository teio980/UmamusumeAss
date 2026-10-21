using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

public sealed class StartGameTaskModule : IGrassTaskModule
{
    private readonly ISettingsService _settingsService;

    public StartGameTaskModule(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        Settings = new StartGameTaskSettingsViewModel(settingsService);
    }

    public GrassTaskDefinition Definition { get; } = new(
        "start-game", "GrassTaskStartGame", "GrassTaskStartGameDescription",
        "Start game", "Launch the configured Android game");

    public StartGameTaskSettingsViewModel Settings { get; }

    object IGrassTaskModule.Settings => Settings;

    public JsonObject ExportSettings() => new()
    {
        ["packageId"] = Settings.PackageId,
        ["activityName"] = Settings.ActivityName,
    };

    public void ImportSettings(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings.PackageId = ReadString(settings, "packageId") ?? Settings.PackageId;
        Settings.ActivityName = ReadString(settings, "activityName") ?? Settings.ActivityName;
    }

    public IGrassTaskModule CreateInstance() => new StartGameTaskModule(_settingsService);

    private static string? ReadString(JsonObject settings, string key)
    {
        try { return settings[key]?.GetValue<string>(); }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }
}
