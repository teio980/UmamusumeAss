using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.ViewModels.Tasks;

/// <summary>
/// Support deck selection, filtering, and validation for every Career mode.
/// </summary>
public sealed class SupportDeckSettingsViewModel : INotifyPropertyChanged
{
    private readonly IUmaDatabaseService? _umaDatabase;
    private string _supportCardIdsText = string.Empty;
    private int? _friendSupportCardId;
    private string _supportDeckMode = "auto";
    private string _supportDeckPreset = "custom";
    private string _supportCardSearchText = string.Empty;
    private string _supportCardTypeFilter = "all";
    private string _friendSupportCardSearchText = string.Empty;
    private string _friendSupportCardTypeFilter = "all";
    private bool _updatingSupportCards;
    private bool _updatingFriendSupportCards;
    private readonly List<CareerSupportCardOption> _allSupportCardOptions = [];

    public SupportDeckSettingsViewModel(IUmaDatabaseService? umaDatabase = null)
    {
        _umaDatabase = umaDatabase;
        if (_umaDatabase is not null)
            _umaDatabase.DatabaseLoaded += OnDatabaseLoaded;

        RefreshSupportCards();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<CareerSupportCardOption> FilteredSupportCardOptions { get; } = [];

    public ObservableCollection<CareerFriendSupportCardOption> FriendSupportCardOptions { get; } = [];

    public ObservableCollection<CareerFriendSupportCardOption> FilteredFriendSupportCardOptions { get; } = [];

    public ObservableCollection<CareerSupportCardTypeOption> SupportCardTypeOptions { get; } = [];

    public IReadOnlyList<CareerSupportDeckPresetOption> SupportDeckPresets { get; } =
        SupportDeckPresetCatalog.Presets
            .Select(item => new CareerSupportDeckPresetOption(item.Value, item.Label))
            .ToArray();

    public IReadOnlyList<CareerSupportDeckModeOption> SupportDeckModes { get; } =
    [
        new("auto", "Auto-Fill (game button)"),
        new("highest-star", "Highest-star preset"),
        new("selected", "Selected cards"),
    ];

    public string SupportCardIdsText
    {
        get => _supportCardIdsText;
        set
        {
            if (!Set(ref _supportCardIdsText, value?.Trim() ?? string.Empty))
                return;
            if (!_updatingSupportCards)
                ApplySupportCardIdsText();
            NotifySupportCardState();
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
            NotifyFriendSupportCardState();
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
            {
                return false;
            }

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

            var requiredTypes = SupportDeckPresetCatalog.GetRequiredTypes(SupportDeckPreset);
            if (requiredTypes is null)
                return SupportDeckPreset.Equals("custom", StringComparison.OrdinalIgnoreCase);
            if (cards.Count != 6)
                return false;

            var actualTypes = cards
                .GroupBy(card => card.Type, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
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
            if (!Set(ref _supportCardSearchText, normalized))
                return;
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
            if (!Set(ref _friendSupportCardSearchText, normalized))
                return;
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

    public IReadOnlyList<int> ParseSupportCardIds()
    {
        var ids = new List<int>();
        foreach (var token in SupportCardIdsText.Split(
                     [',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token, out var id) || id <= 0)
                throw new InvalidOperationException($"Invalid support card ID '{token}'.");
            if (!ids.Contains(id))
                ids.Add(id);
        }
        return ids;
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
                var typeLabel = string.IsNullOrWhiteSpace(card.Type) ? "type unknown" : card.Type.Trim();
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
        NotifySupportCardState();
        NotifyFriendSupportCardState();
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

        var requiredTypes = SupportDeckPresetCatalog.GetRequiredTypes(SupportDeckPreset);
        if (requiredTypes is null)
            return allowCustomPreset;
        if (requiredTypes.ContainsKey("Friend"))
            return true;
        var guestType = card.Type?.Trim();
        return !string.IsNullOrWhiteSpace(guestType)
            && requiredTypes.TryGetValue(guestType, out var requiredCount)
            && requiredCount > 0;
    }

    private void OnFriendSupportCardOptionChanged(object? sender, PropertyChangedEventArgs e)
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
            _friendSupportCardId = option.SupportCardId > 0 ? option.SupportCardId : null;
            OnPropertyChanged(nameof(FriendSupportCardId));
            NotifyFriendSupportCardState();
            return;
        }

        if (option.SupportCardId > 0 && _friendSupportCardId == option.SupportCardId)
        {
            _friendSupportCardId = null;
            OnPropertyChanged(nameof(FriendSupportCardId));
            NotifyFriendSupportCardState();
        }
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
            return;
        }

        _supportCardIdsText = string.Join(",", _allSupportCardOptions
            .Where(item => item.IsSelected)
            .Select(item => item.SupportCardId.ToString(CultureInfo.InvariantCulture)));
        OnPropertyChanged(nameof(SupportCardIdsText));
        NotifySupportCardState();
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

    private void ApplySupportCardIdsText()
    {
        var selectedIds = ParseSupportCardIdSet(_supportCardIdsText);
        _updatingSupportCards = true;
        foreach (var option in _allSupportCardOptions)
            option.IsSelected = selectedIds.Contains(option.SupportCardId);
        _updatingSupportCards = false;
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

    private void OnDatabaseLoaded(object? sender, EventArgs e) => RefreshSupportCards();

    private void NotifySupportCardState()
    {
        OnPropertyChanged(nameof(SelectedSupportCardCount));
        OnPropertyChanged(nameof(SelectedSupportCardCountText));
        OnPropertyChanged(nameof(SupportCardDrawerHeader));
        OnPropertyChanged(nameof(IsSupportDeckValid));
    }

    private void NotifyFriendSupportCardState()
    {
        OnPropertyChanged(nameof(SelectedFriendSupportCardCount));
        OnPropertyChanged(nameof(SelectedFriendSupportCardCountText));
        OnPropertyChanged(nameof(FriendSupportCardDrawerHeader));
        OnPropertyChanged(nameof(IsSupportDeckValid));
    }

    private static HashSet<int> ParseSupportCardIdSet(string text)
    {
        var result = new HashSet<int>();
        foreach (var token in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
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
}
