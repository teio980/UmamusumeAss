namespace UmamusumeWpfGui.Models;

public sealed record ShopPurchaseOptions(
    bool SelectAll,
    bool BuyStarPieces,
    bool BuyAlarmClock,
    bool BuyPleasingParfait,
    bool BuyShoes,
    bool BuySupportPoints,
    bool BuyFlags)
{
    public bool HasIndividualSelections => BuyStarPieces || BuyAlarmClock
        || BuyPleasingParfait || BuyShoes || BuySupportPoints || BuyFlags;

    public IReadOnlyDictionary<string, int> ToMaxTimesOverrides()
    {
        var overrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!SelectAll)
            overrides["shopSelectAll"] = 0;
        return overrides;
    }

    internal bool IsSelected(ShopItemCategory category) => category switch
    {
        ShopItemCategory.StarPieces => BuyStarPieces,
        ShopItemCategory.AlarmClock => BuyAlarmClock,
        ShopItemCategory.PleasingParfait => BuyPleasingParfait,
        ShopItemCategory.Shoes => BuyShoes,
        ShopItemCategory.SupportPoints => BuySupportPoints,
        ShopItemCategory.Flags => BuyFlags,
        _ => false,
    };
}

internal enum ShopItemCategory
{
    StarPieces,
    AlarmClock,
    PleasingParfait,
    Shoes,
    SupportPoints,
    Flags,
}
