namespace UmamusumeWpfGui.Models;

public sealed class HachimiSettings
{
    private HachimiShopSettings _shop = new();

    public HachimiShopSettings Shop
    {
        get => _shop;
        set => _shop = value ?? new HachimiShopSettings();
    }
}

public sealed class HachimiShopSettings
{
    public bool Enabled { get; set; } = true;

    public bool SelectAll { get; set; }

    public bool BuyStarPieces { get; set; }

    public bool BuyAlarmClock { get; set; }

    public bool BuyPleasingParfait { get; set; }

    public bool BuyShoes { get; set; }

    public bool BuySupportPoints { get; set; }

    public bool BuyFlags { get; set; }

    public ShopPurchaseOptions ToOptions() => new(
        SelectAll,
        BuyStarPieces,
        BuyAlarmClock,
        BuyPleasingParfait,
        BuyShoes,
        BuySupportPoints,
        BuyFlags);
}
