namespace Umamusume.AppCore.Models;

public sealed record ShopPurchaseOptions(
    bool SelectAll,
    bool BuyStarPieces,
    bool BuyAlarmClock,
    bool BuyPleasingParfait,
    bool BuyShoes,
    bool BuySupportPoints,
    bool BuyFlags);
