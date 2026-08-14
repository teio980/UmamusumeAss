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
    public IReadOnlyDictionary<string, int> ToMaxTimesOverrides()
    {
        var overrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!SelectAll)
            overrides["shopSelectAll"] = 0;

        for (var slot = 1; slot <= 21; slot++)
        {
            if (!SelectAll && !IsSlotSelected(slot))
                overrides[$"shopBuy{slot}"] = 0;
        }

        return overrides;
    }

    public bool IsSlotSelected(int slot) => ((slot - 1) % 7 + 1) switch
    {
        1 or 2 => BuyStarPieces,
        3 => BuyAlarmClock,
        4 => BuyPleasingParfait,
        5 => BuyShoes,
        6 => BuySupportPoints,
        7 => BuyFlags,
        _ => false,
    };
}
