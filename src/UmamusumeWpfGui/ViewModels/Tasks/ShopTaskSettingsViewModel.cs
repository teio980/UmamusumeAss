using System.ComponentModel;
using System.Runtime.CompilerServices;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.ViewModels.Tasks;

public sealed class ShopTaskSettingsViewModel : INotifyPropertyChanged
{
    public const string DefaultDefinitionPath = "resource/hachimi/shop.json";

    private string _definitionPath = DefaultDefinitionPath;
    private bool _selectAll;
    private bool _buyStarPieces;
    private bool _buyAlarmClock;
    private bool _buyPleasingParfait;
    private bool _buyShoes;
    private bool _buySupportPoints;
    private bool _buyFlags;
    private string _status = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DefinitionPath
    {
        get => _definitionPath;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (_definitionPath == normalized)
                return;
            _definitionPath = normalized;
            OnPropertyChanged();
        }
    }

    public bool SelectAll { get => _selectAll; set => Set(ref _selectAll, value); }
    public bool BuyStarPieces { get => _buyStarPieces; set => Set(ref _buyStarPieces, value); }
    public bool BuyAlarmClock { get => _buyAlarmClock; set => Set(ref _buyAlarmClock, value); }
    public bool BuyPleasingParfait { get => _buyPleasingParfait; set => Set(ref _buyPleasingParfait, value); }
    public bool BuyShoes { get => _buyShoes; set => Set(ref _buyShoes, value); }
    public bool BuySupportPoints { get => _buySupportPoints; set => Set(ref _buySupportPoints, value); }
    public bool BuyFlags { get => _buyFlags; set => Set(ref _buyFlags, value); }

    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value)
                return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public ShopPurchaseOptions ToOptions() => new(
        SelectAll,
        BuyStarPieces,
        BuyAlarmClock,
        BuyPleasingParfait,
        BuyShoes,
        BuySupportPoints,
        BuyFlags);

    internal void SetStatus(string status) => Status = status;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
