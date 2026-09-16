using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.ViewModels.Tasks;

/// <summary>
/// Independent Training-only setup. The data catalog and selection state live
/// here instead of in the Career task's common settings object.
/// </summary>
public sealed class IndependentTrainingSettingsViewModel : INotifyPropertyChanged
{
    public const string AllAgendaFilters = "all";
    public const string AllAgendaMonths = AllAgendaFilters;

    private string _trainingFocus = CareerTrainingTaskSettingsViewModel.IndependentTrainingFocusBalanced;
    private string _lineupStrategy = CareerTrainingTaskSettingsViewModel.IndependentLineupStrategyPace;
    private string _agendaSearchText = string.Empty;
    private string _agendaYearFilter = AllAgendaFilters;
    private string _agendaMonthFilter = AllAgendaFilters;
    private string _agendaTurnFilter = AllAgendaFilters;
    private string _agendaDayFilter = AllAgendaFilters;
    private string _agendaTimeFilter = AllAgendaFilters;
    private string _agendaHalfFilter = AllAgendaFilters;
    private string _agendaSurfaceFilter = AllAgendaFilters;
    private string _agendaDistanceFilter = AllAgendaFilters;
    private string _agendaTrackFilter = AllAgendaFilters;
    private string _agendaDirectionFilter = AllAgendaFilters;
    private string _agendaGradeFilter = AllAgendaFilters;
    private string _skillSearchText = string.Empty;
    private bool _updatingAgenda;
    private bool _updatingSkills;
    private bool _isCareerModeActive = true;
    private bool _isAgendaFilterOpen;
    private IndependentTrainingCatalog _catalog = IndependentTrainingCatalog.Load();
    private readonly List<IndependentRaceOption> _allRaceOptions = [];
    private readonly List<IndependentSkillOption> _allSkillOptions = [];
    private static readonly string[] AgendaDistanceCategories = ["Long", "Mile", "Medium", "Sprint"];

    public IndependentTrainingSettingsViewModel()
    {
        ResetIndependentAgendaCommand = new RelayCommand(_ => ResetAgenda());
        ToggleAgendaFilterCommand = new RelayCommand(_ => IsAgendaFilterOpen = !IsAgendaFilterOpen);
        ResetAgendaFiltersCommand = new RelayCommand(_ => ResetAgendaFilters());
        ResetIndependentSkillsCommand = new RelayCommand(_ => ResetSkills());
        RefreshCatalog();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<IndependentRaceOption> FilteredIndependentRaceOptions { get; } = [];

    public ObservableCollection<IndependentSkillOption> FilteredIndependentSkillOptions { get; } = [];

    public IReadOnlyList<IndependentRaceOption> IndependentRaceOptions => _allRaceOptions;

    public IReadOnlyList<IndependentSkillOption> IndependentSkillOptions => _allSkillOptions;

    public IReadOnlyList<IndependentTrainingOption> AgendaYearOptions { get; private set; } =
    [
        new(AllAgendaFilters, "All"),
    ];

    public IReadOnlyList<IndependentTrainingOption> AgendaMonthOptions { get; private set; } =
    [
        new(AllAgendaMonths, "All"),
    ];

    public IReadOnlyList<IndependentTrainingOption> AgendaTurnOptions { get; private set; } =
    [
        new(AllAgendaFilters, "All"),
    ];

    public IReadOnlyList<IndependentTrainingOption> AgendaDayOptions { get; private set; } =
    [
        new(AllAgendaFilters, "All"),
    ];

    public IReadOnlyList<IndependentTrainingOption> AgendaTimeOptions { get; private set; } =
    [
        new(AllAgendaFilters, "All"),
    ];

    public IReadOnlyList<IndependentTrainingOption> AgendaHalfOptions { get; private set; } =
    [
        new(AllAgendaFilters, "All"),
    ];

    public IReadOnlyList<IndependentTrainingOption> AgendaSurfaceOptions { get; private set; } =
    [
        new(AllAgendaFilters, "All"),
    ];

    public IReadOnlyList<IndependentTrainingOption> AgendaDistanceOptions { get; private set; } =
    [
        new(AllAgendaFilters, "All"),
    ];

    public IReadOnlyList<IndependentTrainingOption> AgendaTrackOptions { get; private set; } =
    [
        new(AllAgendaFilters, "All"),
    ];

    public IReadOnlyList<IndependentTrainingOption> AgendaDirectionOptions { get; private set; } =
    [
        new(AllAgendaFilters, "All"),
    ];

    public IReadOnlyList<IndependentTrainingOption> AgendaGradeOptions { get; private set; } =
    [
        new(AllAgendaFilters, "All"),
    ];

    public ICommand ResetIndependentAgendaCommand { get; }

    public ICommand ToggleAgendaFilterCommand { get; }

    public ICommand ResetAgendaFiltersCommand { get; }

    public ICommand ResetIndependentSkillsCommand { get; }

    public IReadOnlyList<IndependentTrainingOption> TrainingFocusOptions { get; } =
    [
        new(CareerTrainingTaskSettingsViewModel.IndependentTrainingFocusBalanced, "Balanced"),
        new(CareerTrainingTaskSettingsViewModel.IndependentTrainingFocusStamina, "Stamina"),
        new(CareerTrainingTaskSettingsViewModel.IndependentTrainingFocusSprint, "Sprint / Power"),
    ];

    public IReadOnlyList<IndependentTrainingOption> LineupStrategyOptions { get; } =
    [
        new(CareerTrainingTaskSettingsViewModel.IndependentLineupStrategyFront, "Front Runner"),
        new(CareerTrainingTaskSettingsViewModel.IndependentLineupStrategyPace, "Pace Chaser"),
        new(CareerTrainingTaskSettingsViewModel.IndependentLineupStrategyLate, "Late Surger"),
        new(CareerTrainingTaskSettingsViewModel.IndependentLineupStrategyEnd, "End Closer"),
    ];

    public bool IsCareerModeActive
    {
        get => _isCareerModeActive;
        set => Set(ref _isCareerModeActive, value);
    }

    public string TrainingFocus
    {
        get => _trainingFocus;
        set
        {
            var normalized = TrainingFocusOptions.Any(item =>
                    item.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
                ? value
                : CareerTrainingTaskSettingsViewModel.IndependentTrainingFocusBalanced;
            if (Set(ref _trainingFocus, normalized))
                OnPropertyChanged(nameof(IsValid));
        }
    }

    public string LineupStrategy
    {
        get => _lineupStrategy;
        set
        {
            var normalized = LineupStrategyOptions.Any(item =>
                    item.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
                ? value
                : CareerTrainingTaskSettingsViewModel.IndependentLineupStrategyPace;
            if (Set(ref _lineupStrategy, normalized))
                OnPropertyChanged(nameof(IsValid));
        }
    }

    public string AgendaSearchText
    {
        get => _agendaSearchText;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!Set(ref _agendaSearchText, normalized))
                return;
            ApplyAgendaSearch();
        }
    }

    public bool IsAgendaFilterOpen
    {
        get => _isAgendaFilterOpen;
        set => Set(ref _isAgendaFilterOpen, value);
    }

    public string AgendaYearFilter
    {
        get => _agendaYearFilter;
        set => SetAgendaFilter(ref _agendaYearFilter, value, AgendaYearOptions, nameof(AgendaYearFilter));
    }

    public string AgendaMonthFilter
    {
        get => _agendaMonthFilter;
        set => SetAgendaFilter(ref _agendaMonthFilter, value, AgendaMonthOptions, nameof(AgendaMonthFilter));
    }

    public string AgendaTurnFilter
    {
        get => _agendaTurnFilter;
        set => SetAgendaFilter(ref _agendaTurnFilter, value, AgendaTurnOptions, nameof(AgendaTurnFilter));
    }

    public string AgendaDayFilter
    {
        get => _agendaDayFilter;
        set => SetAgendaFilter(ref _agendaDayFilter, value, AgendaDayOptions, nameof(AgendaDayFilter));
    }

    public string AgendaTimeFilter
    {
        get => _agendaTimeFilter;
        set => SetAgendaFilter(ref _agendaTimeFilter, value, AgendaTimeOptions, nameof(AgendaTimeFilter));
    }

    public string AgendaHalfFilter
    {
        get => _agendaHalfFilter;
        set => SetAgendaFilter(ref _agendaHalfFilter, value, AgendaHalfOptions, nameof(AgendaHalfFilter));
    }

    public string AgendaSurfaceFilter
    {
        get => _agendaSurfaceFilter;
        set => SetAgendaFilter(ref _agendaSurfaceFilter, value, AgendaSurfaceOptions, nameof(AgendaSurfaceFilter));
    }

    public string AgendaDistanceFilter
    {
        get => _agendaDistanceFilter;
        set => SetAgendaFilter(ref _agendaDistanceFilter, value, AgendaDistanceOptions, nameof(AgendaDistanceFilter));
    }

    public string AgendaTrackFilter
    {
        get => _agendaTrackFilter;
        set => SetAgendaFilter(ref _agendaTrackFilter, value, AgendaTrackOptions, nameof(AgendaTrackFilter));
    }

    public string AgendaDirectionFilter
    {
        get => _agendaDirectionFilter;
        set => SetAgendaFilter(ref _agendaDirectionFilter, value, AgendaDirectionOptions, nameof(AgendaDirectionFilter));
    }

    public string AgendaGradeFilter
    {
        get => _agendaGradeFilter;
        set => SetAgendaFilter(ref _agendaGradeFilter, value, AgendaGradeOptions, nameof(AgendaGradeFilter));
    }

    public string SkillSearchText
    {
        get => _skillSearchText;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!Set(ref _skillSearchText, normalized))
                return;
            ApplySkillSearch();
        }
    }

    public int SelectedAgendaCount => _allRaceOptions.Count(item => item.IsSelected);

    public int SelectedSkillCount => _allSkillOptions.Count(item => item.IsSelected);

    public string SelectedAgendaCountText => $"Agenda races selected: {SelectedAgendaCount}";

    public string SelectedSkillCountText => $"Skills to add: {SelectedSkillCount}";

    public string AgendaSelectionsText
    {
        get => string.Join(Environment.NewLine,
            _allRaceOptions.Where(item => item.IsSelected).Select(item => item.Race.Key));
        set
        {
            var selected = ParseAgendaSelectionKeys(value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _updatingAgenda = true;
            foreach (var option in _allRaceOptions)
                option.IsSelected = selected.Contains(option.Race.Key);
            _updatingAgenda = false;
            NotifyAgendaChanged();
        }
    }

    public string SkillIdsText
    {
        get => string.Join(",", _allSkillOptions
            .Where(item => item.IsSelected)
            .Select(item => item.Skill.SkillId.ToString(CultureInfo.InvariantCulture)));
        set
        {
            var selected = ParseSkillIdsSafely(value).ToHashSet();
            _updatingSkills = true;
            foreach (var option in _allSkillOptions)
                option.IsSelected = selected.Contains(option.Skill.SkillId);
            _updatingSkills = false;
            NotifySkillsChanged();
        }
    }

    public bool IsValid
    {
        get
        {
            if (!IsCareerModeActive)
                return true;
            if (!_catalog.IsAvailable
                || !TrainingFocusOptions.Any(item => item.Value.Equals(TrainingFocus, StringComparison.OrdinalIgnoreCase))
                || !LineupStrategyOptions.Any(item => item.Value.Equals(LineupStrategy, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var selectedRaces = _allRaceOptions.Where(item => item.IsSelected)
                .Select(item => item.Race.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedSkills = _allSkillOptions.Where(item => item.IsSelected)
                .Select(item => item.Skill.SkillId)
                .ToHashSet();
            var agendaEntries = _allRaceOptions.Where(item => item.IsSelected)
                .Select(item => new IndependentTrainingAgendaSelection(
                    item.Race.Year,
                    item.Race.Turn,
                    item.Race.RaceName))
                .ToArray();
            return selectedRaces.All(key => _catalog.Races.Any(item =>
                       item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                && agendaEntries.All(item => _catalog.TryGetAgendaPickerEntry(item, out _))
                && selectedSkills.All(id => _catalog.Skills.Any(item => item.SkillId == id))
                && selectedSkills.All(id => _catalog.Skills.Any(item =>
                    item.SkillId == id
                    && item.AvailableInGlobal
                    && item.SingleModeEnabled
                    && !string.IsNullOrWhiteSpace(item.EffectiveSearchText)));
        }
    }

    public IReadOnlyList<IndependentTrainingAgendaSelection> ParseAgendaSelections() =>
        _allRaceOptions.Where(item => item.IsSelected)
            .Select(item => new IndependentTrainingAgendaSelection(
                item.Race.Year,
                item.Race.Turn,
                item.Race.RaceName))
            .ToArray();

    public IReadOnlyList<int> ParseSkillIds()
    {
        var ids = new List<int>();
        foreach (var token in SkillIdsText.Split(
                     [',', ' ', ';', '\r', '\n', '\t'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || id <= 0)
            {
                throw new InvalidOperationException($"Invalid independent skill ID '{token}'.");
            }
            if (!ids.Contains(id))
                ids.Add(id);
        }
        return ids;
    }

    public static IReadOnlyList<string> ParseAgendaSelectionKeys(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        return text.Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Count(character => character == '|') >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void RefreshCatalog(string? baseDirectory = null)
    {
        var selectedAgenda = ParseAgendaSelectionKeys(AgendaSelectionsText)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedSkills = ParseSkillIdsSafely(SkillIdsText).ToHashSet();

        _catalog = IndependentTrainingCatalog.Load(baseDirectory);
        _allRaceOptions.Clear();
        _allSkillOptions.Clear();

        foreach (var race in _catalog.Races
                     .OrderBy(item => item.Year, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Turn, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.RaceName, StringComparer.OrdinalIgnoreCase))
        {
            var selection = new IndependentTrainingAgendaSelection(
                race.Year,
                race.Turn,
                race.RaceName);
            var hasPickerEntry = _catalog.TryGetAgendaPickerEntry(selection, out _);
            var raceCardImagePath = hasPickerEntry
                ? IndependentTrainingCatalog.TryResolveRaceCardImagePath(race, baseDirectory)
                : null;
            var isExecutable = hasPickerEntry && raceCardImagePath is not null;
            var option = new IndependentRaceOption(race, isExecutable, raceCardImagePath)
            {
                IsSelected = selectedAgenda.Contains(race.Key) && isExecutable,
            };
            option.PropertyChanged += OnRaceOptionChanged;
            _allRaceOptions.Add(option);
        }

        foreach (var skill in _catalog.Skills
                     .OrderBy(item => item.SkillName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.SkillId))
        {
            var isExecutable = skill.AvailableInGlobal
                && skill.SingleModeEnabled
                && !string.IsNullOrWhiteSpace(skill.EffectiveSearchText);
            var option = new IndependentSkillOption(skill, isExecutable)
            {
                IsSelected = selectedSkills.Contains(skill.SkillId) && isExecutable,
            };
            option.PropertyChanged += OnSkillOptionChanged;
            _allSkillOptions.Add(option);
        }

        AgendaYearOptions = BuildAgendaFilterOptions(
            _allRaceOptions.Select(item => item.Race.Year));
        AgendaMonthOptions = BuildAgendaNumericFilterOptions(
            _allRaceOptions
                .Select(item => item.Race.Month)
                .Where(month => month is >= 1 and <= 12));
        AgendaTurnOptions = BuildAgendaFilterOptions(
            _allRaceOptions.Select(item => item.Race.Turn));
        AgendaDayOptions = BuildAgendaNumericFilterOptions(
            _allRaceOptions.Select(item => item.Race.Day));
        AgendaTimeOptions = BuildAgendaFilterOptions(
            _allRaceOptions.Select(item => item.Race.TimeName));
        AgendaHalfOptions = BuildAgendaFilterOptions(
            _allRaceOptions.Select(item => item.Race.Half));
        AgendaSurfaceOptions = BuildAgendaFilterOptions(
            _allRaceOptions.Select(item => item.Race.Surface));
        AgendaDistanceOptions = BuildAgendaDistanceFilterOptions(
            _allRaceOptions.Select(item => item.Race.DistanceCategory));
        AgendaTrackOptions = BuildAgendaFilterOptions(
            _allRaceOptions.Select(item => item.Race.GameTrack));
        AgendaDirectionOptions = BuildAgendaFilterOptions(
            _allRaceOptions.Select(item => item.Race.Direction));
        AgendaGradeOptions = BuildAgendaFilterOptions(
            _allRaceOptions.Select(item => item.Race.Grade));

        OnPropertyChanged(nameof(AgendaYearOptions));
        OnPropertyChanged(nameof(AgendaMonthOptions));
        OnPropertyChanged(nameof(AgendaTurnOptions));
        OnPropertyChanged(nameof(AgendaDayOptions));
        OnPropertyChanged(nameof(AgendaTimeOptions));
        OnPropertyChanged(nameof(AgendaHalfOptions));
        OnPropertyChanged(nameof(AgendaSurfaceOptions));
        OnPropertyChanged(nameof(AgendaDistanceOptions));
        OnPropertyChanged(nameof(AgendaTrackOptions));
        OnPropertyChanged(nameof(AgendaDirectionOptions));
        OnPropertyChanged(nameof(AgendaGradeOptions));
        NormalizeAgendaFilter(ref _agendaYearFilter, AgendaYearOptions, nameof(AgendaYearFilter));
        NormalizeAgendaFilter(ref _agendaMonthFilter, AgendaMonthOptions, nameof(AgendaMonthFilter));
        NormalizeAgendaFilter(ref _agendaTurnFilter, AgendaTurnOptions, nameof(AgendaTurnFilter));
        NormalizeAgendaFilter(ref _agendaDayFilter, AgendaDayOptions, nameof(AgendaDayFilter));
        NormalizeAgendaFilter(ref _agendaTimeFilter, AgendaTimeOptions, nameof(AgendaTimeFilter));
        NormalizeAgendaFilter(ref _agendaHalfFilter, AgendaHalfOptions, nameof(AgendaHalfFilter));
        NormalizeAgendaFilter(ref _agendaSurfaceFilter, AgendaSurfaceOptions, nameof(AgendaSurfaceFilter));
        NormalizeAgendaFilter(ref _agendaDistanceFilter, AgendaDistanceOptions, nameof(AgendaDistanceFilter));
        NormalizeAgendaFilter(ref _agendaTrackFilter, AgendaTrackOptions, nameof(AgendaTrackFilter));
        NormalizeAgendaFilter(ref _agendaDirectionFilter, AgendaDirectionOptions, nameof(AgendaDirectionFilter));
        NormalizeAgendaFilter(ref _agendaGradeFilter, AgendaGradeOptions, nameof(AgendaGradeFilter));

        ApplyAgendaSearch();
        ApplySkillSearch();
        NotifyAgendaChanged();
        NotifySkillsChanged();
        OnPropertyChanged(nameof(IndependentRaceOptions));
        OnPropertyChanged(nameof(IndependentSkillOptions));
        OnPropertyChanged(nameof(IsValid));
    }

    private void ResetAgenda()
    {
        _updatingAgenda = true;
        foreach (var option in _allRaceOptions)
            option.IsSelected = false;
        _updatingAgenda = false;
        AgendaSearchText = string.Empty;
        ResetAgendaFilters();
        NotifyAgendaChanged();
    }

    private void ResetAgendaFilters()
    {
        AgendaYearFilter = AllAgendaFilters;
        AgendaMonthFilter = AllAgendaFilters;
        AgendaTurnFilter = AllAgendaFilters;
        AgendaDayFilter = AllAgendaFilters;
        AgendaTimeFilter = AllAgendaFilters;
        AgendaHalfFilter = AllAgendaFilters;
        AgendaSurfaceFilter = AllAgendaFilters;
        AgendaDistanceFilter = AllAgendaFilters;
        AgendaTrackFilter = AllAgendaFilters;
        AgendaDirectionFilter = AllAgendaFilters;
        AgendaGradeFilter = AllAgendaFilters;
        IsAgendaFilterOpen = false;
    }

    private void ResetSkills()
    {
        _updatingSkills = true;
        foreach (var option in _allSkillOptions)
            option.IsSelected = false;
        _updatingSkills = false;
        SkillSearchText = string.Empty;
        NotifySkillsChanged();
    }

    private void OnRaceOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_updatingAgenda && sender is IndependentRaceOption
            && e.PropertyName == nameof(IndependentRaceOption.IsSelected))
        {
            NotifyAgendaChanged();
        }
    }

    private void OnSkillOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_updatingSkills && sender is IndependentSkillOption
            && e.PropertyName == nameof(IndependentSkillOption.IsSelected))
        {
            NotifySkillsChanged();
        }
    }

    private static IndependentTrainingOption[] BuildAgendaFilterOptions(
        IEnumerable<string> values,
        Func<string, string>? labelSelector = null)
    {
        return new[]
            {
                new IndependentTrainingOption(AllAgendaFilters, "All"),
            }
            .Concat(values
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => new IndependentTrainingOption(
                    value,
                    labelSelector?.Invoke(value) ?? value)))
            .ToArray();
    }

    private static IndependentTrainingOption[] BuildAgendaNumericFilterOptions(
        IEnumerable<int> values,
        Func<int, string>? labelSelector = null)
    {
        return new[]
            {
                new IndependentTrainingOption(AllAgendaFilters, "All"),
            }
            .Concat(values
                .Where(value => value > 0)
                .Distinct()
                .OrderBy(value => value)
                .Select(value =>
                {
                    var text = value.ToString(CultureInfo.InvariantCulture);
                    return new IndependentTrainingOption(
                        text,
                        labelSelector?.Invoke(value) ?? text);
                }))
            .ToArray();
    }

    private static IndependentTrainingOption[] BuildAgendaDistanceFilterOptions(
        IEnumerable<string> values)
    {
        var available = values
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new[]
            {
                new IndependentTrainingOption(AllAgendaFilters, "All"),
            }
            .Concat(AgendaDistanceCategories
                .Where(available.Contains)
                .Select(category => new IndependentTrainingOption(category, category)))
            .ToArray();
    }

    private void SetAgendaFilter(
        ref string field,
        string? value,
        IReadOnlyList<IndependentTrainingOption> options,
        string propertyName)
    {
        var candidate = value?.Trim() ?? AllAgendaFilters;
        var normalized = options.FirstOrDefault(item =>
            item.Value.Equals(candidate, StringComparison.OrdinalIgnoreCase))?.Value
            ?? AllAgendaFilters;

        if (!Set(ref field, normalized, propertyName))
            return;
        ApplyAgendaSearch();
    }

    private void NormalizeAgendaFilter(
        ref string field,
        IReadOnlyList<IndependentTrainingOption> options,
        string propertyName)
    {
        var current = field;
        if (options.Any(item =>
                item.Value.Equals(current, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        field = AllAgendaFilters;
        OnPropertyChanged(propertyName);
    }

    private void ApplyAgendaSearch()
    {
        var query = AgendaSearchText;
        FilteredIndependentRaceOptions.Clear();
        foreach (var option in _allRaceOptions)
        {
            var matchesSearch = string.IsNullOrWhiteSpace(query)
                || option.Race.DisplayLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Race.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Race.Month.ToString(CultureInfo.InvariantCulture)
                    .Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Race.GameDistance.ToString(CultureInfo.InvariantCulture)
                    .Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Race.RaceId.ToString(CultureInfo.InvariantCulture)
                    .Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Race.Day.ToString(CultureInfo.InvariantCulture)
                    .Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Race.TimeName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Race.Surface.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Race.DistanceCategory.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Race.Direction.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Race.GameTrack.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Race.Length.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Race.Grade.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Race.Half.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (matchesSearch
                && MatchesAgendaFilter(AgendaYearFilter, option.Race.Year)
                && MatchesAgendaFilter(AgendaMonthFilter, option.Race.Month.ToString(CultureInfo.InvariantCulture))
                && MatchesAgendaFilter(AgendaTurnFilter, option.Race.Turn)
                && MatchesAgendaFilter(AgendaDayFilter, option.Race.Day.ToString(CultureInfo.InvariantCulture))
                && MatchesAgendaFilter(AgendaTimeFilter, option.Race.TimeName)
                && MatchesAgendaFilter(AgendaHalfFilter, option.Race.Half)
                && MatchesAgendaFilter(AgendaSurfaceFilter, option.Race.Surface)
                && MatchesAgendaFilter(AgendaDistanceFilter, option.Race.DistanceCategory)
                && MatchesAgendaFilter(AgendaTrackFilter, option.Race.GameTrack)
                && MatchesAgendaFilter(AgendaDirectionFilter, option.Race.Direction)
                && MatchesAgendaFilter(AgendaGradeFilter, option.Race.Grade))
            {
                FilteredIndependentRaceOptions.Add(option);
            }
        }
    }

    private static bool MatchesAgendaFilter(string selected, string actual) =>
        selected.Equals(AllAgendaFilters, StringComparison.OrdinalIgnoreCase)
        || actual.Equals(selected, StringComparison.OrdinalIgnoreCase);

    private void ApplySkillSearch()
    {
        var query = SkillSearchText;
        FilteredIndependentSkillOptions.Clear();
        foreach (var option in _allSkillOptions)
        {
            if (string.IsNullOrWhiteSpace(query)
                || option.Skill.DisplayLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Skill.SearchTerms.Any(term => term.Contains(query, StringComparison.OrdinalIgnoreCase))
                || option.Skill.SkillId.ToString(CultureInfo.InvariantCulture)
                    .Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredIndependentSkillOptions.Add(option);
            }
        }
    }

    private void NotifyAgendaChanged()
    {
        OnPropertyChanged(nameof(AgendaSelectionsText));
        OnPropertyChanged(nameof(SelectedAgendaCount));
        OnPropertyChanged(nameof(SelectedAgendaCountText));
        OnPropertyChanged(nameof(IsValid));
    }

    private void NotifySkillsChanged()
    {
        OnPropertyChanged(nameof(SkillIdsText));
        OnPropertyChanged(nameof(SelectedSkillCount));
        OnPropertyChanged(nameof(SelectedSkillCountText));
        OnPropertyChanged(nameof(IsValid));
    }

    private static int[] ParseSkillIdsSafely(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        return text.Split(
                [',', ' ', ';', '\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries)
            .Select(token => int.TryParse(
                token,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var id) && id > 0 ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
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

    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;

        public RelayCommand(Action<object?> execute) => _execute = execute;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _execute(parameter);
    }
}
