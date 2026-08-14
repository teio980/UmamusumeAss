using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.ViewModels;

public sealed class HachimiShopSettingsViewModel : INotifyPropertyChanged
{
    private readonly ISettingsService? _settingsService;
    private readonly HachimiShopSettings _settings;

    public HachimiShopSettingsViewModel(ISettingsService? settingsService = null)
    {
        _settingsService = settingsService;
        _settings = settingsService?.Load().Hachimi.Shop ?? new HachimiShopSettings();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Enabled
    {
        get => _settings.Enabled;
        set => SetValue(_settings.Enabled, value, next => _settings.Enabled = next);
    }

    public bool SelectAll
    {
        get => _settings.SelectAll;
        set => SetValue(_settings.SelectAll, value, next => _settings.SelectAll = next);
    }

    public bool BuyStarPieces
    {
        get => _settings.BuyStarPieces;
        set => SetValue(_settings.BuyStarPieces, value, next => _settings.BuyStarPieces = next);
    }

    public bool BuyAlarmClock
    {
        get => _settings.BuyAlarmClock;
        set => SetValue(_settings.BuyAlarmClock, value, next => _settings.BuyAlarmClock = next);
    }

    public bool BuyPleasingParfait
    {
        get => _settings.BuyPleasingParfait;
        set => SetValue(_settings.BuyPleasingParfait, value, next => _settings.BuyPleasingParfait = next);
    }

    public bool BuyShoes
    {
        get => _settings.BuyShoes;
        set => SetValue(_settings.BuyShoes, value, next => _settings.BuyShoes = next);
    }

    public bool BuySupportPoints
    {
        get => _settings.BuySupportPoints;
        set => SetValue(_settings.BuySupportPoints, value, next => _settings.BuySupportPoints = next);
    }

    public bool BuyFlags
    {
        get => _settings.BuyFlags;
        set => SetValue(_settings.BuyFlags, value, next => _settings.BuyFlags = next);
    }

    internal bool IsDefault =>
        _settings.Enabled
        && !_settings.SelectAll
        && !_settings.BuyStarPieces
        && !_settings.BuyAlarmClock
        && !_settings.BuyPleasingParfait
        && !_settings.BuyShoes
        && !_settings.BuySupportPoints
        && !_settings.BuyFlags;

    internal void ImportLegacySettings(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SetIfPresent(settings, "selectAll", value => SelectAll = value);
        SetIfPresent(settings, "buyStarPieces", value => BuyStarPieces = value);
        SetIfPresent(settings, "buyAlarmClock", value => BuyAlarmClock = value);
        SetIfPresent(settings, "buyPleasingParfait", value => BuyPleasingParfait = value);
        SetIfPresent(settings, "buyShoes", value => BuyShoes = value);
        SetIfPresent(settings, "buySupportPoints", value => BuySupportPoints = value);
        SetIfPresent(settings, "buyFlags", value => BuyFlags = value);
    }

    private void SetValue<T>(T current, T value, Action<T> assign, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
            return;
        assign(value);
        OnPropertyChanged(propertyName);
        Persist();
    }

    private void Persist()
    {
        if (_settingsService is null)
            return;

        var root = _settingsService.Load();
        root.Hachimi.Shop = CopySettings();
        _settingsService.Save(root);
    }

    private HachimiShopSettings CopySettings() => new()
    {
        Enabled = _settings.Enabled,
        SelectAll = _settings.SelectAll,
        BuyStarPieces = _settings.BuyStarPieces,
        BuyAlarmClock = _settings.BuyAlarmClock,
        BuyPleasingParfait = _settings.BuyPleasingParfait,
        BuyShoes = _settings.BuyShoes,
        BuySupportPoints = _settings.BuySupportPoints,
        BuyFlags = _settings.BuyFlags,
    };

    private static void SetIfPresent(JsonObject settings, string key, Action<bool> setter)
    {
        try
        {
            if (settings[key] is JsonValue value && value.TryGetValue<bool>(out var result))
                setter(result);
        }
        catch (InvalidOperationException)
        {
        }
        catch (FormatException)
        {
        }
    }

    private static void SetIfPresent(JsonObject settings, string key, Action<string> setter)
    {
        try
        {
            if (settings[key] is JsonValue value && value.TryGetValue<string>(out var result)
                && result is not null)
            {
                setter(result);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (FormatException)
        {
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
