using System.Globalization;
using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

public sealed class DailyRaceTaskModule : IGrassTaskModule
{
    private readonly ISettingsService _settingsService;
    private readonly ILocalizationService _localizationService;
    private readonly IDailyRacePipeline _pipeline;

    public DailyRaceTaskModule(
        ISettingsService settingsService,
        ILocalizationService localizationService,
        IDailyRacePipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(localizationService);
        ArgumentNullException.ThrowIfNull(pipeline);
        _settingsService = settingsService;
        _localizationService = localizationService;
        _pipeline = pipeline;
        Settings = new DailyRaceTaskSettingsViewModel();
    }

    public GrassTaskDefinition Definition { get; } = new(
        "daily-race",
        "GrassTaskDailyRace",
        "GrassTaskDailyRaceDescription",
        "Daily Race",
        "Run Daily Race for Monies or Support Points");

    public DailyRaceTaskSettingsViewModel Settings { get; }

    object IGrassTaskModule.Settings => Settings;

    public JsonObject ExportSettings() => new()
    {
        ["definitionPath"] = Settings.DefinitionPath,
        ["mode"] = Settings.Mode,
        ["difficulty"] = Settings.Difficulty,
        ["raceCount"] = Settings.RaceCount,
    };

    public void ImportSettings(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings.DefinitionPath = ReadString(settings, "definitionPath") ?? Settings.DefinitionPath;
        Settings.Mode = ReadString(settings, "mode") ?? Settings.Mode;
        Settings.Difficulty = ReadString(settings, "difficulty") ?? Settings.Difficulty;
        Settings.RaceCountText = ReadInt(
                settings,
                "raceCount",
                Settings.RaceCount)
            .ToString(CultureInfo.InvariantCulture);
    }

    public IGrassTaskModule CreateInstance() => new DailyRaceTaskModule(
        _settingsService,
        _localizationService,
        _pipeline);

    public bool CanExecute(GrassTaskExecutionContext context) =>
        context.Connection is not null
        && !string.IsNullOrWhiteSpace(Settings.DefinitionPath)
        && Settings.IsModeValid
        && Settings.IsDifficultyValid
        && Settings.RaceCount > 0;

    public async Task<GrassTaskExecutionResult> ExecuteAsync(
        GrassTaskExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!CanExecute(context) || context.Connection is not { } connection)
        {
            var message = Localize(
                "GrassDailyRaceConnectionRequired",
                "Connect a device and configure a valid Daily Race definition first.");
            Settings.SetStatus(message);
            return new GrassTaskExecutionResult(false, false, message);
        }

        var startingMessage = Localize("GrassDailyRaceStarting", "Starting Daily Race");
        Settings.SetStatus(startingMessage);
        context.LogSink?.Add(
            Localize("GrassTaskDailyRace", "Daily Race"),
            string.Format(
                CultureInfo.InvariantCulture,
                Localize("GrassDailyRaceRunning", "Running {0} {1} race(s) from {2}"),
                Settings.Mode,
                Settings.RaceCount,
                Settings.DefinitionPath));

        var result = await _pipeline.RunAsync(
                connection,
                Settings.DefinitionPath,
                Settings.Mode,
                Settings.Difficulty,
                Settings.RaceCount,
                context.LogSink,
                cancellationToken)
            .ConfigureAwait(false);
        var status = result.Succeeded
            ? string.Format(
                CultureInfo.InvariantCulture,
                Localize("GrassDailyRaceCompleted", "Daily Race completed: {0} race(s)"),
                result.RacesCompleted)
            : result.Message;
        Settings.SetStatus(status);
        return new GrassTaskExecutionResult(result.Succeeded, false, result.Message);
    }

    public async Task<GrassTaskExecutionResult> StopAsync(
        GrassTaskExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Connection is not { } connection)
        {
            var message = Localize(
                "GrassDailyRaceConnectionRequired",
                "Connect a device before stopping Daily Race.");
            Settings.SetStatus(message);
            return new GrassTaskExecutionResult(false, false, message);
        }

        var result = await _pipeline.StopAsync(
                connection,
                context.LogSink,
                cancellationToken)
            .ConfigureAwait(false);
        Settings.SetStatus(result.Message);
        return new GrassTaskExecutionResult(result.Succeeded, false, result.Message);
    }

    private string Localize(string key, string fallback)
    {
        var value = _localizationService.GetString(key);
        return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value;
    }

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
            return Math.Clamp(
                settings[key]?.GetValue<int>() ?? fallback,
                DailyRaceTaskSettingsViewModel.MinimumRaceCount,
                DailyRaceTaskSettingsViewModel.MaximumRaceCount);
        }
        catch (InvalidOperationException) { return fallback; }
        catch (FormatException) { return fallback; }
    }
}
