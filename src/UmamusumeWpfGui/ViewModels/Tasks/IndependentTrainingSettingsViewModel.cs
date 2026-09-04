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
    private string _trainingFocus = CareerTrainingTaskSettingsViewModel.IndependentTrainingFocusBalanced;
    private string _lineupStrategy = CareerTrainingTaskSettingsViewModel.IndependentLineupStrategyPace;
    private string _agendaSearchText = string.Empty;
    private string _skillSearchText = string.Empty;
    private bool _updatingAgenda;
    private bool _updatingSkills;
    private bool _isCareerModeActive = true;
    private IndependentTrainingCatalog _catalog = IndependentTrainingCatalog.Load();
    private readonly List<IndependentRaceOption> _allRaceOptions = [];
    private readonly List<IndependentSkillOption> _allSkillOptions = [];

    public IndependentTrainingSettingsViewModel()
    {
        ResetIndependentAgendaCommand = new RelayCommand(_ => ResetAgenda());
        ResetIndependentSkillsCommand = new RelayCommand(_ => ResetSkills());
        RefreshCatalog();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<IndependentRaceOption> FilteredIndependentRaceOptions { get; } = [];

    public ObservableCollection<IndependentSkillOption> FilteredIndependentSkillOptions { get; } = [];

    public IReadOnlyList<IndependentRaceOption> IndependentRaceOptions => _allRaceOptions;

    public IReadOnlyList<IndependentSkillOption> IndependentSkillOptions => _allSkillOptions;

    public ICommand ResetIndependentAgendaCommand { get; }

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
        NotifyAgendaChanged();
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

    private void ApplyAgendaSearch()
    {
        var query = AgendaSearchText;
        FilteredIndependentRaceOptions.Clear();
        foreach (var option in _allRaceOptions)
        {
            if (string.IsNullOrWhiteSpace(query)
                || option.Race.DisplayLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Race.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredIndependentRaceOptions.Add(option);
            }
        }
    }

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
