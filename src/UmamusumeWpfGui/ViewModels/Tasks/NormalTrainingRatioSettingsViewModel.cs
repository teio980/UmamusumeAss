using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.ViewModels.Tasks;

/// <summary>
/// Editable Normal Career ratio fields. Text is retained while editing so an
/// invalid value can be shown and reported by the parent settings model.
/// </summary>
public sealed class NormalTrainingRatioSettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ILocalizationService? _localizationService;
    private string _speedText = UraTrainingRatio.Default.Speed.ToString(CultureInfo.InvariantCulture);
    private string _staminaText = UraTrainingRatio.Default.Stamina.ToString(CultureInfo.InvariantCulture);
    private string _powerText = UraTrainingRatio.Default.Power.ToString(CultureInfo.InvariantCulture);
    private string _gutsText = UraTrainingRatio.Default.Guts.ToString(CultureInfo.InvariantCulture);
    private string _witText = UraTrainingRatio.Default.Wit.ToString(CultureInfo.InvariantCulture);

    public NormalTrainingRatioSettingsViewModel(ILocalizationService? localizationService = null)
    {
        _localizationService = localizationService;
        if (_localizationService is not null)
            _localizationService.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SpeedText { get => _speedText; set => SetText(ref _speedText, value); }
    public string StaminaText { get => _staminaText; set => SetText(ref _staminaText, value); }
    public string PowerText { get => _powerText; set => SetText(ref _powerText, value); }
    public string GutsText { get => _gutsText; set => SetText(ref _gutsText, value); }
    public string WitText { get => _witText; set => SetText(ref _witText, value); }

    public int Speed => Parse(SpeedText);
    public int Stamina => Parse(StaminaText);
    public int Power => Parse(PowerText);
    public int Guts => Parse(GutsText);
    public int Wit => Parse(WitText);

    public bool IsValid =>
        TryParse(SpeedText, out var speed)
        && TryParse(StaminaText, out var stamina)
        && TryParse(PowerText, out var power)
        && TryParse(GutsText, out var guts)
        && TryParse(WitText, out var wit)
        && speed is >= 0 and <= 100
        && stamina is >= 0 and <= 100
        && power is >= 0 and <= 100
        && guts is >= 0 and <= 100
        && wit is >= 0 and <= 100
        && speed + stamina + power + guts + wit > 0;

    public bool IsInvalid => !IsValid;

    public string SpeedPercentage => FormatPercentage(Speed);
    public string StaminaPercentage => FormatPercentage(Stamina);
    public string PowerPercentage => FormatPercentage(Power);
    public string GutsPercentage => FormatPercentage(Guts);
    public string WitPercentage => FormatPercentage(Wit);

    public string CyclePreview
    {
        get
        {
            if (!IsValid)
                return "—";

            var cycle = ToRatio().BuildCycle();
            const int previewLimit = 12;
            var preview = cycle
                .Take(previewLimit)
                .Select(DisplayName);
            var text = string.Join(" → ", preview);
            return cycle.Count > previewLimit
                ? $"{text} … {FormatTotal(cycle.Count)}"
                : text;
        }
    }

    public UraTrainingRatio ToRatio() => new(Speed, Stamina, Power, Guts, Wit);

    public void SetRatio(UraTrainingRatio ratio)
    {
        ArgumentNullException.ThrowIfNull(ratio);
        SpeedText = ratio.Speed.ToString(CultureInfo.InvariantCulture);
        StaminaText = ratio.Stamina.ToString(CultureInfo.InvariantCulture);
        PowerText = ratio.Power.ToString(CultureInfo.InvariantCulture);
        GutsText = ratio.Guts.ToString(CultureInfo.InvariantCulture);
        WitText = ratio.Wit.ToString(CultureInfo.InvariantCulture);
    }

    private void SetText(ref string field, string? value, [CallerMemberName] string? propertyName = null)
    {
        var normalized = value ?? string.Empty;
        if (string.Equals(field, normalized, StringComparison.Ordinal))
            return;

        field = normalized;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(Speed));
        OnPropertyChanged(nameof(Stamina));
        OnPropertyChanged(nameof(Power));
        OnPropertyChanged(nameof(Guts));
        OnPropertyChanged(nameof(Wit));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(IsInvalid));
        OnPropertyChanged(nameof(SpeedPercentage));
        OnPropertyChanged(nameof(StaminaPercentage));
        OnPropertyChanged(nameof(PowerPercentage));
        OnPropertyChanged(nameof(GutsPercentage));
        OnPropertyChanged(nameof(WitPercentage));
        OnPropertyChanged(nameof(CyclePreview));
    }

    private string FormatPercentage(int value)
    {
        if (!IsWithinRange(SpeedText)
            || !IsWithinRange(StaminaText)
            || !IsWithinRange(PowerText)
            || !IsWithinRange(GutsText)
            || !IsWithinRange(WitText))
        {
            return "—";
        }

        var total = Speed + Stamina + Power + Guts + Wit;
        return total > 0
            ? $"{value * 100d / total:0.#}%"
            : "—";
    }

    private static bool IsWithinRange(string? text) =>
        TryParse(text, out var value) && value is >= 0 and <= 100;

    private string DisplayName(string trainingType)
    {
        var key = trainingType switch
        {
            "speed" => "GrassNormalTrainingSpeedLabel",
            "stamina" => "GrassNormalTrainingStaminaLabel",
            "power" => "GrassNormalTrainingPowerLabel",
            "guts" => "GrassNormalTrainingGutsLabel",
            "wit" => "GrassNormalTrainingWitLabel",
            _ => trainingType,
        };
        var localized = _localizationService?.GetString(key);
        return string.IsNullOrWhiteSpace(localized) || localized.Equals(key, StringComparison.Ordinal)
            ? trainingType switch
            {
                "speed" => "Speed",
                "stamina" => "Stamina",
                "power" => "Power",
                "guts" => "Guts",
                "wit" => "Wit",
                _ => trainingType,
            }
            : localized;
    }

    private string FormatTotal(int count)
    {
        var template = _localizationService?.GetString("GrassNormalTrainingCycleTotal");
        if (string.IsNullOrWhiteSpace(template)
            || template.Equals("GrassNormalTrainingCycleTotal", StringComparison.Ordinal))
            template = "({0} total)";
        return string.Format(CultureInfo.CurrentCulture, template, count);
    }

    public void Dispose()
    {
        if (_localizationService is not null)
            _localizationService.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, string culture) =>
        OnPropertyChanged(nameof(CyclePreview));

    private static int Parse(string? text) =>
        TryParse(text, out var value) ? value : 0;

    private static bool TryParse(string? text, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
