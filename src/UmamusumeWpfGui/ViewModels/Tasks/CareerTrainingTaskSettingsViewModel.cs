using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.ViewModels.Tasks;

/// <summary>
/// Composition root for Career task settings. Flat properties remain as
/// forwarding properties so existing task profiles and bindings keep working
/// while the UI migrates to child view models.
/// </summary>
public sealed class CareerTrainingTaskSettingsViewModel : INotifyPropertyChanged, IDisposable
{
    public const string DefaultManifestPath = HachimiResourcePaths.UraManifest;
    public const string DefaultStrategyId = "default-speed-medium";
    public const string DefaultEventHandling = CareerEventHandlingModes.Default;
    public const string NormalCareerMode = "normal";
    public const string IndependentCareerMode = "independent";
    public const string IndependentTrainingFocusBalanced = "balanced";
    public const string IndependentTrainingFocusStamina = "stamina";
    public const string IndependentTrainingFocusSprint = "sprint";
    public const string IndependentLineupStrategyFront = "front";
    public const string IndependentLineupStrategyPace = "pace";
    public const string IndependentLineupStrategyLate = "late";
    public const string IndependentLineupStrategyEnd = "end";

    private string _careerMode = IndependentCareerMode;
    private string _strategyId = DefaultStrategyId;
    private string _normalLineupStrategy = CareerStrategyCatalog.DefaultLineupStrategy;
    private string _normalEventHandling = DefaultEventHandling;
    private bool _pauseOnUnknownOutcome = true;
    private bool _allowOptionalRaces;
    private string _status = string.Empty;
    private bool _disposed;

    public CareerTrainingTaskSettingsViewModel(IUmaDatabaseService? umaDatabase = null)
    {
        Entry = new CareerEntrySettingsViewModel(umaDatabase);
        SupportDeck = new SupportDeckSettingsViewModel(umaDatabase);
        Independent = new IndependentTrainingSettingsViewModel();
        Independent.IsCareerModeActive = IsIndependentCareer;

        Entry.PropertyChanged += OnChildPropertyChanged;
        SupportDeck.PropertyChanged += OnChildPropertyChanged;
        Independent.PropertyChanged += OnChildPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CareerEntrySettingsViewModel Entry { get; }
    public SupportDeckSettingsViewModel SupportDeck { get; }
    public IndependentTrainingSettingsViewModel Independent { get; }

    public IReadOnlyList<CareerModeOption> CareerModes { get; } =
    [
        new(NormalCareerMode, "Normal Career"),
        new(IndependentCareerMode, "Independent Training (auto)"),
    ];

    public string CareerMode
    {
        get => _careerMode;
        set
        {
            var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
            if (normalized.Length == 0)
                normalized = IndependentCareerMode;
            if (!Set(ref _careerMode, normalized))
                return;
            Independent.IsCareerModeActive = IsIndependentCareer;
            OnPropertyChanged(nameof(IsIndependentCareer));
            OnPropertyChanged(nameof(IsNormalCareer));
            OnPropertyChanged(nameof(IsKnownCareerMode));
            OnPropertyChanged(nameof(IsIndependentTrainingSettingsValid));
            OnPropertyChanged(nameof(IsValid));
        }
    }

    public bool IsIndependentCareer =>
        CareerMode.Equals(IndependentCareerMode, StringComparison.OrdinalIgnoreCase);

    public bool IsNormalCareer =>
        CareerMode.Equals(NormalCareerMode, StringComparison.OrdinalIgnoreCase);

    public bool IsKnownCareerMode =>
        CareerMode.Equals(NormalCareerMode, StringComparison.OrdinalIgnoreCase)
        || CareerMode.Equals(IndependentCareerMode, StringComparison.OrdinalIgnoreCase);

    public string StrategyId
    {
        get => _strategyId;
        set
        {
            if (!Set(ref _strategyId, value?.Trim() ?? string.Empty))
                return;

            OnPropertyChanged(nameof(NormalTrainingStrategy));
            OnPropertyChanged(nameof(IsValid));
        }
    }

    public IReadOnlyList<UraTrainingStrategyOption> NormalTrainingStrategyOptions { get; } =
        UraStrategyRegistry.AvailableStrategies;

    public string NormalTrainingStrategy
    {
        get => StrategyId;
        set => StrategyId = value;
    }

    public IReadOnlyList<CareerEventHandlingOption> NormalEventHandlingOptions { get; } =
    [
        new(DefaultEventHandling, "Default"),
    ];

    public string NormalEventHandling
    {
        get => _normalEventHandling;
        set
        {
            var normalized = NormalEventHandlingOptions
                .FirstOrDefault(item => item.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
                ?.Value ?? DefaultEventHandling;
            Set(ref _normalEventHandling, normalized);
        }
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

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    // Common entry compatibility properties.
    public string ManifestPath { get => Entry.ManifestPath; set => Entry.ManifestPath = value; }
    public int? TraineeId { get => Entry.TraineeId; set => Entry.TraineeId = value; }
    public CareerTraineeOption? SelectedTrainee { get => Entry.SelectedTrainee; set => Entry.SelectedTrainee = value; }
    public bool DeleteExistingCareerData
    {
        get => Entry.DeleteExistingCareerData;
        set => Entry.DeleteExistingCareerData = value;
    }

    // Keep the old in-memory property available for callers compiled against
    // the previous profile model. Its value is intentionally the inverse of
    // the new UI-facing setting.
    [Obsolete("Use DeleteExistingCareerData instead.")]
    public bool ContinueExistingCareer
    {
        get => !DeleteExistingCareerData;
        set => DeleteExistingCareerData = !value;
    }
    public string TraineeSearchText { get => Entry.TraineeSearchText; set => Entry.TraineeSearchText = value; }
    public bool IsTraineeDropDownOpen { get => Entry.IsTraineeDropDownOpen; set => Entry.IsTraineeDropDownOpen = value; }
    public ObservableCollection<CareerTraineeOption> TraineeOptions => Entry.TraineeOptions;
    public ObservableCollection<CareerTraineeOption> FilteredTraineeOptions => Entry.FilteredTraineeOptions;
    public IReadOnlyList<CareerLegacySelectionModeOption> LegacySelectionModes => Entry.LegacySelectionModes;
    public string LegacySelectionMode { get => Entry.LegacySelectionMode; set => Entry.LegacySelectionMode = value; }
    public bool IsManualLegacySelection => Entry.IsManualLegacySelection;
    public bool UseLegacyGuest { get => Entry.UseLegacyGuest; set => Entry.UseLegacyGuest = value; }
    public bool UseCachedLegacy { get => Entry.UseCachedLegacy; set => Entry.UseCachedLegacy = value; }
    public ObservableCollection<CareerSparkOption> AttributeSparkOptions => Entry.AttributeSparkOptions;
    public ObservableCollection<CareerSparkOption> AptitudeSparkOptions => Entry.AptitudeSparkOptions;

    // Independent compatibility properties.
    public IReadOnlyList<IndependentTrainingOption> IndependentTrainingFocusOptions => Independent.TrainingFocusOptions;
    public IReadOnlyList<IndependentTrainingOption> IndependentLineupStrategyOptions => Independent.LineupStrategyOptions;
    public IReadOnlyList<IndependentTrainingOption> IndependentAgendaYearOptions => Independent.AgendaYearOptions;
    public IReadOnlyList<IndependentTrainingOption> IndependentAgendaMonthOptions => Independent.AgendaMonthOptions;
    public IReadOnlyList<IndependentTrainingOption> IndependentAgendaTurnOptions => Independent.AgendaTurnOptions;
    public IReadOnlyList<IndependentTrainingOption> IndependentAgendaDayOptions => Independent.AgendaDayOptions;
    public IReadOnlyList<IndependentTrainingOption> IndependentAgendaTimeOptions => Independent.AgendaTimeOptions;
    public IReadOnlyList<IndependentTrainingOption> IndependentAgendaHalfOptions => Independent.AgendaHalfOptions;
    public IReadOnlyList<IndependentTrainingOption> IndependentAgendaSurfaceOptions => Independent.AgendaSurfaceOptions;
    public IReadOnlyList<IndependentTrainingOption> IndependentAgendaDistanceOptions => Independent.AgendaDistanceOptions;
    public IReadOnlyList<IndependentTrainingOption> IndependentAgendaTrackOptions => Independent.AgendaTrackOptions;
    public IReadOnlyList<IndependentTrainingOption> IndependentAgendaDirectionOptions => Independent.AgendaDirectionOptions;
    public IReadOnlyList<IndependentTrainingOption> IndependentAgendaGradeOptions => Independent.AgendaGradeOptions;
    public string IndependentTrainingFocus { get => Independent.TrainingFocus; set => Independent.TrainingFocus = value; }
    public string IndependentLineupStrategy { get => Independent.LineupStrategy; set => Independent.LineupStrategy = value; }
    public IReadOnlyList<IndependentTrainingOption> NormalLineupStrategyOptions { get; } =
    [
        new(IndependentLineupStrategyFront, "Front Runner"),
        new(IndependentLineupStrategyPace, "Pace Chaser"),
        new(IndependentLineupStrategyLate, "Late Surger"),
        new(IndependentLineupStrategyEnd, "End Closer"),
    ];
    public string NormalLineupStrategy
    {
        get => _normalLineupStrategy;
        set
        {
            var normalized = NormalLineupStrategyOptions.Any(item =>
                    item.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
                ? value
                : CareerStrategyCatalog.DefaultLineupStrategy;
            if (Set(ref _normalLineupStrategy, normalized))
                OnPropertyChanged(nameof(IsValid));
        }
    }
    public bool IsIndependentAgendaFilterOpen { get => Independent.IsAgendaFilterOpen; set => Independent.IsAgendaFilterOpen = value; }
    public string IndependentAgendaYearFilter { get => Independent.AgendaYearFilter; set => Independent.AgendaYearFilter = value; }
    public string IndependentAgendaSearchText { get => Independent.AgendaSearchText; set => Independent.AgendaSearchText = value; }
    public string IndependentAgendaMonthFilter { get => Independent.AgendaMonthFilter; set => Independent.AgendaMonthFilter = value; }
    public string IndependentAgendaTurnFilter { get => Independent.AgendaTurnFilter; set => Independent.AgendaTurnFilter = value; }
    public string IndependentAgendaDayFilter { get => Independent.AgendaDayFilter; set => Independent.AgendaDayFilter = value; }
    public string IndependentAgendaTimeFilter { get => Independent.AgendaTimeFilter; set => Independent.AgendaTimeFilter = value; }
    public string IndependentAgendaHalfFilter { get => Independent.AgendaHalfFilter; set => Independent.AgendaHalfFilter = value; }
    public string IndependentAgendaSurfaceFilter { get => Independent.AgendaSurfaceFilter; set => Independent.AgendaSurfaceFilter = value; }
    public string IndependentAgendaDistanceFilter { get => Independent.AgendaDistanceFilter; set => Independent.AgendaDistanceFilter = value; }
    public string IndependentAgendaTrackFilter { get => Independent.AgendaTrackFilter; set => Independent.AgendaTrackFilter = value; }
    public string IndependentAgendaDirectionFilter { get => Independent.AgendaDirectionFilter; set => Independent.AgendaDirectionFilter = value; }
    public string IndependentAgendaGradeFilter { get => Independent.AgendaGradeFilter; set => Independent.AgendaGradeFilter = value; }
    public string IndependentSkillSearchText { get => Independent.SkillSearchText; set => Independent.SkillSearchText = value; }
    public int SelectedIndependentAgendaCount => Independent.SelectedAgendaCount;
    public int SelectedIndependentSkillCount => Independent.SelectedSkillCount;
    public string SelectedIndependentAgendaCountText => Independent.SelectedAgendaCountText;
    public string SelectedIndependentSkillCountText => Independent.SelectedSkillCountText;
    public string IndependentAgendaSelectionsText { get => Independent.AgendaSelectionsText; set => Independent.AgendaSelectionsText = value; }
    public string IndependentSkillIdsText { get => Independent.SkillIdsText; set => Independent.SkillIdsText = value; }
    public IReadOnlyList<IndependentRaceOption> IndependentRaceOptions => Independent.IndependentRaceOptions;
    public IReadOnlyList<IndependentSkillOption> IndependentSkillOptions => Independent.IndependentSkillOptions;
    public ObservableCollection<IndependentRaceOption> FilteredIndependentRaceOptions => Independent.FilteredIndependentRaceOptions;
    public ObservableCollection<IndependentSkillOption> FilteredIndependentSkillOptions => Independent.FilteredIndependentSkillOptions;
    public System.Windows.Input.ICommand ResetIndependentAgendaCommand => Independent.ResetIndependentAgendaCommand;
    public System.Windows.Input.ICommand ToggleIndependentAgendaFilterCommand => Independent.ToggleAgendaFilterCommand;
    public System.Windows.Input.ICommand ResetIndependentAgendaFiltersCommand => Independent.ResetAgendaFiltersCommand;
    public System.Windows.Input.ICommand ResetIndependentSkillsCommand => Independent.ResetIndependentSkillsCommand;

    // Support deck compatibility properties.
    public ObservableCollection<CareerSupportCardOption> FilteredSupportCardOptions => SupportDeck.FilteredSupportCardOptions;
    public ObservableCollection<CareerFriendSupportCardOption> FriendSupportCardOptions => SupportDeck.FriendSupportCardOptions;
    public ObservableCollection<CareerFriendSupportCardOption> FilteredFriendSupportCardOptions => SupportDeck.FilteredFriendSupportCardOptions;
    public ObservableCollection<CareerSupportCardTypeOption> SupportCardTypeOptions => SupportDeck.SupportCardTypeOptions;
    public IReadOnlyList<CareerSupportDeckPresetOption> SupportDeckPresets => SupportDeck.SupportDeckPresets;
    public IReadOnlyList<CareerSupportDeckModeOption> SupportDeckModes => SupportDeck.SupportDeckModes;
    public string SupportCardIdsText { get => SupportDeck.SupportCardIdsText; set => SupportDeck.SupportCardIdsText = value; }
    public string SupportDeckMode { get => SupportDeck.SupportDeckMode; set => SupportDeck.SupportDeckMode = value; }
    public bool IsManualSupportDeck => SupportDeck.IsManualSupportDeck;
    public bool IsSupportPresetMode => SupportDeck.IsSupportPresetMode;
    public bool IsHighestStarSupportDeck => SupportDeck.IsHighestStarSupportDeck;
    public bool IsFriendSupportCardSettingEnabled => SupportDeck.IsFriendSupportCardSettingEnabled;
    public int? FriendSupportCardId { get => SupportDeck.FriendSupportCardId; set => SupportDeck.FriendSupportCardId = value; }
    public bool IsSupportDeckValid => SupportDeck.IsSupportDeckValid;
    public string SupportCardSearchText { get => SupportDeck.SupportCardSearchText; set => SupportDeck.SupportCardSearchText = value; }
    public string SupportCardTypeFilter { get => SupportDeck.SupportCardTypeFilter; set => SupportDeck.SupportCardTypeFilter = value; }
    public string FriendSupportCardSearchText { get => SupportDeck.FriendSupportCardSearchText; set => SupportDeck.FriendSupportCardSearchText = value; }
    public string FriendSupportCardTypeFilter { get => SupportDeck.FriendSupportCardTypeFilter; set => SupportDeck.FriendSupportCardTypeFilter = value; }
    public int SelectedSupportCardCount => SupportDeck.SelectedSupportCardCount;
    public string SelectedSupportCardCountText => SupportDeck.SelectedSupportCardCountText;
    public int SelectedFriendSupportCardCount => SupportDeck.SelectedFriendSupportCardCount;
    public string SelectedFriendSupportCardCountText => SupportDeck.SelectedFriendSupportCardCountText;
    public string FriendSupportCardDrawerHeader => SupportDeck.FriendSupportCardDrawerHeader;
    public string SupportCardDrawerHeader => SupportDeck.SupportCardDrawerHeader;
    public string SupportDeckPreset { get => SupportDeck.SupportDeckPreset; set => SupportDeck.SupportDeckPreset = value; }

    public IReadOnlyList<int> ParseSupportCardIds() => SupportDeck.ParseSupportCardIds();
    public IReadOnlyList<IndependentTrainingAgendaSelection> ParseIndependentAgendaSelections() => Independent.ParseAgendaSelections();
    public IReadOnlyList<int> ParseIndependentSkillIds() => Independent.ParseSkillIds();
    public static IReadOnlyList<string> ParseIndependentAgendaSelectionKeys(string? text) => IndependentTrainingSettingsViewModel.ParseAgendaSelectionKeys(text);
    public IReadOnlyList<string> ParseLegacyAttributeSparks() => Entry.ParseLegacyAttributeSparks();
    public IReadOnlyList<string> ParseLegacyAptitudeSparks() => Entry.ParseLegacyAptitudeSparks();
    public void SetLegacySparkSelections(IEnumerable<string> attributeSparks, IEnumerable<string> aptitudeSparks) => Entry.SetLegacySparkSelections(attributeSparks, aptitudeSparks);

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(ManifestPath)
        && Entry.IsManifestValid()
        && TraineeId is > 0
        && IsKnownCareerMode
        && (IsIndependentCareer
            || (!string.IsNullOrWhiteSpace(StrategyId)
                && UraStrategyRegistry.IsRegistered(StrategyId)
                && CareerStrategyCatalog.TryGetLineupStrategyUiMapping(
                    NormalLineupStrategy,
                    out _)))
        && IsSupportDeckValid
        && (!IsIndependentCareer || IsIndependentTrainingSettingsValid);

    public bool IsIndependentTrainingSettingsValid => Independent.IsValid;

    public void RefreshTrainees() => Entry.RefreshTrainees();
    public void RefreshSupportCards() => SupportDeck.RefreshSupportCards();
    public void RefreshIndependentTrainingCatalog(string? baseDirectory = null) => Independent.RefreshCatalog(baseDirectory);
    internal void SetStatus(string status) => Status = status;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Entry.PropertyChanged -= OnChildPropertyChanged;
        SupportDeck.PropertyChanged -= OnChildPropertyChanged;
        Independent.PropertyChanged -= OnChildPropertyChanged;
        Entry.Dispose();
        SupportDeck.Dispose();
    }

    private void OnChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e.PropertyName);
        if (sender == Independent)
        {
            var compatibilityName = e.PropertyName switch
            {
                nameof(IndependentTrainingSettingsViewModel.TrainingFocus) => nameof(IndependentTrainingFocus),
                nameof(IndependentTrainingSettingsViewModel.LineupStrategy) => nameof(IndependentLineupStrategy),
                nameof(IndependentTrainingSettingsViewModel.IsAgendaFilterOpen) => nameof(IsIndependentAgendaFilterOpen),
                nameof(IndependentTrainingSettingsViewModel.AgendaYearFilter) => nameof(IndependentAgendaYearFilter),
                nameof(IndependentTrainingSettingsViewModel.AgendaSearchText) => nameof(IndependentAgendaSearchText),
                nameof(IndependentTrainingSettingsViewModel.AgendaMonthFilter) => nameof(IndependentAgendaMonthFilter),
                nameof(IndependentTrainingSettingsViewModel.AgendaTurnFilter) => nameof(IndependentAgendaTurnFilter),
                nameof(IndependentTrainingSettingsViewModel.AgendaDayFilter) => nameof(IndependentAgendaDayFilter),
                nameof(IndependentTrainingSettingsViewModel.AgendaTimeFilter) => nameof(IndependentAgendaTimeFilter),
                nameof(IndependentTrainingSettingsViewModel.AgendaHalfFilter) => nameof(IndependentAgendaHalfFilter),
                nameof(IndependentTrainingSettingsViewModel.AgendaSurfaceFilter) => nameof(IndependentAgendaSurfaceFilter),
                nameof(IndependentTrainingSettingsViewModel.AgendaDistanceFilter) => nameof(IndependentAgendaDistanceFilter),
                nameof(IndependentTrainingSettingsViewModel.AgendaTrackFilter) => nameof(IndependentAgendaTrackFilter),
                nameof(IndependentTrainingSettingsViewModel.AgendaDirectionFilter) => nameof(IndependentAgendaDirectionFilter),
                nameof(IndependentTrainingSettingsViewModel.AgendaGradeFilter) => nameof(IndependentAgendaGradeFilter),
                nameof(IndependentTrainingSettingsViewModel.AgendaYearOptions) => nameof(IndependentAgendaYearOptions),
                nameof(IndependentTrainingSettingsViewModel.AgendaMonthOptions) => nameof(IndependentAgendaMonthOptions),
                nameof(IndependentTrainingSettingsViewModel.AgendaTurnOptions) => nameof(IndependentAgendaTurnOptions),
                nameof(IndependentTrainingSettingsViewModel.AgendaDayOptions) => nameof(IndependentAgendaDayOptions),
                nameof(IndependentTrainingSettingsViewModel.AgendaTimeOptions) => nameof(IndependentAgendaTimeOptions),
                nameof(IndependentTrainingSettingsViewModel.AgendaHalfOptions) => nameof(IndependentAgendaHalfOptions),
                nameof(IndependentTrainingSettingsViewModel.AgendaSurfaceOptions) => nameof(IndependentAgendaSurfaceOptions),
                nameof(IndependentTrainingSettingsViewModel.AgendaDistanceOptions) => nameof(IndependentAgendaDistanceOptions),
                nameof(IndependentTrainingSettingsViewModel.AgendaTrackOptions) => nameof(IndependentAgendaTrackOptions),
                nameof(IndependentTrainingSettingsViewModel.AgendaDirectionOptions) => nameof(IndependentAgendaDirectionOptions),
                nameof(IndependentTrainingSettingsViewModel.AgendaGradeOptions) => nameof(IndependentAgendaGradeOptions),
                nameof(IndependentTrainingSettingsViewModel.SkillSearchText) => nameof(IndependentSkillSearchText),
                nameof(IndependentTrainingSettingsViewModel.AgendaSelectionsText) => nameof(IndependentAgendaSelectionsText),
                nameof(IndependentTrainingSettingsViewModel.SkillIdsText) => nameof(IndependentSkillIdsText),
                nameof(IndependentTrainingSettingsViewModel.SelectedAgendaCount) => nameof(SelectedIndependentAgendaCount),
                nameof(IndependentTrainingSettingsViewModel.SelectedAgendaCountText) => nameof(SelectedIndependentAgendaCountText),
                nameof(IndependentTrainingSettingsViewModel.SelectedSkillCount) => nameof(SelectedIndependentSkillCount),
                nameof(IndependentTrainingSettingsViewModel.SelectedSkillCountText) => nameof(SelectedIndependentSkillCountText),
                _ => null,
            };
            if (compatibilityName is not null)
                OnPropertyChanged(compatibilityName);
        }

        OnPropertyChanged(nameof(IsIndependentTrainingSettingsValid));
        OnPropertyChanged(nameof(IsValid));
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
}

public sealed record CareerTraineeOption(int TraineeId, string Label, BitmapSource? Thumbnail = null);
public sealed record CareerSupportCardTypeOption(string Value, string Label);
public sealed record CareerSupportDeckModeOption(string Value, string Label);

public sealed class CareerSupportCardOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public CareerSupportCardOption(UmaSupportCardRecord card)
    {
        SupportCardId = card.SupportCardId;
        Label = string.IsNullOrWhiteSpace(card.NameEn) ? $"Support card {card.SupportCardId}" : card.NameEn;
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
public sealed record CareerModeOption(string Value, string Label);
public sealed record CareerEventHandlingOption(string Value, string Label);
public sealed record IndependentTrainingOption(string Value, string Label);

public sealed class IndependentRaceOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public IndependentRaceOption(IndependentTrainingRace race, bool isExecutable, string? raceCardImagePath = null)
    {
        Race = race;
        IsExecutable = isExecutable;
        RaceCardImagePath = raceCardImagePath;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IndependentTrainingRace Race { get; }
    public bool IsExecutable { get; }
    public string? RaceCardImagePath { get; }
    public bool HasRaceCardImage => !string.IsNullOrWhiteSpace(RaceCardImagePath);
    public string CardMatchHint => HasRaceCardImage ? $"Card match · Race ID {Race.RaceId}" : $"Card unavailable · Race ID {Race.RaceId}";
    public string Label => IsExecutable ? Race.DisplayLabel : $"{Race.DisplayLabel} · no Race ID card asset";
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !IsExecutable)
                return;
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}

public sealed class IndependentSkillOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public IndependentSkillOption(IndependentTrainingSkill skill, bool isExecutable)
    {
        Skill = skill;
        IsExecutable = isExecutable;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IndependentTrainingSkill Skill { get; }
    public int SkillId => Skill.SkillId;
    public bool IsExecutable { get; }
    public string Label => IsExecutable ? Skill.DisplayLabel : $"{Skill.DisplayLabel} · unavailable in Global Add Skills";
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !IsExecutable)
                return;
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}

public sealed class CareerFriendSupportCardOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public CareerFriendSupportCardOption(int supportCardId, string label, string type, string rarity, string? imageUrl)
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
