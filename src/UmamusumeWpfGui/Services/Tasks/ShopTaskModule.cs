using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

public sealed class ShopTaskModule : IGrassTaskModule
{
    public object Settings { get; } = new();

    public GrassTaskDefinition Definition { get; } = new(
        "shop-purchase", "GrassTaskFriendsShop", "GrassTaskFriendsShopDescription",
        "Friends & Shop", "Open the shop and purchase configured items");

    public JsonObject ExportSettings() => new();

    public void ImportSettings(JsonObject settings) => ArgumentNullException.ThrowIfNull(settings);

    public IGrassTaskModule CreateInstance() => new ShopTaskModule();
}
