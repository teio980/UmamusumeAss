using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Training;

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
    private int? _friendSupportCardId;
    private string _supportDeckMode = "auto";
    private string _supportDeckPreset = "custom";
    private string _supportCardSearchText = string.Empty;
    private string _supportCardTypeFilter = "all";
    private string _friendSupportCardSearchText = string.Empty;
    private string _friendSupportCardTypeFilter = "all";
    private bool _updatingSupportCards;
    private string _strategyId = DefaultStrategyId;
    private bool _pauseOnUnknownOutcome = true;
    private bool _allowOptionalRaces;
    private bool _continueExistingCareer;
    private string _legacySelectionMode = "auto";
    private bool _useLegacyGuest;
    private bool _useCachedLegacy = true;
    private string _status = string.Empty;
    private string _traineeSearchText = string.Empty;
    private bool _isTraineeDropDownOpen;
    private readonly List<CareerTraineeOption> _allTraineeOptions = [];
    private readonly List<CareerSupportCardOption> _allSupportCardOptions = [];
    private bool _updatingFriendSupportCards;

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

    public ObservableCollection<CareerFriendSupportCardOption> FriendSupportCardOptions { get; } = [];

    public ObservableCollection<CareerFriendSupportCardOption> FilteredFriendSupportCardOptions { get; } = [];

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

    public bool ContinueExistingCareer
    {
        get => _continueExistingCareer;
        set => Set(ref _continueExistingCareer, value);
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
            OnPropertyChanged(nameof(SupportCardDrawerHeader));
            OnPropertyChanged(nameof(IsSupportDeckValid));
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
                OnPropertyChanged(nameof(SupportCardDrawerHeader));
            }

            OnPropertyChanged(nameof(IsManualSupportDeck));
            OnPropertyChanged(nameof(IsSupportPresetMode));
            OnPropertyChanged(nameof(IsHighestStarSupportDeck));
            OnPropertyChanged(nameof(IsFriendSupportCardSettingEnabled));
            OnPropertyChanged(nameof(IsSupportDeckValid));

            if (IsHighestStarSupportDeck
                && SupportDeckPreset.Equals("custom", StringComparison.OrdinalIgnoreCase))
            {
                SupportDeckPreset = SupportDeckPresets
                    .First(item => !item.Value.Equals("custom", StringComparison.OrdinalIgnoreCase))
                    .Value;
            }
        }
    }

    public bool IsManualSupportDeck =>
        SupportDeckMode.Equals("selected", StringComparison.OrdinalIgnoreCase);

    public bool IsSupportPresetMode =>
        !SupportDeckMode.Equals("auto", StringComparison.OrdinalIgnoreCase);

    public bool IsHighestStarSupportDeck =>
        SupportDeckMode.Equals("highest-star", StringComparison.OrdinalIgnoreCase);

    public bool IsFriendSupportCardSettingEnabled => IsSupportPresetMode;

    public int? FriendSupportCardId
    {
        get => _friendSupportCardId;
        set
        {
            var normalized = value is > 0
                && FriendSupportCardOptions.Any(item => item.SupportCardId == value)
                ? value
                : null;
            if (!Set(ref _friendSupportCardId, normalized))
                return;
            SetFriendSupportCardOptionSelection(normalized);
            OnPropertyChanged(nameof(SelectedFriendSupportCardCount));
            OnPropertyChanged(nameof(SelectedFriendSupportCardCountText));
            OnPropertyChanged(nameof(FriendSupportCardDrawerHeader));
            OnPropertyChanged(nameof(IsSupportDeckValid));
        }
    }

    public bool IsSupportDeckValid
    {
        get
        {
            if (SupportDeckMode.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return true;

            if (IsHighestStarSupportDeck)
            {
                if (SupportDeckPreset.Equals("custom", StringComparison.OrdinalIgnoreCase))
                    return false;

                return IsValidFriendSupportCard();
            }

            IReadOnlyList<int> ids;
            try
            {
                ids = ParseSupportCardIds();
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            if (ids.Count is not (5 or 6)
                || _umaDatabase is null
                || (FriendSupportCardId is > 0 && ids.Count != 5)
                || !IsValidFriendSupportCard(allowCustomPreset: true))
                return false;

            var cards = new List<UmaSupportCardRecord>(ids.Count + 1);
            foreach (var id in ids)
            {
                if (!_umaDatabase.TryGetSupportCard(id, out var card)
                    || card is null
                    || !card.Available)
                {
                    return false;
                }

                cards.Add(card);
            }

            if (FriendSupportCardId is > 0
                && _umaDatabase.TryGetSupportCard(FriendSupportCardId.Value, out var friendCard)
                && friendCard is not null)
            {
                cards.Add(friendCard);
            }

            var requiredTypes = GetRequiredSupportTypes(SupportDeckPreset);
            if (requiredTypes is null)
                return SupportDeckPreset.Equals("custom", StringComparison.OrdinalIgnoreCase);

            if (cards.Count != 6)
                return false;

            var actualTypes = cards
                .GroupBy(card => card.Type, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Count(),
                    StringComparer.OrdinalIgnoreCase);
            return requiredTypes.All(required =>
                actualTypes.TryGetValue(required.Key, out var actual)
                && actual == required.Value);
        }
    }

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

    public string FriendSupportCardSearchText
    {
        get => _friendSupportCardSearchText;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (_friendSupportCardSearchText == normalized)
                return;
            _friendSupportCardSearchText = normalized;
            OnPropertyChanged();
            ApplyFriendSupportCardSearch();
        }
    }

    public string FriendSupportCardTypeFilter
    {
        get => _friendSupportCardTypeFilter;
        set
        {
            var normalized = SupportCardTypeOptions.Any(item =>
                    item.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
                ? value
                : "all";
            if (!Set(ref _friendSupportCardTypeFilter, normalized))
                return;
            ApplyFriendSupportCardSearch();
        }
    }

    public int SelectedSupportCardCount =>
        _allSupportCardOptions.Count(item => item.IsSelected);

    public string SelectedSupportCardCountText =>
        $"Selected {SelectedSupportCardCount}/5";

    public int SelectedFriendSupportCardCount =>
        FriendSupportCardOptions.Count(item => item.IsSelected && item.SupportCardId > 0);

    public string SelectedFriendSupportCardCountText =>
        $"Selected {SelectedFriendSupportCardCount}/1";

    public string FriendSupportCardDrawerHeader
    {
        get
        {
            var selected = FriendSupportCardOptions.FirstOrDefault(item =>
                item.IsSelected && item.SupportCardId > 0);
            return selected is null
                ? $"Friend support card ({SelectedFriendSupportCardCount}/1): None selected"
                : $"Friend support card ({SelectedFriendSupportCardCount}/1): {selected.Label}";
        }
    }

    public string SupportCardDrawerHeader =>
        $"Own support cards ({SelectedSupportCardCount}/5)";

    public string SupportDeckPreset
    {
        get => _supportDeckPreset;
        set
        {
            var normalized = SupportDeckPresets.Any(item =>
                    item.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
                ? value
                : "custom";
            if (IsHighestStarSupportDeck
                && normalized.Equals("custom", StringComparison.OrdinalIgnoreCase))
            {
                normalized = SupportDeckPresets
                    .First(item => !item.Value.Equals("custom", StringComparison.OrdinalIgnoreCase))
                    .Value;
            }
            if (!Set(ref _supportDeckPreset, normalized))
                return;
            OnPropertyChanged(nameof(IsSupportDeckValid));
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

    public bool UseCachedLegacy
    {
        get => _useCachedLegacy;
        set => Set(ref _useCachedLegacy, value);
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
        && ManifestFileExists()
        && TraineeId is > 0
        && !string.IsNullOrWhiteSpace(StrategyId)
        && UraStrategyRegistry.IsRegistered(StrategyId)
        && IsSupportDeckValid;

    private bool ManifestFileExists()
    {
        try
        {
            return File.Exists(Path.GetFullPath(ManifestPath));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private bool IsValidFriendSupportCard(bool allowCustomPreset = false)
    {
        if (FriendSupportCardId is not > 0)
            return true;

        if (_umaDatabase is null
            || !_umaDatabase.TryGetSupportCard(FriendSupportCardId.Value, out var card)
            || card is null
            || !card.Available)
        {
            return false;
        }

        var requiredTypes = GetRequiredSupportTypes(SupportDeckPreset);
        if (requiredTypes is null)
            return allowCustomPreset;

        // The friend1 preset means five own cards plus one guest card, so
        // the guest may be any available support-card type.
        if (requiredTypes.ContainsKey("Friend"))
            return true;

        var guestType = card.Type?.Trim();
        return !string.IsNullOrWhiteSpace(guestType)
            && requiredTypes.TryGetValue(guestType, out var requiredCount)
            && requiredCount > 0;
    }

    private static Dictionary<string, int>? GetRequiredSupportTypes(
        string supportDeckPreset) =>
        supportDeckPreset.ToLowerInvariant() switch
        {
            "speed3-stamina3" => new Dictionary<string, int>
            {
                ["Speed"] = 3,
                ["Stamina"] = 3,
            },
            "speed3-stamina2-wit1" => new Dictionary<string, int>
            {
                ["Speed"] = 3,
                ["Stamina"] = 2,
                ["Wit"] = 1,
            },
            "speed2-stamina2-power1-wit1" => new Dictionary<string, int>
            {
                ["Speed"] = 2,
                ["Stamina"] = 2,
                ["Power"] = 1,
                ["Wit"] = 1,
            },
            "speed2-stamina1-power1-wit1-friend1" => new Dictionary<string, int>
            {
                ["Speed"] = 2,
                ["Stamina"] = 1,
                ["Power"] = 1,
                ["Wit"] = 1,
                ["Friend"] = 1,
            },
            _ => null,
        };

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
        FriendSupportCardOptions.Clear();
        FilteredFriendSupportCardOptions.Clear();
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

                var label = string.IsNullOrWhiteSpace(card.NameEn)
                    ? $"Support card {card.SupportCardId.ToString(CultureInfo.InvariantCulture)}"
                    : card.NameEn;
                var typeLabel = string.IsNullOrWhiteSpace(card.Type)
                    ? "type unknown"
                    : card.Type.Trim();
                var friendOption = new CareerFriendSupportCardOption(
                    card.SupportCardId,
                    label,
                    typeLabel,
                    GetSupportRarityLabel(card.Rarity),
                    card.ImageUrl)
                {
                    IsSelected = card.SupportCardId == _friendSupportCardId,
                };
                friendOption.PropertyChanged += OnFriendSupportCardOptionChanged;
                FriendSupportCardOptions.Add(friendOption);
            }
        }

        if (_friendSupportCardId is not > 0
            || !FriendSupportCardOptions.Any(item => item.SupportCardId == _friendSupportCardId))
        {
            _friendSupportCardId = null;
        }

        if (!SupportCardTypeOptions.Any(item =>
                item.Value.Equals(_supportCardTypeFilter, StringComparison.OrdinalIgnoreCase)))
        {
            _supportCardTypeFilter = "all";
            OnPropertyChanged(nameof(SupportCardTypeFilter));
        }

        if (!SupportCardTypeOptions.Any(item =>
                item.Value.Equals(_friendSupportCardTypeFilter, StringComparison.OrdinalIgnoreCase)))
        {
            _friendSupportCardTypeFilter = "all";
            OnPropertyChanged(nameof(FriendSupportCardTypeFilter));
        }

        ApplySupportCardSearch();
        ApplyFriendSupportCardSearch();
        OnPropertyChanged(nameof(SelectedSupportCardCount));
        OnPropertyChanged(nameof(SelectedSupportCardCountText));
        OnPropertyChanged(nameof(SupportCardDrawerHeader));
        OnPropertyChanged(nameof(FriendSupportCardId));
        OnPropertyChanged(nameof(SelectedFriendSupportCardCount));
        OnPropertyChanged(nameof(SelectedFriendSupportCardCountText));
        OnPropertyChanged(nameof(FriendSupportCardDrawerHeader));
        OnPropertyChanged(nameof(IsSupportDeckValid));
    }

    private void OnFriendSupportCardOptionChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (_updatingFriendSupportCards
            || sender is not CareerFriendSupportCardOption option
            || e.PropertyName != nameof(CareerFriendSupportCardOption.IsSelected))
        {
            return;
        }

        if (option.IsSelected)
        {
            _updatingFriendSupportCards = true;
            foreach (var item in FriendSupportCardOptions)
                item.IsSelected = ReferenceEquals(item, option);
            _updatingFriendSupportCards = false;

            _friendSupportCardId = option.SupportCardId > 0
                ? option.SupportCardId
                : null;
            OnPropertyChanged(nameof(FriendSupportCardId));
            OnPropertyChanged(nameof(SelectedFriendSupportCardCount));
            OnPropertyChanged(nameof(SelectedFriendSupportCardCountText));
            OnPropertyChanged(nameof(FriendSupportCardDrawerHeader));
            OnPropertyChanged(nameof(IsSupportDeckValid));
            return;
        }

        if (option.SupportCardId > 0
            && _friendSupportCardId == option.SupportCardId)
        {
            _friendSupportCardId = null;
            OnPropertyChanged(nameof(FriendSupportCardId));
            OnPropertyChanged(nameof(SelectedFriendSupportCardCount));
            OnPropertyChanged(nameof(SelectedFriendSupportCardCountText));
            OnPropertyChanged(nameof(FriendSupportCardDrawerHeader));
            OnPropertyChanged(nameof(IsSupportDeckValid));
        }
    }

    private void SetFriendSupportCardOptionSelection(int? supportCardId)
    {
        if (FriendSupportCardOptions.Count == 0)
            return;

        _updatingFriendSupportCards = true;
        foreach (var option in FriendSupportCardOptions)
            option.IsSelected = option.SupportCardId == supportCardId;
        _updatingFriendSupportCards = false;
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

    private void ApplyFriendSupportCardSearch()
    {
        var query = FriendSupportCardSearchText;
        var type = FriendSupportCardTypeFilter;
        FilteredFriendSupportCardOptions.Clear();
        foreach (var option in FriendSupportCardOptions)
        {
            var typeMatches = type.Equals("all", StringComparison.OrdinalIgnoreCase)
                || option.Type.Equals(type, StringComparison.OrdinalIgnoreCase);
            var textMatches = string.IsNullOrWhiteSpace(query)
                || option.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.SupportCardId.ToString(CultureInfo.InvariantCulture)
                    .Contains(query, StringComparison.OrdinalIgnoreCase);
            if (typeMatches && textMatches)
                FilteredFriendSupportCardOptions.Add(option);
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

        if (option.IsSelected && SelectedSupportCardCount > 5)
        {
            _updatingSupportCards = true;
            option.IsSelected = false;
            _updatingSupportCards = false;
            SetStatus("Select up to 5 own support cards here; the Friend card has its own 1/1 selector.");
            return;
        }

        _supportCardIdsText = string.Join(",", _allSupportCardOptions
            .Where(item => item.IsSelected)
            .Select(item => item.SupportCardId.ToString(CultureInfo.InvariantCulture)));
        OnPropertyChanged(nameof(SupportCardIdsText));
        OnPropertyChanged(nameof(SelectedSupportCardCount));
        OnPropertyChanged(nameof(SelectedSupportCardCountText));
        OnPropertyChanged(nameof(SupportCardDrawerHeader));
        OnPropertyChanged(nameof(IsSupportDeckValid));
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

    private static string GetSupportRarityLabel(string? rarity) =>
        rarity?.Trim().ToUpperInvariant() switch
        {
            "3" or "SSR" => "SSR",
            "2" or "SR" => "SR",
            "1" or "R" => "R",
            _ => string.IsNullOrWhiteSpace(rarity) ? "rarity unknown" : rarity.Trim(),
        };

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

public sealed class CareerFriendSupportCardOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public CareerFriendSupportCardOption(
        int supportCardId,
        string label,
        string type,
        string rarity,
        string? imageUrl)
    {
        SupportCardId = supportCardId;
        Label = label;
        Type = type;
        Rarity = rarity;
        ImageUrl = imageUrl;
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
