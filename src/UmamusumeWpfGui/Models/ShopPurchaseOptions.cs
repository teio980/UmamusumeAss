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
    public bool IsSlotSelected(int slot) => slot switch
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
