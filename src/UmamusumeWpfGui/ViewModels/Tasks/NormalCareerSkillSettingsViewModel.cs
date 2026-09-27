using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.ViewModels.Tasks;

/// <summary>
/// Ordered skill targets configured specifically for Normal Career.
/// Only global skills that can be selected in the existing skill catalog are
/// offered here.
/// </summary>
public sealed class NormalCareerSkillSettingsViewModel : INotifyPropertyChanged
{
    private readonly IReadOnlyList<NormalCareerSkillOption> _allSkills;
    private string _searchText = string.Empty;
    private bool _isCareerModeActive;
    private NormalCareerSkillOption? _selectedAvailableSkill;
    private NormalCareerSkillOption? _selectedTargetSkill;

    public NormalCareerSkillSettingsViewModel(string? baseDirectory = null)
    {
        _allSkills = IndependentTrainingCatalog.Load(baseDirectory).Skills
            .Where(skill => skill.IsSelectable)
            .OrderBy(skill => skill.SkillName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(skill => skill.SkillId)
            .Select(skill => new NormalCareerSkillOption(skill))
            .ToArray();

        foreach (var skill in _allSkills)
            FilteredSkills.Add(skill);

        AddSelectedSkillCommand = new RelayCommand(
            _ => AddSelectedSkill(),
            _ => SelectedAvailableSkill is not null && !TargetSkills.Contains(SelectedAvailableSkill));
        MoveSelectedSkillUpCommand = new RelayCommand(
            _ => MoveSelectedTargetSkill(-1),
            _ => SelectedTargetSkill is not null && TargetSkills.IndexOf(SelectedTargetSkill) > 0);
        MoveSelectedSkillDownCommand = new RelayCommand(
            _ => MoveSelectedTargetSkill(1),
            _ => SelectedTargetSkill is not null
                && TargetSkills.IndexOf(SelectedTargetSkill) >= 0
                && TargetSkills.IndexOf(SelectedTargetSkill) < TargetSkills.Count - 1);
        RemoveSelectedSkillCommand = new RelayCommand(
            _ => RemoveSelectedTargetSkill(),
            _ => SelectedTargetSkill is not null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<NormalCareerSkillOption> FilteredSkills { get; } = [];

    public ObservableCollection<NormalCareerSkillOption> TargetSkills { get; } = [];

    public ICommand AddSelectedSkillCommand { get; }

    public ICommand MoveSelectedSkillUpCommand { get; }

    public ICommand MoveSelectedSkillDownCommand { get; }

    public ICommand RemoveSelectedSkillCommand { get; }

    public bool IsCareerModeActive
    {
        get => _isCareerModeActive;
        set => Set(ref _isCareerModeActive, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!Set(ref _searchText, normalized))
                return;
            SelectedAvailableSkill = null;
            ApplySearch();
        }
    }

    public NormalCareerSkillOption? SelectedAvailableSkill
    {
        get => _selectedAvailableSkill;
        set
        {
            if (!Set(ref _selectedAvailableSkill, value))
                return;
            OnPropertyChanged(nameof(CanAddSelectedSkill));
            RaiseCommandStates();
        }
    }

    public NormalCareerSkillOption? SelectedTargetSkill
    {
        get => _selectedTargetSkill;
        set
        {
            if (!Set(ref _selectedTargetSkill, value))
                return;
            OnPropertyChanged(nameof(CanMoveSelectedSkillUp));
            OnPropertyChanged(nameof(CanMoveSelectedSkillDown));
            RaiseCommandStates();
        }
    }

    public bool CanAddSelectedSkill =>
        SelectedAvailableSkill is not null && !TargetSkills.Contains(SelectedAvailableSkill);

    public bool CanMoveSelectedSkillUp =>
        SelectedTargetSkill is not null && TargetSkills.IndexOf(SelectedTargetSkill) > 0;

    public bool CanMoveSelectedSkillDown =>
        SelectedTargetSkill is not null
        && TargetSkills.IndexOf(SelectedTargetSkill) >= 0
        && TargetSkills.IndexOf(SelectedTargetSkill) < TargetSkills.Count - 1;

    public int SelectedTargetSkillCount => TargetSkills.Count;

    public IReadOnlyList<int> SelectedSkillIds => TargetSkills
        .Select(option => option.SkillId)
        .ToArray();

    /// <summary>
    /// Comma separated profile compatibility property. Its order is the
    /// purchase priority order shown by the editor.
    /// </summary>
    public string SkillIdsText
    {
        get => string.Join(",", SelectedSkillIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        set => SetSelectedSkillIds(ParseSkillIdsSafely(value));
    }

    public void SetSelectedSkillIds(IEnumerable<int> skillIds)
    {
        ArgumentNullException.ThrowIfNull(skillIds);
        var skillsById = _allSkills.ToDictionary(option => option.SkillId);
        var orderedSkills = skillIds
            .Where(id => id > 0)
            .Distinct()
            .Where(skillsById.ContainsKey)
            .Select(id => skillsById[id])
            .ToArray();

        TargetSkills.Clear();
        foreach (var skill in orderedSkills)
            TargetSkills.Add(skill);
        SelectedTargetSkill = TargetSkills.FirstOrDefault();
        NotifyTargetsChanged();
    }

    private void ApplySearch()
    {
        var query = SearchText;
        FilteredSkills.Clear();
        foreach (var option in _allSkills)
        {
            if (string.IsNullOrWhiteSpace(query)
                || option.Skill.DisplayLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.Skill.SearchTerms.Any(term => term.Contains(query, StringComparison.OrdinalIgnoreCase))
                || option.Skill.SkillId.ToString(CultureInfo.InvariantCulture)
                    .Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredSkills.Add(option);
            }
        }
    }

    private void AddSelectedSkill()
    {
        if (SelectedAvailableSkill is null || TargetSkills.Contains(SelectedAvailableSkill))
            return;

        TargetSkills.Add(SelectedAvailableSkill);
        SelectedTargetSkill = SelectedAvailableSkill;
        NotifyTargetsChanged();
    }

    private void MoveSelectedTargetSkill(int offset)
    {
        if (SelectedTargetSkill is null)
            return;

        var index = TargetSkills.IndexOf(SelectedTargetSkill);
        var targetIndex = index + offset;
        if (index < 0 || targetIndex < 0 || targetIndex >= TargetSkills.Count)
            return;

        var skill = SelectedTargetSkill;
        TargetSkills.Move(index, targetIndex);
        SelectedTargetSkill = skill;
        NotifyTargetsChanged();
    }

    private void RemoveSelectedTargetSkill()
    {
        if (SelectedTargetSkill is null)
            return;

        var index = TargetSkills.IndexOf(SelectedTargetSkill);
        if (index < 0)
            return;

        TargetSkills.RemoveAt(index);
        SelectedTargetSkill = TargetSkills.Count == 0
            ? null
            : TargetSkills[Math.Min(index, TargetSkills.Count - 1)];
        NotifyTargetsChanged();
    }

    private void NotifyTargetsChanged()
    {
        OnPropertyChanged(nameof(SkillIdsText));
        OnPropertyChanged(nameof(SelectedSkillIds));
        OnPropertyChanged(nameof(SelectedTargetSkillCount));
        OnPropertyChanged(nameof(CanAddSelectedSkill));
        OnPropertyChanged(nameof(CanMoveSelectedSkillUp));
        OnPropertyChanged(nameof(CanMoveSelectedSkillDown));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        (AddSelectedSkillCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveSelectedSkillUpCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveSelectedSkillDownCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RemoveSelectedSkillCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private static int[] ParseSkillIdsSafely(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        return text.Split([',', ' ', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                && id > 0 ? id : 0)
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

    private sealed class RelayCommand(Action<object?> execute, Predicate<object?> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute(parameter);

        public void Execute(object? parameter) => execute(parameter);

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class NormalCareerSkillOption(IndependentTrainingSkill skill)
{
    public IndependentTrainingSkill Skill { get; } = skill;

    public int SkillId => Skill.SkillId;

    public string Label => Skill.DisplayLabel;
}
