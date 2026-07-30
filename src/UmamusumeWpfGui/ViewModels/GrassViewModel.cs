using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.ViewModels;

/// <summary>
/// MAA-inspired grass/task page state.
/// This phase owns queue editing and presentation only; no game launch or task
/// execution is wired yet.
/// </summary>
public sealed class GrassViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly LogViewModel _logViewModel;
    private readonly ILocalizationService _localizationService;
    private GrassTaskItemViewModel? _selectedTask;
    private bool _isAdvancedSettings;
    private readonly bool _executionImplemented;
    private bool _disposed;

    public GrassViewModel(
        LogViewModel logViewModel,
        ILocalizationService localizationService)
    {
        ArgumentNullException.ThrowIfNull(logViewModel);
        ArgumentNullException.ThrowIfNull(localizationService);
        _logViewModel = logViewModel;
        _localizationService = localizationService;
        _executionImplemented = false;

        Tasks =
        [
            new("Daily Training", "Training plan and daily development flow (not connected)"),
            new("Rewards Collection", "Collect available rewards (not connected)"),
            new("Friends & Shop", "Friend interactions and shop checks (not connected)"),
        ];
        foreach (var task in Tasks)
            task.PropertyChanged += OnTaskPropertyChanged;
        _selectedTask = Tasks[0];
        _localizationService.LanguageChanged += OnLanguageChanged;
        RefreshLocalizedText();

        AddTaskCommand = new RelayCommand(_ => AddTask());
        RemoveTaskCommand = new RelayCommand(_ => RemoveSelectedTask(), _ => SelectedTask is not null);
        CopyTaskCommand = new RelayCommand(_ => CopySelectedTask(), _ => SelectedTask is not null);
        SelectAllCommand = new RelayCommand(_ => SetAllTasks(true));
        InvertSelectionCommand = new RelayCommand(_ => InvertTaskSelection());
        StartCommand = new RelayCommand(_ => { }, _ => false);
        StopCommand = new RelayCommand(_ => { }, _ => false);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<GrassTaskItemViewModel> Tasks { get; }

    public ObservableCollection<LogEntry> Logs => _logViewModel.Entries;

    public GrassTaskItemViewModel? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (ReferenceEquals(_selectedTask, value))
                return;
            _selectedTask = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedTaskTitle));
            OnPropertyChanged(nameof(SelectedTaskDescription));
            ((RelayCommand)RemoveTaskCommand).RaiseCanExecuteChanged();
            ((RelayCommand)CopyTaskCommand).RaiseCanExecuteChanged();
        }
    }

    public string SelectedTaskTitle => SelectedTask?.Name
        ?? Localize("GrassNoTaskSelected", "No task selected");

    public string SelectedTaskDescription =>
        SelectedTask?.Description
        ?? Localize("GrassSelectTaskHint", "Select a task on the left to view its settings");

    public string TaskCountSummary =>
        string.Format(
            CultureInfo.InvariantCulture,
            Localize("GrassTaskCountSummary", "Configured {0} tasks, {1} enabled"),
            Tasks.Count,
            Tasks.Count(task => task.IsEnabled));

    public string PageStatus => _executionImplemented
        ? Localize("GrassExecutionEnabled", "Task execution is enabled")
        : Localize("GrassExecutionFuture", "Task execution will be added in a later version");

    public bool IsAdvancedSettings
    {
        get => _isAdvancedSettings;
        set
        {
            if (_isAdvancedSettings == value)
                return;
            _isAdvancedSettings = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsGeneralSettings));
        }
    }

    public bool IsGeneralSettings
    {
        get => !IsAdvancedSettings;
        set
        {
            if (value == IsGeneralSettings)
                return;
            IsAdvancedSettings = !value;
        }
    }

    public ICommand AddTaskCommand { get; }
    public ICommand RemoveTaskCommand { get; }
    public ICommand CopyTaskCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand InvertSelectionCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        foreach (var task in Tasks)
            task.PropertyChanged -= OnTaskPropertyChanged;
    }

    private void AddTask()
    {
        var item = new GrassTaskItemViewModel(
            string.Format(
                CultureInfo.InvariantCulture,
                Localize("GrassCustomTaskName", "Custom Task {0}"),
                Tasks.Count + 1),
            Localize("GrassCustomTaskDescription", "Custom task settings (not connected)"));
        item.PropertyChanged += OnTaskPropertyChanged;
        Tasks.Add(item);
        SelectedTask = item;
        NotifyTaskSummaryChanged();
    }

    private void RemoveSelectedTask()
    {
        if (SelectedTask is null)
            return;

        var index = Tasks.IndexOf(SelectedTask);
        SelectedTask.PropertyChanged -= OnTaskPropertyChanged;
        Tasks.Remove(SelectedTask);
        SelectedTask = Tasks.ElementAtOrDefault(Math.Max(0, index - 1));
        NotifyTaskSummaryChanged();
    }

    private void CopySelectedTask()
    {
        if (SelectedTask is null)
            return;

        var copy = new GrassTaskItemViewModel(
            string.Format(
                CultureInfo.InvariantCulture,
                Localize("GrassTaskCopyName", "{0} Copy"),
                SelectedTask.Name),
            SelectedTask.Description,
            SelectedTask.IsEnabled);
        copy.PropertyChanged += OnTaskPropertyChanged;
        var index = Tasks.IndexOf(SelectedTask);
        Tasks.Insert(index + 1, copy);
        SelectedTask = copy;
        NotifyTaskSummaryChanged();
    }

    private void SetAllTasks(bool enabled)
    {
        foreach (var task in Tasks)
            task.IsEnabled = enabled;
        NotifyTaskSummaryChanged();
    }

    private void InvertTaskSelection()
    {
        foreach (var task in Tasks)
            task.IsEnabled = !task.IsEnabled;
        NotifyTaskSummaryChanged();
    }

    private void NotifyTaskSummaryChanged() => OnPropertyChanged(nameof(TaskCountSummary));

    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GrassTaskItemViewModel.IsEnabled))
            NotifyTaskSummaryChanged();
    }

    private void OnLanguageChanged(object? sender, string culture)
    {
        RefreshLocalizedText();
        OnPropertyChanged(nameof(SelectedTaskTitle));
        OnPropertyChanged(nameof(SelectedTaskDescription));
        OnPropertyChanged(nameof(TaskCountSummary));
        OnPropertyChanged(nameof(PageStatus));
    }

    private void RefreshLocalizedText()
    {
        var definitions = new[]
        {
            ("GrassTaskDailyTraining", "GrassTaskDailyTrainingDescription", "Daily Training", "Training plan and daily development flow (not connected)"),
            ("GrassTaskRewardsCollection", "GrassTaskRewardsCollectionDescription", "Rewards Collection", "Collect available rewards (not connected)"),
            ("GrassTaskFriendsShop", "GrassTaskFriendsShopDescription", "Friends & Shop", "Friend interactions and shop checks (not connected)"),
        };

        for (var index = 0; index < definitions.Length && index < Tasks.Count; index++)
        {
            var definition = definitions[index];
            Tasks[index].UpdateText(
                Localize(definition.Item1, definition.Item3),
                Localize(definition.Item2, definition.Item4));
            Tasks[index].Status = Localize("GrassTaskIdle", "Idle");
        }
    }

    private string Localize(string key, string fallback)
    {
        var localized = _localizationService.GetString(key);
        return string.IsNullOrWhiteSpace(localized) || localized == key
            ? fallback
            : localized;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
