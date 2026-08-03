using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

public sealed class AdbShopPipeline : IShopPipeline
{
    private readonly HachimiJsonPipelineRunner _runner;

    public AdbShopPipeline(HachimiJsonPipelineRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    public async Task<ShopPipelineResult> RunIfPresentAsync(
        LastVerifiedConnection connection,
        string definitionPath,
        ShopPurchaseOptions options,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionPath);

        var overrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!options.SelectAll)
            overrides["shopSelectAll"] = 0;

        for (var slot = 1; slot <= 7; slot++)
        {
            if (!options.SelectAll && !options.IsSlotSelected(slot))
                overrides[$"shopBuy{slot}"] = 0;
        }

        var result = await _runner.RunAsync(
                connection,
                definitionPath,
                "shopProbe",
                new HachimiPipelineRunOptions
                {
                    MaxTimesOverrides = overrides,
                },
                logSink,
                cancellationToken)
            .ConfigureAwait(false);

        return new ShopPipelineResult(
            result.Succeeded,
            result.Succeeded
                ? "Shop checked and handled when present."
                : result.Message);
    }
}
