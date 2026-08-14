using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

public sealed class ShopTaskModule : IGrassTaskModule
{
    private const string DefinitionPath = "resource/hachimi/shop_task.json";
    private readonly HachimiJsonPipelineRunner _runner;

    public ShopTaskModule(HachimiJsonPipelineRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        Settings = new object();
    }

    public GrassTaskDefinition Definition { get; } = new(
        "shop-purchase",
        "GrassTaskFriendsShop",
        "GrassTaskFriendsShopDescription",
        "Friends & Shop",
        "Open the shop and purchase the configured items");

    public object Settings { get; }

    public JsonObject ExportSettings() => new();

    public void ImportSettings(JsonObject settings) => ArgumentNullException.ThrowIfNull(settings);

    public IGrassTaskModule CreateInstance() => new ShopTaskModule(_runner);

    public bool CanExecute(GrassTaskExecutionContext context) => context.Connection is not null;

    public async Task<GrassTaskExecutionResult> ExecuteAsync(
        GrassTaskExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Connection is not { } connection)
            return new(false, false, "Connect a device before opening the shop.");

        var result = await _runner.RunAsync(
                connection,
                DefinitionPath,
                "home",
                logSink: context.LogSink,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new(result.Succeeded, false, result.Message);
    }

    public Task<GrassTaskExecutionResult> StopAsync(
        GrassTaskExecutionContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new GrassTaskExecutionResult(true, false, "Stop requested."));
}
