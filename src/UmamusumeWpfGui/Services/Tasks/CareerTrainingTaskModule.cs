using System.Globalization;
using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Training;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

public sealed class CareerTrainingTaskModule : IGrassTaskModule
{
    private readonly ILocalizationService _localizationService;
    private readonly ICareerTrainingPipeline _pipeline;
    private readonly IUmaDatabaseService _umaDatabase;

    public CareerTrainingTaskModule(
        ILocalizationService localizationService,
        ICareerTrainingPipeline pipeline,
        IUmaDatabaseService umaDatabase)
    {
        ArgumentNullException.ThrowIfNull(localizationService);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(umaDatabase);
        _localizationService = localizationService;
        _pipeline = pipeline;
        _umaDatabase = umaDatabase;
        Settings = new CareerTrainingTaskSettingsViewModel(_umaDatabase);
    }

    public GrassTaskDefinition Definition { get; } = new(
        "career-training",
        "GrassTaskCareerTraining",
        "GrassTaskCareerTrainingDescription",
        "Career Training",
        "Run a modular scenario training career");

    public CareerTrainingTaskSettingsViewModel Settings { get; }

    object IGrassTaskModule.Settings => Settings;

    public JsonObject ExportSettings() => new()
    {
        ["scenarioId"] = Settings.ScenarioId,
        ["manifestPath"] = Settings.ManifestPath,
        ["traineeId"] = Settings.TraineeId,
        ["supportDeckMode"] = Settings.SupportDeckMode,
        ["supportDeckPreset"] = Settings.SupportDeckPreset,
        ["supportCardIds"] = new JsonArray(Settings.ParseSupportCardIds()
            .Select(id => (JsonNode?)JsonValue.Create(id))
            .ToArray()),
        ["strategyId"] = Settings.StrategyId,
        ["pauseOnUnknownOutcome"] = Settings.PauseOnUnknownOutcome,
        ["allowOptionalRaces"] = Settings.AllowOptionalRaces,
        ["legacySelectionMode"] = Settings.LegacySelectionMode,
        ["useLegacyGuest"] = Settings.UseLegacyGuest,
        ["useCachedLegacy"] = Settings.UseCachedLegacy,
        ["legacyAttributeSparks"] = new JsonArray(Settings.ParseLegacyAttributeSparks()
            .Select(item => (JsonNode?)JsonValue.Create(item))
            .ToArray()),
        ["legacyAptitudeSparks"] = new JsonArray(Settings.ParseLegacyAptitudeSparks()
            .Select(item => (JsonNode?)JsonValue.Create(item))
            .ToArray()),
    };

    public void ImportSettings(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var manifestPath = ReadString(settings, "manifestPath");
        Settings.ManifestPath = MigrateManifestPath(manifestPath ?? Settings.ManifestPath);
        Settings.ScenarioId = ReadString(settings, "scenarioId") ?? Settings.ScenarioId;
        Settings.TraineeId = ReadNullableInt(settings, "traineeId") ?? Settings.TraineeId;
        var supportCardIds = settings["supportCardIds"] is JsonArray cards
            ? string.Join(",", cards
                .Select(item => item?.GetValue<int>())
                .Where(item => item is > 0))
            : string.Empty;
        Settings.SupportCardIdsText = supportCardIds;
        var savedSupportDeckMode = ReadString(settings, "supportDeckMode");
        Settings.SupportDeckMode = savedSupportDeckMode
            ?? (string.IsNullOrWhiteSpace(supportCardIds) ? "auto" : "selected");
        Settings.SupportDeckPreset = ReadString(settings, "supportDeckPreset")
            ?? Settings.SupportDeckPreset;
        Settings.StrategyId = ReadString(settings, "strategyId") ?? Settings.StrategyId;
        Settings.PauseOnUnknownOutcome = ReadBool(
            settings,
            "pauseOnUnknownOutcome",
            Settings.PauseOnUnknownOutcome);
        Settings.AllowOptionalRaces = ReadBool(
            settings,
            "allowOptionalRaces",
            Settings.AllowOptionalRaces);
        Settings.LegacySelectionMode = ReadString(settings, "legacySelectionMode")
            ?? Settings.LegacySelectionMode;
        Settings.UseLegacyGuest = ReadBool(
            settings,
            "useLegacyGuest",
            Settings.UseLegacyGuest);
        Settings.UseCachedLegacy = ReadBool(
            settings,
            "useCachedLegacy",
            Settings.UseCachedLegacy);
        Settings.SetLegacySparkSelections(
            ReadStringArray(settings, "legacyAttributeSparks"),
            ReadStringArray(settings, "legacyAptitudeSparks"));
    }

    public IGrassTaskModule CreateInstance() => new CareerTrainingTaskModule(
        _localizationService,
        _pipeline,
        _umaDatabase);

    public bool CanExecute(GrassTaskExecutionContext context) =>
        context.Connection is not null
        && Settings.IsValid
        && (!Settings.IsManualSupportDeck
            || Settings.SelectedSupportCardCount is 5 or 6)
        && (!Settings.IsHighestStarSupportDeck
            || !Settings.SupportDeckPreset.Equals("custom", StringComparison.OrdinalIgnoreCase))
        && Settings.TraineeId is > 0
        && _umaDatabase.TryGetTrainee(Settings.TraineeId.Value, out var trainee)
        && trainee is not null
        && trainee.Available;

    public async Task<GrassTaskExecutionResult> ExecuteAsync(
        GrassTaskExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!CanExecute(context) || context.Connection is not { } connection)
        {
            var message = Localize(
                "GrassCareerTrainingConnectionRequired",
                "Connect a device and configure a valid career training profile first.");
            Settings.SetStatus(message);
            return new GrassTaskExecutionResult(false, false, message);
        }

        try
        {
            var result = await _pipeline.RunAsync(
                    connection,
                    new CareerTrainingSettings(
                        Settings.ManifestPath,
                        Settings.TraineeId!.Value,
                        Settings.ParseSupportCardIds(),
                        Settings.SupportDeckMode,
                        Settings.SupportDeckPreset,
                        Settings.StrategyId,
                        Settings.PauseOnUnknownOutcome,
                        Settings.AllowOptionalRaces,
                        Settings.LegacySelectionMode,
                        Settings.UseLegacyGuest,
                        Settings.UseCachedLegacy,
                        Settings.ParseLegacyAttributeSparks(),
                        Settings.ParseLegacyAptitudeSparks()),
                    context.LogSink,
                    cancellationToken)
                .ConfigureAwait(false);
            Settings.SetStatus(result.Message);
            return new GrassTaskExecutionResult(result.Succeeded, false, result.Message);
        }
        catch (OperationCanceledException)
        {
            var message = Localize("GrassCareerTrainingCanceled", "Career training canceled.");
            Settings.SetStatus(message);
            return new GrassTaskExecutionResult(false, false, message);
        }
        catch (Exception exception)
        {
            Settings.SetStatus(exception.Message);
            return new GrassTaskExecutionResult(false, false, exception.Message);
        }
    }

    public async Task<GrassTaskExecutionResult> StopAsync(
        GrassTaskExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Connection is not { } connection)
        {
            return new GrassTaskExecutionResult(
                true,
                false,
                Localize("GrassCareerTrainingStopRequested", "Career training stop requested."));
        }

        return await StopPipelineAsync(connection, context.LogSink, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<GrassTaskExecutionResult> StopPipelineAsync(
        LastVerifiedConnection connection,
        IGrassTaskLogSink? logSink,
        CancellationToken cancellationToken)
    {
        var result = await _pipeline.StopAsync(connection, logSink, cancellationToken)
            .ConfigureAwait(false);
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

    private static bool ReadBool(JsonObject settings, string key, bool fallback)
    {
        try { return settings[key]?.GetValue<bool>() ?? fallback; }
        catch (InvalidOperationException) { return fallback; }
        catch (FormatException) { return fallback; }
    }

    private static string[] ReadStringArray(JsonObject settings, string key)
    {
        if (settings[key] is not JsonArray values)
            return [];

        return values
            .Select(item =>
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

    private static string MigrateManifestPath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        const string legacyPrefix = "resource/uma/scenarios/ura/";
        var isLegacyRelative = normalized.StartsWith(
            legacyPrefix,
            StringComparison.OrdinalIgnoreCase);
        var isLegacyAbsolute = normalized.EndsWith(
            "/resource/uma/scenarios/ura/manifest.json",
            StringComparison.OrdinalIgnoreCase);
        return isLegacyRelative || isLegacyAbsolute
            ? CareerTrainingTaskSettingsViewModel.DefaultManifestPath
            : path.Trim();
    }
}
