using System.Globalization;
using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// UI/task-queue shell for Team Race. The actual screenshot/OCR/input state
/// machine is supplied through <see cref="ITeamRacePipeline"/>.
/// </summary>
public sealed class TeamRaceTaskModule : IGrassTaskModule
{
    private readonly ISettingsService _settingsService;
    private readonly ILocalizationService _localizationService;
    private readonly ITeamRacePipeline _pipeline;

    public TeamRaceTaskModule(
        ISettingsService settingsService,
        ILocalizationService localizationService,
        ITeamRacePipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(localizationService);
        ArgumentNullException.ThrowIfNull(pipeline);
        _settingsService = settingsService;
        _localizationService = localizationService;
        _pipeline = pipeline;
        Settings = new TeamRaceTaskSettingsViewModel();
    }

    public GrassTaskDefinition Definition { get; } = new(
        "team-race",
        "GrassTaskTeamRace",
        "GrassTaskTeamRaceDescription",
        "Team Race",
        "Run the Team Race state machine");

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

        Settings.DefinitionPath = ReadString(settings, "definitionPath")
            ?? Settings.DefinitionPath;
        Settings.RaceCountText = ReadInt(settings, "raceCount", Settings.RaceCount)
            .ToString(CultureInfo.InvariantCulture);
        Settings.StopWhenTicketsEmpty = ReadBool(
            settings,
            "stopWhenTicketsEmpty",
            Settings.StopWhenTicketsEmpty);
    }

    public IGrassTaskModule CreateInstance() => new TeamRaceTaskModule(
        _settingsService,
        _localizationService,
        _pipeline);

    public bool CanExecute(GrassTaskExecutionContext context) =>
        context.Connection is not null
        && !string.IsNullOrWhiteSpace(Settings.DefinitionPath)
        && Settings.RaceCount > 0;

    public async Task<GrassTaskExecutionResult> ExecuteAsync(
        GrassTaskExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!CanExecute(context) || context.Connection is not { } connection)
        {
            var connectionMessage = Localize(
                "GrassTeamRaceConnectionRequired",
                "Connect a device and configure a valid Team Race definition first");
            Settings.SetStatus(connectionMessage);
            return new GrassTaskExecutionResult(false, false, connectionMessage);
        }

        var startingMessage = Localize("GrassTeamRaceStarting", "Starting Team Race");
        Settings.SetStatus(startingMessage);
        context.LogSink?.Add(
            Localize("GrassTaskTeamRace", "Team Race"),
            string.Format(
                CultureInfo.InvariantCulture,
                Localize("GrassTeamRaceRunning", "Running {0} race(s)"),
                Settings.RaceCount));

        var result = await _pipeline.RunAsync(
            connection,
            Settings.DefinitionPath,
            Settings.RaceCount,
            Settings.StopWhenTicketsEmpty,
            context.LogSink,
            cancellationToken).ConfigureAwait(false);
        var pipelineMessage = LocalizePipelineMessage(result.Message);
        var status = result.Succeeded
            ? string.Format(
                CultureInfo.InvariantCulture,
                Localize("GrassTeamRaceCompleted", "Team Race completed: {0} race(s)"),
                result.RacesCompleted)
            : pipelineMessage;
        Settings.SetStatus(status);
        return new GrassTaskExecutionResult(result.Succeeded, false, pipelineMessage);
    }

    public async Task<GrassTaskExecutionResult> StopAsync(
        GrassTaskExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Connection is not { } connection)
        {
            var connectionMessage = Localize(
                "GrassTeamRaceConnectionRequired",
                "Connect a device and configure a valid Team Race definition first");
            Settings.SetStatus(connectionMessage);
            return new GrassTaskExecutionResult(false, false, connectionMessage);
        }

        var result = await _pipeline.StopAsync(
            connection,
            context.LogSink,
            cancellationToken).ConfigureAwait(false);
        var pipelineMessage = LocalizePipelineMessage(result.Message);
        Settings.SetStatus(pipelineMessage);
        return new GrassTaskExecutionResult(result.Succeeded, false, pipelineMessage);
    }

    private string Localize(string key, string fallback)
    {
        var value = _localizationService.GetString(key);
        return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value;
    }

    private string LocalizePipelineMessage(string message) =>
        string.Equals(
            message,
            TeamRacePipelinePlaceholder.NotImplementedMessage,
            StringComparison.Ordinal)
            ? Localize(
                "GrassTeamRaceExecutorMissing",
                TeamRacePipelinePlaceholder.NotImplementedMessage)
            : message;

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

    private static int ReadInt(JsonObject settings, string key, int fallback)
    {
        try
        {
            return Math.Clamp(
                settings[key]?.GetValue<int>() ?? fallback,
                TeamRaceTaskSettingsViewModel.MinimumRaceCount,
                TeamRaceTaskSettingsViewModel.MaximumRaceCount);
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private static bool ReadBool(JsonObject settings, string key, bool fallback)
    {
        try
        {
            return settings[key]?.GetValue<bool>() ?? fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
        catch (FormatException)
        {
            return fallback;
        }
    }
}
