using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Services;
using Umamusume.Avalonia.Services;

namespace Umamusume.Avalonia.ViewModels;

/// <summary>Editable global Hachimi shop preferences shared by shop tasks.</summary>
public sealed class ShopTaskSettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ILocalizationService? _localizationService;
    private HachimiShopSettings _settings;
    private string _errorText = string.Empty;

    public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool Enabled { get => _settings.Enabled; set => SetValue(_settings.Enabled, value, v => _settings.Enabled = v, nameof(Enabled)); }
    public bool SelectAll { get => _settings.SelectAll; set => SetValue(_settings.SelectAll, value, v => _settings.SelectAll = v, nameof(SelectAll)); }
    public bool BuyStarPieces { get => _settings.BuyStarPieces; set => SetValue(_settings.BuyStarPieces, value, v => _settings.BuyStarPieces = v, nameof(BuyStarPieces)); }
    public bool BuyAlarmClock { get => _settings.BuyAlarmClock; set => SetValue(_settings.BuyAlarmClock, value, v => _settings.BuyAlarmClock = v, nameof(BuyAlarmClock)); }
    public bool BuyPleasingParfait { get => _settings.BuyPleasingParfait; set => SetValue(_settings.BuyPleasingParfait, value, v => _settings.BuyPleasingParfait = v, nameof(BuyPleasingParfait)); }
    public bool BuyShoes { get => _settings.BuyShoes; set => SetValue(_settings.BuyShoes, value, v => _settings.BuyShoes = v, nameof(BuyShoes)); }
    public bool BuySupportPoints { get => _settings.BuySupportPoints; set => SetValue(_settings.BuySupportPoints, value, v => _settings.BuySupportPoints = v, nameof(BuySupportPoints)); }
    public bool BuyFlags { get => _settings.BuyFlags; set => SetValue(_settings.BuyFlags, value, v => _settings.BuyFlags = v, nameof(BuyFlags)); }

    public ShopTaskSettingsViewModel(ISettingsService settingsService, ILocalizationService? localizationService = null)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _settings = Clone(settingsService.Load().Hachimi.Shop);
    }

    private void SetValue(bool current, bool value, Action<bool> assign, string propertyName)
    {
        if (current == value)
            return;
        assign(value);
        OnPropertyChanged(propertyName);
        if (Persist())
            return;
        assign(current);
        OnPropertyChanged(propertyName);
    }

    private bool Persist()
    {
        try
        {
            var root = _settingsService.Load();
            root.Hachimi.Shop = Clone(_settings);
            _settingsService.Save(root);
            ErrorText = string.Empty;
            OnPropertyChanged(nameof(HasError));
            return true;
        }
        catch (Exception exception)
        {
            ErrorText = $"{UiText.Get(_localizationService, "商店设置保存失败", "Failed to save shop settings")}: {exception.Message}";
            OnPropertyChanged(nameof(HasError));
            return false;
        }
    }

    private static HachimiShopSettings Clone(HachimiShopSettings source) => new()
    {
        Enabled = source.Enabled,
        SelectAll = source.SelectAll,
        BuyStarPieces = source.BuyStarPieces,
        BuyAlarmClock = source.BuyAlarmClock,
        BuyPleasingParfait = source.BuyPleasingParfait,
        BuyShoes = source.BuyShoes,
        BuySupportPoints = source.BuySupportPoints,
        BuyFlags = source.BuyFlags,
    };
}
