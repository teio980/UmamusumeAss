using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Umamusume.AppCore.Models;

public sealed class HachimiSettings
{
    private HachimiShopSettings _shop = new();
    private List<HachimiTaskQueueItem> _taskQueue = [];

    public HachimiShopSettings Shop
    {
        get => _shop;
        set => _shop = value ?? new HachimiShopSettings();
    }

    /// <summary>Task profiles in execution order, matching the WPF TaskQueue item shape.</summary>
    public List<HachimiTaskQueueItem> TaskQueue
    {
        get => _taskQueue;
        set => _taskQueue = value ?? [];
    }

    /// <summary>The task profile currently selected for editing in the task page.</summary>
    public string? SelectedTaskId { get; set; }

    /// <summary>Selected queue row, used to distinguish repeated profiles of one task type.</summary>
    public int? SelectedTaskIndex { get; set; }
}

/// <summary>Platform-neutral equivalent of WPF GrassTaskCacheItem.</summary>
public sealed class HachimiTaskQueueItem
{
    public string TaskId { get; set; } = "";

    public bool IsEnabled { get; set; } = true;

    public JsonObject Settings { get; set; } = new();
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
