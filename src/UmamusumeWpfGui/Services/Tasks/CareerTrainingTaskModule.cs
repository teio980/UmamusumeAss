using System.IO;
using System.Globalization;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Training;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

public sealed class CareerTrainingTaskModule : IGrassTaskModule, IGrassTaskPreflightDiagnostics
{
    private readonly ILocalizationService _localizationService;
    private readonly ICareerTrainingPipeline _normalPipeline;
    private readonly IIndependentTrainingPipeline _independentPipeline;
    private readonly IUmaDatabaseService _umaDatabase;

    public CareerTrainingTaskModule(
        ILocalizationService localizationService,
        ICareerTrainingPipeline normalPipeline,
        IIndependentTrainingPipeline independentPipeline,
        IUmaDatabaseService umaDatabase)
    {
        ArgumentNullException.ThrowIfNull(localizationService);
        ArgumentNullException.ThrowIfNull(normalPipeline);
        ArgumentNullException.ThrowIfNull(independentPipeline);
        ArgumentNullException.ThrowIfNull(umaDatabase);
        _localizationService = localizationService;
        _normalPipeline = normalPipeline;
        _independentPipeline = independentPipeline;
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

    public System.Text.Json.Nodes.JsonObject ExportSettings() => CareerTaskSettingsSerializer.Export(Settings);

    public void ImportSettings(System.Text.Json.Nodes.JsonObject settings) => CareerTaskSettingsSerializer.Import(Settings, settings);

    public IGrassTaskModule CreateInstance() => new CareerTrainingTaskModule(
        _localizationService,
        _normalPipeline,
        _independentPipeline,
        _umaDatabase);

    public bool CanExecute(GrassTaskExecutionContext context) =>
        GetCannotExecuteReason(context) is null;

    public string? GetCannotExecuteReason(GrassTaskExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Connection is null)
            return "No verified ADB connection is available.";

        if (string.IsNullOrWhiteSpace(Settings.ManifestPath))
            return "Career manifest path is empty.";

        try
        {
            if (!File.Exists(ResourcePathRuntime.Resolve(Settings.ManifestPath)))
            {
                return $"Career manifest was not found at '{Settings.ManifestPath}'.";
            }
        }
        catch (ArgumentException)
        {
            return $"Career manifest path is invalid: '{Settings.ManifestPath}'.";
        }
        catch (NotSupportedException)
        {
            return $"Career manifest path is not supported: '{Settings.ManifestPath}'.";
        }

        if (Settings.TraineeId is not > 0)
            return "No trainee is configured.";

        if (!Settings.IsKnownCareerMode)
        {
            return $"Unknown Career mode '{Settings.CareerMode}'.";
        }

        if (!Settings.IsIndependentCareer
            && (string.IsNullOrWhiteSpace(Settings.StrategyId)
                || !UraStrategyRegistry.IsRegistered(Settings.StrategyId)))
        {
            return $"Normal training strategy '{Settings.StrategyId}' is not registered for this build.";
        }

        if (Settings.IsNormalCareer
            && !CareerStrategyCatalog.TryGetLineupStrategyUiMapping(
                Settings.NormalLineupStrategy,
                out _))
        {
            return $"Normal Career lineup strategy '{Settings.NormalLineupStrategy}' is invalid.";
        }

        if (Settings.IsManualSupportDeck
            && Settings.SelectedSupportCardCount is not (5 or 6))
        {
            return $"Support deck mode 'selected' requires 5 or 6 own support cards; "
                + $"configured {Settings.SelectedSupportCardCount}.";
        }

        if (Settings.IsHighestStarSupportDeck
            && Settings.SupportDeckPreset.Equals("custom", StringComparison.OrdinalIgnoreCase))
        {
            return "Highest-star support selection requires a support deck preset.";
        }

        if (!Settings.IsSupportDeckValid)
            return "Support deck settings are invalid.";

        if (Settings.IsIndependentCareer && !Settings.IsIndependentTrainingSettingsValid)
        {
            return $"Independent Training settings are invalid (focus='{Settings.IndependentTrainingFocus}', "
                + $"lineup='{Settings.IndependentLineupStrategy}', "
                + $"agenda={Settings.SelectedIndependentAgendaCount}, "
                + $"skills={Settings.SelectedIndependentSkillCount}).";
        }

        if (!_umaDatabase.TryGetTrainee(Settings.TraineeId.Value, out var trainee)
            || trainee is null
            || !trainee.Available)
        {
            return $"Configured trainee ID {Settings.TraineeId.Value.ToString(CultureInfo.InvariantCulture)} "
                + "was not found or is unavailable.";
        }

        return null;
    }

    public async Task<GrassTaskExecutionResult> ExecuteAsync(
        GrassTaskExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var cannotExecuteReason = GetCannotExecuteReason(context);
        if (cannotExecuteReason is not null || context.Connection is not { } connection)
        {
            var message = cannotExecuteReason ?? Localize(
                    "GrassCareerTrainingConnectionRequired",
                    "Connect a device and configure a valid career training profile first.");
            Settings.SetStatus(message);
            return new GrassTaskExecutionResult(false, false, message);
        }

        try
        {
            var deleteExistingCareerData = Settings.DeleteExistingCareerData;
            context.LogSink?.Add(
                "Career Training",
                deleteExistingCareerData
                    ? "Career entry policy: Delete Career data, then start fresh."
                    : "Career entry policy: Resume existing Career.");
            context.TaskLogSink?.Add(
                "Setup",
                deleteExistingCareerData
                    ? "Career entry: delete existing data and start a new career."
                    : "Career entry: resume the existing career.",
                HachimiTaskLogEventKind.Action);
            context.LogSink?.Add(
                "Career Training",
                $"Career mode selected: {Settings.CareerMode}."
                    + (Settings.IsIndependentCareer
                        ? " Independent setup will be applied after support selection."
                        : " Normal Career will use the final Start action."));

            if (!Settings.IsIndependentCareer)
            {
                var normalResult = await _normalPipeline.RunAsync(
                        connection,
                        CareerTaskSettingsMapper.ToNormalSettings(Settings),
                        context.LogSink,
                        context.TaskLogSink,
                        cancellationToken)
                    .ConfigureAwait(false);
                Settings.SetStatus(normalResult.Message);
                return new GrassTaskExecutionResult(
                    normalResult.Succeeded,
                    false,
                    normalResult.Message);
            }

            var result = await _independentPipeline.RunAsync(
                    connection,
                    CareerTaskSettingsMapper.ToIndependentSettings(Settings),
                    context.LogSink,
                    context.TaskLogSink,
                    cancellationToken)
                .ConfigureAwait(false);
            Settings.SetStatus(result.Message);
            return new GrassTaskExecutionResult(result.Succeeded, false, result.Message);
        }
        catch (OperationCanceledException)
        {
            var message = Localize("GrassCareerTrainingCanceled", "Career training canceled.");
            context.TaskLogSink?.Add("Result", message, HachimiTaskLogEventKind.Warning);
            Settings.SetStatus(message);
            return new GrassTaskExecutionResult(false, false, message);
        }
        catch (Exception exception)
        {
            context.TaskLogSink?.Add(
                "Result",
                HachimiTaskLogSemantics.ToUserFacingFailure(exception.Message),
                HachimiTaskLogEventKind.Failure);
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

        if (!Settings.IsKnownCareerMode)
        {
            var message = $"Unknown Career mode '{Settings.CareerMode}'.";
            Settings.SetStatus(message);
            return new GrassTaskExecutionResult(false, false, message);
        }

        if (Settings.IsIndependentCareer)
        {
            var result = await _independentPipeline.StopAsync(
                    connection,
                    context.LogSink,
                    context.TaskLogSink,
                    cancellationToken)
                .ConfigureAwait(false);
            return new GrassTaskExecutionResult(result.Succeeded, false, result.Message);
        }

        var normalResult = await _normalPipeline.StopAsync(
                connection,
                context.LogSink,
                context.TaskLogSink,
                cancellationToken)
            .ConfigureAwait(false);
        return new GrassTaskExecutionResult(normalResult.Succeeded, false, normalResult.Message);
    }

    private string Localize(string key, string fallback)
    {
        var value = _localizationService.GetString(key);
        return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value;
    }

}
