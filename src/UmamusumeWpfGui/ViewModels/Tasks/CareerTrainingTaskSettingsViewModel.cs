using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.ViewModels.Tasks;

public sealed class CareerTrainingTaskSettingsViewModel : INotifyPropertyChanged
{
    public const string DefaultManifestPath = "resource/hachimi/ura/manifest.json";
    public const string DefaultStrategyId = "default-speed-medium";

    private readonly IUmaDatabaseService? _umaDatabase;
    private string _scenarioId = "ura";
    private string _manifestPath = DefaultManifestPath;
    private int? _traineeId = 100601;
    private string _supportCardIdsText = string.Empty;
    private string _supportDeckMode = "auto";
    private string _supportDeckPreset = "custom";
    private string _supportCardSearchText = string.Empty;
    private string _supportCardTypeFilter = "all";
    private bool _updatingSupportCards;
    private string _strategyId = DefaultStrategyId;
    private bool _pauseOnUnknownOutcome = true;
    private bool _allowOptionalRaces;
    private string _legacySelectionMode = "auto";
    private bool _useLegacyGuest;
    private string _status = string.Empty;
    private string _traineeSearchText = string.Empty;
    private bool _isTraineeDropDownOpen;
    private readonly List<CareerTraineeOption> _allTraineeOptions = [];
    private readonly List<CareerSupportCardOption> _allSupportCardOptions = [];

    public CareerTrainingTaskSettingsViewModel(IUmaDatabaseService? umaDatabase = null)
    {
        _umaDatabase = umaDatabase;
        if (_umaDatabase is not null)
            _umaDatabase.DatabaseLoaded += OnDatabaseLoaded;
        RefreshTrainees();
        RefreshSupportCards();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<CareerTraineeOption> TraineeOptions { get; } = [];

    public ObservableCollection<CareerTraineeOption> FilteredTraineeOptions { get; } = [];

    public ObservableCollection<CareerSupportCardOption> FilteredSupportCardOptions { get; } = [];

    public ObservableCollection<CareerSupportCardTypeOption> SupportCardTypeOptions { get; } = [];

    public IReadOnlyList<CareerSupportDeckPresetOption> SupportDeckPresets { get; } =
    [
        new("custom", "Custom"),
        new("speed3-stamina3", "3 Speed / 3 Stamina"),
        new("speed3-stamina2-wit1", "3 Speed / 2 Stamina / 1 Wit"),
        new("speed2-stamina2-power1-wit1", "2 Speed / 2 Stamina / 1 Power / 1 Wit"),
        new("speed2-stamina1-power1-wit1-friend1", "2 Speed / 1 Stamina / 1 Power / 1 Wit / 1 Friend"),
    ];

    public IReadOnlyList<CareerSupportDeckModeOption> SupportDeckModes { get; } =
    [
        new("auto", "Auto-Fill (game button)"),
        new("highest-star", "Highest-star preset"),
        new("selected", "Selected cards"),
    ];

    public IReadOnlyList<CareerLegacySelectionModeOption> LegacySelectionModes { get; } =
    [
        new("auto", "Auto-Select"),
        new("manual", "Select Legacy 1 and Legacy 2"),
    ];

    public ObservableCollection<CareerSparkOption> AttributeSparkOptions { get; } =
    [
        new("Speed", "Speed"),
        new("Stamina", "Stamina"),
        new("Power", "Power"),
        new("Guts", "Guts"),
        new("Wit", "Wit"),
    ];

    public ObservableCollection<CareerSparkOption> AptitudeSparkOptions { get; } =
    [
        new("Turf", "Turf"),
        new("Dirt", "Dirt"),
        new("Sprint", "Sprint"),
        new("Mile", "Mile"),
        new("Medium", "Medium"),
        new("Long", "Long"),
        new("Front", "Front"),
        new("Pace", "Pace"),
        new("Late", "Late"),
        new("End", "End"),
    ];

    public string TraineeSearchText
    {
        get => _traineeSearchText;
        set
        {
            var normalized = value ?? string.Empty;
            if (_traineeSearchText == normalized)
                return;
            _traineeSearchText = normalized;
            OnPropertyChanged();
            ApplyTraineeSearch();
        }
    }

    public bool IsTraineeDropDownOpen
    {
        get => _isTraineeDropDownOpen;
        set
        {
            if (_isTraineeDropDownOpen == value)
                return;
            _isTraineeDropDownOpen = value;
            OnPropertyChanged();
        }
    }

    public string ManifestPath
    {
        get => _manifestPath;
        set => Set(ref _manifestPath, value?.Trim() ?? string.Empty);
    }

    public string ScenarioId
    {
        get => _scenarioId;
        set => Set(ref _scenarioId, value?.Trim() ?? string.Empty);
    }

    public int? TraineeId
    {
        get => _traineeId;
        set
        {
            var normalized = value is > 0 ? value : null;
            if (Set(ref _traineeId, normalized))
                OnPropertyChanged(nameof(SelectedTrainee));
        }
    }

    public CareerTraineeOption? SelectedTrainee
    {
        get => TraineeOptions.FirstOrDefault(item => item.TraineeId == TraineeId)
            ?? TraineeOptions.FirstOrDefault();
        set => TraineeId = value?.TraineeId;
    }

    public string SupportCardIdsText
    {
        get => _supportCardIdsText;
        set
        {
            if (!Set(ref _supportCardIdsText, value?.Trim() ?? string.Empty))
                return;
            if (!_updatingSupportCards)
                ApplySupportCardIdsText();
            OnPropertyChanged(nameof(SelectedSupportCardCount));
            OnPropertyChanged(nameof(SelectedSupportCardCountText));
        }
    }

    public string SupportDeckMode
    {
        get => _supportDeckMode;
        set
        {
            var normalized = SupportDeckModes.Any(item =>
                    item.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
                ? value
                : "auto";
            if (!Set(ref _supportDeckMode, normalized))
                return;

            if (!IsManualSupportDeck)
            {
                _updatingSupportCards = true;
                foreach (var option in _allSupportCardOptions)
                    option.IsSelected = false;
                _updatingSupportCards = false;
                if (_supportCardIdsText.Length > 0)
                {
                    _supportCardIdsText = string.Empty;
                    OnPropertyChanged(nameof(SupportCardIdsText));
                }
                OnPropertyChanged(nameof(SelectedSupportCardCount));
                OnPropertyChanged(nameof(SelectedSupportCardCountText));
            }

            OnPropertyChanged(nameof(IsManualSupportDeck));
            OnPropertyChanged(nameof(IsSupportPresetMode));
            OnPropertyChanged(nameof(IsHighestStarSupportDeck));
        }
    }

    public bool IsManualSupportDeck =>
        SupportDeckMode.Equals("selected", StringComparison.OrdinalIgnoreCase);

    public bool IsSupportPresetMode =>
        !SupportDeckMode.Equals("auto", StringComparison.OrdinalIgnoreCase);

    public bool IsHighestStarSupportDeck =>
        SupportDeckMode.Equals("highest-star", StringComparison.OrdinalIgnoreCase);

    public string SupportCardSearchText
    {
        get => _supportCardSearchText;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (_supportCardSearchText == normalized)
                return;
            _supportCardSearchText = normalized;
            OnPropertyChanged();
            ApplySupportCardSearch();
        }
    }

    public string SupportCardTypeFilter
    {
        get => _supportCardTypeFilter;
        set
        {
            var normalized = SupportCardTypeOptions.Any(item =>
                    item.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
                ? value
                : "all";
            if (!Set(ref _supportCardTypeFilter, normalized))
                return;
            ApplySupportCardSearch();
        }
    }

    public int SelectedSupportCardCount =>
        _allSupportCardOptions.Count(item => item.IsSelected);

    public string SelectedSupportCardCountText =>
        $"Selected {SelectedSupportCardCount}/6";

    public string SupportDeckPreset
    {
        get => _supportDeckPreset;
        set
        {
            var normalized = SupportDeckPresets.Any(item =>
                    item.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
                ? value
                : "custom";
            Set(ref _supportDeckPreset, normalized);
        }
    }

    public string StrategyId
    {
        get => _strategyId;
        set => Set(ref _strategyId, value?.Trim() ?? string.Empty);
    }

    public bool PauseOnUnknownOutcome
    {
        get => _pauseOnUnknownOutcome;
        set => Set(ref _pauseOnUnknownOutcome, value);
    }

    public bool AllowOptionalRaces
    {
        get => _allowOptionalRaces;
        set => Set(ref _allowOptionalRaces, value);
    }

    public string LegacySelectionMode
    {
        get => _legacySelectionMode;
        set
        {
            var normalized = string.Equals(value, "manual", StringComparison.OrdinalIgnoreCase)
                ? "manual"
                : "auto";
            if (!Set(ref _legacySelectionMode, normalized))
                return;
            OnPropertyChanged(nameof(IsManualLegacySelection));
        }
    }

    public bool IsManualLegacySelection =>
        string.Equals(LegacySelectionMode, "manual", StringComparison.OrdinalIgnoreCase);

    public bool UseLegacyGuest
    {
        get => _useLegacyGuest;
        set => Set(ref _useLegacyGuest, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public IReadOnlyList<int> ParseSupportCardIds()
    {
        var ids = new List<int>();
        foreach (var token in SupportCardIdsText.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token, out var id) || id <= 0)
                throw new InvalidOperationException($"Invalid support card ID '{token}'.");
            if (!ids.Contains(id))
                ids.Add(id);
        }

        return ids;
    }

    public IReadOnlyList<string> ParseLegacyAttributeSparks() =>
        AttributeSparkOptions.Where(item => item.IsSelected).Select(item => item.Key).ToArray();

    public IReadOnlyList<string> ParseLegacyAptitudeSparks() =>
        AptitudeSparkOptions.Where(item => item.IsSelected).Select(item => item.Key).ToArray();

    public void SetLegacySparkSelections(
        IEnumerable<string> attributeSparks,
        IEnumerable<string> aptitudeSparks)
    {
        SetSelections(AttributeSparkOptions, attributeSparks);
        SetSelections(AptitudeSparkOptions, aptitudeSparks);
    }

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(ManifestPath)
        && TraineeId is > 0
        && !string.IsNullOrWhiteSpace(StrategyId);

    internal void SetStatus(string status) => Status = status;

    public void RefreshTrainees()
    {
        var selectedId = TraineeId;
        TraineeOptions.Clear();
        _allTraineeOptions.Clear();

        if (_umaDatabase is not null)
        {
            foreach (var trainee in _umaDatabase.Trainees
                         .Where(item => item.Available && HasRunnerTemplate(item))
                         .OrderBy(item => item.NameEn, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.TraineeId))
            {
                var label = string.IsNullOrWhiteSpace(trainee.NameEn)
                    ? "Unknown trainee"
                    : trainee.NameEn;
                BitmapSource? thumbnail = null;
                var imagePath = _umaDatabase.GetTraineeImagePath(trainee.TraineeId);
                if (File.Exists(imagePath))
                {
                    try
                    {
                        thumbnail = UmaImageCodec.Load(imagePath, maxDimension: 72);
                    }
                    catch (Exception)
                    {
                        // Keep the ID selectable if the optional thumbnail is invalid.
                    }
                }

                var option = new CareerTraineeOption(trainee.TraineeId, label, thumbnail);
                _allTraineeOptions.Add(option);
                TraineeOptions.Add(option);
            }
        }

        if (selectedId is not null
            && !_allTraineeOptions.Any(item => item.TraineeId == selectedId))
        {
            TraineeId = null;
        }

        OnPropertyChanged(nameof(TraineeOptions));
        ApplyTraineeSearch();
    }

    public void RefreshSupportCards()
    {
        var selectedIds = ParseSupportCardIdSet(_supportCardIdsText);
        _allSupportCardOptions.Clear();
        FilteredSupportCardOptions.Clear();
        SupportCardTypeOptions.Clear();
        SupportCardTypeOptions.Add(new("all", "All types"));

        if (_umaDatabase is not null)
        {
            foreach (var type in _umaDatabase.SupportCards
                         .Where(item => item.Available && !string.IsNullOrWhiteSpace(item.Type))
                         .Select(item => item.Type.Trim())
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                SupportCardTypeOptions.Add(new(type, type));
            }

            foreach (var card in _umaDatabase.SupportCards
                         .Where(item => item.Available)
                         .OrderBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.NameEn, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.SupportCardId))
            {
                var option = new CareerSupportCardOption(card)
                {
                    IsSelected = selectedIds.Contains(card.SupportCardId),
                };
                option.PropertyChanged += OnSupportCardOptionChanged;
                _allSupportCardOptions.Add(option);
            }
        }

        if (!SupportCardTypeOptions.Any(item =>
                item.Value.Equals(_supportCardTypeFilter, StringComparison.OrdinalIgnoreCase)))
        {
            _supportCardTypeFilter = "all";
            OnPropertyChanged(nameof(SupportCardTypeFilter));
        }

        ApplySupportCardSearch();
        OnPropertyChanged(nameof(SelectedSupportCardCount));
        OnPropertyChanged(nameof(SelectedSupportCardCountText));
    }

    private bool HasRunnerTemplate(UmaTraineeRecord trainee) =>
        File.Exists(_umaDatabase!.GetMaintenanceTraineeReferenceImagePath(trainee.TraineeId))
        || File.Exists(_umaDatabase.GetTraineeReferenceImagePath(trainee.TraineeId))
        || File.Exists(_umaDatabase.GetTraineeImagePath(trainee.TraineeId))
        || File.Exists(_umaDatabase.GetTraineeLiveOutfitReferenceImagePath(trainee.BaseCharacterId))
        || File.Exists(_umaDatabase.GetTraineeLiveOutfitImagePath(trainee.BaseCharacterId));

    private void OnDatabaseLoaded(object? sender, EventArgs e)
    {
        RefreshTrainees();
        RefreshSupportCards();
    }

    private void ApplyTraineeSearch()
    {
        var query = TraineeSearchText.Trim();
        FilteredTraineeOptions.Clear();
        foreach (var option in _allTraineeOptions)
        {
            if (string.IsNullOrWhiteSpace(query)
                || option.TraineeId == TraineeId
                || option.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.TraineeId.ToString(CultureInfo.InvariantCulture)
                    .Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredTraineeOptions.Add(option);
            }
        }

        OnPropertyChanged(nameof(SelectedTrainee));
    }

    private void ApplySupportCardSearch()
    {
        var query = SupportCardSearchText;
        var type = SupportCardTypeFilter;
        FilteredSupportCardOptions.Clear();
        foreach (var option in _allSupportCardOptions)
        {
            var typeMatches = type.Equals("all", StringComparison.OrdinalIgnoreCase)
                || option.Type.Equals(type, StringComparison.OrdinalIgnoreCase);
            var textMatches = string.IsNullOrWhiteSpace(query)
                || option.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.SupportCardId.ToString(CultureInfo.InvariantCulture)
                    .Contains(query, StringComparison.OrdinalIgnoreCase);
            if (typeMatches && textMatches)
                FilteredSupportCardOptions.Add(option);
        }
    }

    private void ApplySupportCardIdsText()
    {
        var selectedIds = ParseSupportCardIdSet(_supportCardIdsText);
        _updatingSupportCards = true;
        foreach (var option in _allSupportCardOptions)
            option.IsSelected = selectedIds.Contains(option.SupportCardId);
        _updatingSupportCards = false;
    }

    private void OnSupportCardOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updatingSupportCards
            || sender is not CareerSupportCardOption option
            || e.PropertyName != nameof(CareerSupportCardOption.IsSelected))
        {
            return;
        }

        if (option.IsSelected && SelectedSupportCardCount > 6)
        {
            _updatingSupportCards = true;
            option.IsSelected = false;
            _updatingSupportCards = false;
            SetStatus("A support deck can contain at most 6 cards.");
            return;
        }

        _supportCardIdsText = string.Join(",", _allSupportCardOptions
            .Where(item => item.IsSelected)
            .Select(item => item.SupportCardId.ToString(CultureInfo.InvariantCulture)));
        OnPropertyChanged(nameof(SupportCardIdsText));
        OnPropertyChanged(nameof(SelectedSupportCardCount));
        OnPropertyChanged(nameof(SelectedSupportCardCountText));
    }

    private static HashSet<int> ParseSupportCardIdSet(string text)
    {
        var result = new HashSet<int>();
        foreach (var token in text.Split(
                     [',', ' ', ';'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, out var id) && id > 0)
                result.Add(id);
        }

        return result;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static void SetSelections(
        IEnumerable<CareerSparkOption> options,
        IEnumerable<string> selectedKeys)
    {
        var selected = selectedKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
            option.IsSelected = selected.Contains(option.Key);
    }
}

public sealed record CareerTraineeOption(
    int TraineeId,
    string Label,
    BitmapSource? Thumbnail = null);

public sealed record CareerSupportCardTypeOption(string Value, string Label);

public sealed record CareerSupportDeckModeOption(string Value, string Label);

public sealed class CareerSupportCardOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public CareerSupportCardOption(UmaSupportCardRecord card)
    {
        SupportCardId = card.SupportCardId;
        Label = string.IsNullOrWhiteSpace(card.NameEn)
            ? $"Support card {card.SupportCardId}"
            : card.NameEn;
        Type = string.IsNullOrWhiteSpace(card.Type) ? "Unknown" : card.Type;
        Rarity = card.Rarity;
        ImageUrl = card.ImageUrl;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int SupportCardId { get; }

    public string Label { get; }

    public string Type { get; }

    public string Rarity { get; }

    public string? ImageUrl { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}

public sealed record CareerSupportDeckPresetOption(string Value, string Label);

public sealed record CareerLegacySelectionModeOption(string Value, string Label);

public sealed class CareerSparkOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public CareerSparkOption(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }

    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}
