using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

public interface IShopPipeline
{
    Task<ShopPipelineResult> RunIfPresentAsync(
        LastVerifiedConnection connection,
        string definitionPath,
        ShopPurchaseOptions options,
        IGrassTaskLogSink? logSink = null,
        CancellationToken cancellationToken = default);
}

public sealed record ShopPipelineResult(
    bool Succeeded,
    string Message);
