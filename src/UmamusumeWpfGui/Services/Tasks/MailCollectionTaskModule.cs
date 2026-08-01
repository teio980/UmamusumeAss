using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

public sealed class MailCollectionTaskModule : IGrassTaskModule
{
    private readonly ILocalizationService _localizationService;
    private readonly IMailCollectionPipeline _pipeline;

    public MailCollectionTaskModule(
        ILocalizationService localizationService,
        IMailCollectionPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(localizationService);
        ArgumentNullException.ThrowIfNull(pipeline);
        _localizationService = localizationService;
        _pipeline = pipeline;
        Settings = new MailCollectionTaskSettingsViewModel();
    }

    public GrassTaskDefinition Definition { get; } = new(
        "mail-collection",
        "GrassTaskRewardsCollection",
        "GrassTaskRewardsCollectionDescription",
        "Mail collection",
        "Collect all available presents from the in-game mailbox");

    public MailCollectionTaskSettingsViewModel Settings { get; }

    object IGrassTaskModule.Settings => Settings;

    public JsonObject ExportSettings() => new()
    {
        ["definitionPath"] = Settings.DefinitionPath,
    };

    public void ImportSettings(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings.DefinitionPath = ReadString(settings, "definitionPath")
            ?? Settings.DefinitionPath;
    }

    public IGrassTaskModule CreateInstance() => new MailCollectionTaskModule(
        _localizationService,
        _pipeline);

    public bool CanExecute(GrassTaskExecutionContext context) =>
        context.Connection is not null
        && !string.IsNullOrWhiteSpace(Settings.DefinitionPath);

    public async Task<GrassTaskExecutionResult> ExecuteAsync(
        GrassTaskExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!CanExecute(context) || context.Connection is not { } connection)
        {
            var message = Localize(
                "GrassMailCollectionConnectionRequired",
                "Connect a device before collecting mailbox rewards");
            Settings.SetStatus(message);
            return new GrassTaskExecutionResult(false, false, message);
        }

        Settings.SetStatus(Localize(
            "GrassMailCollectionStarting",
            "Collecting mailbox rewards"));
        context.LogSink?.Add(
            Localize("GrassTaskRewardsCollection", "Mail collection"),
            string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                Localize(
                    "GrassMailCollectionRunning",
                    "Collecting rewards using {0}"),
                Settings.DefinitionPath));

        var result = await _pipeline.RunAsync(
                connection,
                Settings.DefinitionPath,
                context.LogSink,
                cancellationToken)
            .ConfigureAwait(false);
        Settings.SetStatus(result.Succeeded
            ? Localize("GrassMailCollectionCompleted", "Mailbox rewards collected")
            : result.Message);
        return new GrassTaskExecutionResult(result.Succeeded, false, result.Message);
    }

    public async Task<GrassTaskExecutionResult> StopAsync(
        GrassTaskExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Connection is not { } connection)
        {
            var message = Localize(
                "GrassMailCollectionConnectionRequired",
                "Connect a device before collecting mailbox rewards");
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
