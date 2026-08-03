using System.Globalization;
using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

public sealed class ShopTaskModule : IGrassTaskModule
{
    private readonly ILocalizationService _localizationService;
    private readonly IShopPipeline _pipeline;

    public ShopTaskModule(
        ILocalizationService localizationService,
        IShopPipeline pipeline,
        ShopTaskSettingsViewModel settings)
    {
        ArgumentNullException.ThrowIfNull(localizationService);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(settings);
        _localizationService = localizationService;
        _pipeline = pipeline;
        Settings = settings;
    }

    public GrassTaskDefinition Definition { get; } = new(
        "shop",
        "GrassTaskFriendsShop",
        "GrassTaskFriendsShopDescription",
        "Shop",
        "Check and purchase configured shop items");

    public ShopTaskSettingsViewModel Settings { get; }

    object IGrassTaskModule.Settings => Settings;

    public JsonObject ExportSettings() => new()
    {
        ["definitionPath"] = Settings.DefinitionPath,
        ["selectAll"] = Settings.SelectAll,
        ["buyStarPieces"] = Settings.BuyStarPieces,
        ["buyAlarmClock"] = Settings.BuyAlarmClock,
        ["buyPleasingParfait"] = Settings.BuyPleasingParfait,
        ["buyShoes"] = Settings.BuyShoes,
        ["buySupportPoints"] = Settings.BuySupportPoints,
        ["buyFlags"] = Settings.BuyFlags,
    };

    public void ImportSettings(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings.DefinitionPath = ReadString(settings, "definitionPath") ?? Settings.DefinitionPath;
        Settings.SelectAll = ReadBool(settings, "selectAll", Settings.SelectAll);
        Settings.BuyStarPieces = ReadBool(settings, "buyStarPieces", Settings.BuyStarPieces);
        Settings.BuyAlarmClock = ReadBool(settings, "buyAlarmClock", Settings.BuyAlarmClock);
        Settings.BuyPleasingParfait = ReadBool(settings, "buyPleasingParfait", Settings.BuyPleasingParfait);
        Settings.BuyShoes = ReadBool(settings, "buyShoes", Settings.BuyShoes);
        Settings.BuySupportPoints = ReadBool(settings, "buySupportPoints", Settings.BuySupportPoints);
        Settings.BuyFlags = ReadBool(settings, "buyFlags", Settings.BuyFlags);
    }

    public IGrassTaskModule CreateInstance() => new ShopTaskModule(
        _localizationService,
        _pipeline,
        Settings);

    public bool CanExecute(GrassTaskExecutionContext context) =>
        context.Connection is not null
        && !string.IsNullOrWhiteSpace(Settings.DefinitionPath);

    public async Task<GrassTaskExecutionResult> ExecuteAsync(
        GrassTaskExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!CanExecute(context) || context.Connection is not { } connection)
        {
            var message = Localize("GrassShopConnectionRequired", "Connect a device before checking the shop.");
            Settings.SetStatus(message);
            return new GrassTaskExecutionResult(false, false, message);
        }

        var result = await _pipeline.RunIfPresentAsync(
                connection,
                Settings.DefinitionPath,
                Settings.ToOptions(),
                context.LogSink,
                cancellationToken)
            .ConfigureAwait(false);
        Settings.SetStatus(result.Message);
        return new GrassTaskExecutionResult(result.Succeeded, false, result.Message);
    }

    public Task<GrassTaskExecutionResult> StopAsync(
        GrassTaskExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        Settings.SetStatus(Localize("GrassShopStopped", "Shop check stopped."));
        return Task.FromResult(new GrassTaskExecutionResult(true, false, Settings.Status));
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

    private static bool ReadBool(JsonObject settings, string key, bool fallback)
    {
        try { return settings[key]?.GetValue<bool>() ?? fallback; }
        catch (InvalidOperationException) { return fallback; }
        catch (FormatException) { return fallback; }
    }
}
