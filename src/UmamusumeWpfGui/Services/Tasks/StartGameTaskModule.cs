using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Complete Start game task module. New task types should follow this shape
/// and keep their settings and execution code in their own module folder.
/// </summary>
public sealed class StartGameTaskModule : IGrassTaskModule
{
    private readonly IGameLauncher _gameLauncher;
    private readonly ISettingsService _settingsService;
    private readonly ILocalizationService _localizationService;

    public StartGameTaskModule(
        IGameLauncher gameLauncher,
        ISettingsService settingsService,
        ILocalizationService localizationService)
    {
        ArgumentNullException.ThrowIfNull(gameLauncher);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(localizationService);
        _gameLauncher = gameLauncher;
        _settingsService = settingsService;
        _localizationService = localizationService;
        Settings = new StartGameTaskSettingsViewModel(settingsService);
    }

    public GrassTaskDefinition Definition { get; } = new(
        "start-game",
        "GrassTaskStartGame",
        "GrassTaskStartGameDescription",
        "Start game",
        "Launch the configured Android game");

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

    public IGrassTaskModule CreateInstance() => new StartGameTaskModule(
        _gameLauncher,
        _settingsService,
        _localizationService);

    public bool CanExecute(GrassTaskExecutionContext context) =>
        context.Connection is not null
        && !string.IsNullOrWhiteSpace(Settings.PackageId);

    public async Task<GrassTaskExecutionResult> ExecuteAsync(
        GrassTaskExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!CanExecute(context) || context.Connection is not { } connection)
        {
            Settings.SetStatus(Localize(
                "GrassGameConnectionRequired",
                "Connect a device in Settings to start the game"));
            return new GrassTaskExecutionResult(
                false,
                false,
                Settings.Status);
        }

        Settings.SetStatus(Localize("GrassGameStarting", "Starting game"));
        Settings.Persist();
        var result = await _gameLauncher.StartAsync(
            connection.AdbPath,
            connection.Serial,
            Settings.PackageId,
            Settings.ActivityName,
            cancellationToken).ConfigureAwait(false);

        Settings.SetStatus(result.ProcessDetected
            ? Localize("GrassGameStarted", "Game started")
            : result.Succeeded
                ? Localize(
                    "GrassGameLaunchPending",
                    "Launch command completed; game process is not detected yet")
                : Localize("GrassGameLaunchFailed", "Game launch failed"));

        return new GrassTaskExecutionResult(
            result.Succeeded,
            result.ProcessDetected,
            result.Message);
    }

    public async Task<GrassTaskExecutionResult> StopAsync(
        GrassTaskExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Connection is not { } connection
            || string.IsNullOrWhiteSpace(Settings.PackageId))
        {
            return new GrassTaskExecutionResult(
                false,
                false,
                Localize("GrassGameConnectionRequired", "Connect a device in Settings to stop the game"));
        }

        Settings.SetStatus(Localize("GrassGameStopping", "Stopping game"));
        var result = await _gameLauncher.StopAsync(
            connection.AdbPath,
            connection.Serial,
            Settings.PackageId,
            cancellationToken).ConfigureAwait(false);
        Settings.SetStatus(result.Succeeded
            ? Localize("GrassGameStopped", "Game stopped")
            : Localize("GrassGameStopFailed", "Game stop failed"));

        return new GrassTaskExecutionResult(
            result.Succeeded,
            false,
            result.Message);
    }

    private string Localize(string key, string fallback)
    {
        var value = _localizationService.GetString(key);
        return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value;
    }

    private static string? ReadString(JsonObject settings, string key)
    {
        try
        {
            return settings[key]?.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
