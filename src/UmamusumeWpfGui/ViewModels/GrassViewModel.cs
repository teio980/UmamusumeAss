using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.ViewModels;

/// <summary>
/// Generic MAA-style task queue coordinator.
/// Task-specific settings and execution belong to IGrassTaskModule instances.
/// </summary>
public sealed class GrassViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly LogViewModel _logViewModel;
    private readonly ILocalizationService _localizationService;
    private readonly IGrassTaskCatalog _taskCatalog;
    private readonly IConnectionStateService? _connectionState;
    private GrassTaskItemViewModel? _selectedTask;
    private GrassTaskItemViewModel? _runningTask;
    private Func<IReadOnlyList<IGrassTaskModule>, IGrassTaskModule?>? _requestTaskSelection;
    private bool _isAdvancedSettings;
    private bool _isQueueOperationInProgress;
    private bool _isQueueRunning;
    private CancellationTokenSource? _queueOperationCts;
    private bool _disposed;

    public GrassViewModel(
        LogViewModel logViewModel,
        ILocalizationService localizationService,
        IGrassTaskCatalog taskCatalog)
        : this(logViewModel, localizationService, taskCatalog, null)
    {
    }

    public GrassViewModel(
        LogViewModel logViewModel,
        ILocalizationService localizationService,
        IGrassTaskCatalog taskCatalog,
        IConnectionStateService? connectionState)
    {
        ArgumentNullException.ThrowIfNull(logViewModel);
        ArgumentNullException.ThrowIfNull(localizationService);
        ArgumentNullException.ThrowIfNull(taskCatalog);
        _logViewModel = logViewModel;
        _localizationService = localizationService;
        _taskCatalog = taskCatalog;
        _connectionState = connectionState;

        Tasks = [];
        _localizationService.LanguageChanged += OnLanguageChanged;
        if (_connectionState is not null)
            _connectionState.StateChanged += OnConnectionStateChanged;
        RefreshLocalizedText();

        AddTaskCommand = new RelayCommand(_ => AddTask(), _ => CanAddTask);
        RemoveTaskCommand = new RelayCommand(_ => RemoveSelectedTask(), _ => SelectedTask is not null);
        CopyTaskCommand = new RelayCommand(_ => CopySelectedTask(), _ => SelectedTask is not null);
        SelectAllCommand = new RelayCommand(_ => SetAllTasks(true));
        InvertSelectionCommand = new RelayCommand(_ => InvertTaskSelection());
        StartCommand = new RelayCommand(_ => _ = StartQueueAsync(), _ => CanStartQueue);
        StopCommand = new RelayCommand(_ => _ = StopQueueAsync(), _ => CanStopQueue);
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

    public string SelectedTaskDescription => SelectedTask?.Description
        ?? Localize("GrassSelectTaskHint", "Select a task on the left to view its settings");

    public string TaskCountSummary => string.Format(
        CultureInfo.InvariantCulture,
        Localize("GrassTaskCountSummary", "Configured {0} tasks, {1} enabled"),
        Tasks.Count,
        Tasks.Count(task => task.IsEnabled));

    public bool IsConnected =>
        _connectionState?.State == ConnectionState.Connected
        && _connectionState.LastVerifiedConnection is not null;

    public bool IsQueueOperationInProgress
    {
        get => _isQueueOperationInProgress;
        private set
        {
            if (_isQueueOperationInProgress == value)
                return;
            _isQueueOperationInProgress = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PageStatus));
            RaiseQueueCommandStateChanged();
        }
    }

    public bool IsQueueRunning
    {
        get => _isQueueRunning;
        private set
        {
            if (_isQueueRunning == value)
                return;
            _isQueueRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PageStatus));
            RaiseQueueCommandStateChanged();
        }
    }

    public string PageStatus => _connectionState is null
        ? Localize("GrassExecutionFuture", "Task execution will be added in a later version")
        : IsQueueOperationInProgress
            ? Localize("GrassGameStarting", "Starting queue")
            : IsQueueRunning
                ? Localize("GrassGameRunning", "Queue is running")
                : IsConnected
                    ? Localize("GrassGameReady", "Queue is ready")
                    : Localize(
                        "GrassGameConnectionRequired",
                        "Connect a device in Settings to start the queue");

    public bool CanStartQueue =>
        _connectionState is not null
        && IsConnected
        && !IsQueueOperationInProgress
        && !IsQueueRunning
        && Tasks.Any(task => task.IsEnabled && task.Module.CanExecute(CurrentContext));

    public bool CanStopQueue =>
        _connectionState is not null
        && IsConnected
        && !IsQueueOperationInProgress
        && IsQueueRunning
        && _runningTask is not null;

    /// <summary>
    /// Future UI task picker seam. With one module, Add uses it directly.
    /// </summary>
    public Func<IReadOnlyList<IGrassTaskModule>, IGrassTaskModule?>? RequestTaskSelection
    {
        get => _requestTaskSelection;
        set
        {
            if (ReferenceEquals(_requestTaskSelection, value))
                return;
            _requestTaskSelection = value;
            OnPropertyChanged();
            ((RelayCommand)AddTaskCommand).RaiseCanExecuteChanged();
        }
    }

    public bool CanAddTask =>
        _taskCatalog.Modules.Count == 1
        || (_requestTaskSelection is not null && _taskCatalog.Modules.Count > 0);

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
        if (_connectionState is not null)
            _connectionState.StateChanged -= OnConnectionStateChanged;
        _queueOperationCts?.Cancel();
        _queueOperationCts?.Dispose();
        _queueOperationCts = null;
        foreach (var task in Tasks)
            task.PropertyChanged -= OnTaskPropertyChanged;
    }

    private GrassTaskExecutionContext CurrentContext =>
        new(_connectionState?.LastVerifiedConnection);

    private void AddTask()
    {
        if (!CanAddTask)
            return;

        var prototype = _requestTaskSelection is not null
            ? _requestTaskSelection(_taskCatalog.Modules)
            : _taskCatalog.Modules.Count == 1
                ? _taskCatalog.Modules[0]
                : null;
        if (prototype is null)
            return;

        var item = CreateTaskItem(prototype.CreateInstance());
        item.PropertyChanged += OnTaskPropertyChanged;
        Tasks.Add(item);
        SelectedTask = item;
        NotifyTaskSummaryChanged();
        RaiseQueueCommandStateChanged();
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
        RaiseQueueCommandStateChanged();
    }

    private void CopySelectedTask()
    {
        if (SelectedTask is null)
            return;

        var copy = CreateTaskItem(SelectedTask.Module.CreateInstance());
        copy.IsEnabled = SelectedTask.IsEnabled;
        copy.PropertyChanged += OnTaskPropertyChanged;
        var index = Tasks.IndexOf(SelectedTask);
        Tasks.Insert(index + 1, copy);
        SelectedTask = copy;
        NotifyTaskSummaryChanged();
        RaiseQueueCommandStateChanged();
    }

    private void SetAllTasks(bool enabled)
    {
        foreach (var task in Tasks)
            task.IsEnabled = enabled;
        NotifyTaskSummaryChanged();
        RaiseQueueCommandStateChanged();
    }

    private void InvertTaskSelection()
    {
        foreach (var task in Tasks)
            task.IsEnabled = !task.IsEnabled;
        NotifyTaskSummaryChanged();
        RaiseQueueCommandStateChanged();
    }

    private async Task StartQueueAsync()
    {
        if (!CanStartQueue)
            return;

        var context = CurrentContext;
        var queuedTasks = Tasks.Where(task => task.IsEnabled).ToList();
        IsQueueOperationInProgress = true;
        _queueOperationCts?.Dispose();
        _queueOperationCts = new CancellationTokenSource();

        try
        {
            foreach (var task in queuedTasks)
            {
                if (!task.Module.CanExecute(context))
                {
                    task.Status = Localize("GrassTaskError", "Error");
                    continue;
                }

                _runningTask = task;
                task.Status = Localize("GrassTaskRunning", "Running");
                var result = await task.Module.ExecuteAsync(
                    context,
                    _queueOperationCts.Token).ConfigureAwait(true);
                task.Status = result.Succeeded
                    ? Localize("GrassTaskCompleted", "Completed")
                    : Localize("GrassTaskError", "Error");
                _logViewModel.AddLocal(
                    task.Name,
                    result.Message,
                    result.Succeeded ? LogEntryKind.Success : LogEntryKind.Failure);

                if (!result.Succeeded)
                    break;

                IsQueueRunning = true;
            }
        }
        catch (OperationCanceledException)
        {
            if (_runningTask is not null)
                _runningTask.Status = Localize("GrassTaskIdle", "Idle");
        }
        catch (Exception exception)
        {
            if (_runningTask is not null)
                _runningTask.Status = Localize("GrassTaskError", "Error");
            _logViewModel.AddLocal(
                Localize("GrassLogs", "Activity log"),
                exception.Message,
                LogEntryKind.Failure);
        }
        finally
        {
            IsQueueOperationInProgress = false;
            RaiseQueueCommandStateChanged();
        }
    }

    private async Task StopQueueAsync()
    {
        var runningTask = _runningTask;
        if (!CanStopQueue || runningTask is null)
            return;

        IsQueueOperationInProgress = true;
        _queueOperationCts?.Dispose();
        _queueOperationCts = new CancellationTokenSource();
        try
        {
            var result = await runningTask.Module.StopAsync(
                CurrentContext,
                _queueOperationCts.Token).ConfigureAwait(true);
            _logViewModel.AddLocal(
                runningTask.Name,
                result.Message,
                result.Succeeded ? LogEntryKind.Success : LogEntryKind.Failure);
            if (result.Succeeded)
            {
                runningTask.Status = Localize("GrassTaskCompleted", "Completed");
                IsQueueRunning = false;
                _runningTask = null;
            }
            else
            {
                runningTask.Status = Localize("GrassTaskError", "Error");
            }
        }
        catch (OperationCanceledException)
        {
            // The task remains marked as running so the user can retry Stop.
        }
        catch (Exception exception)
        {
            _logViewModel.AddLocal(
                runningTask.Name,
                exception.Message,
                LogEntryKind.Failure);
        }
        finally
        {
            IsQueueOperationInProgress = false;
            RaiseQueueCommandStateChanged();
        }
    }

    private void NotifyTaskSummaryChanged() => OnPropertyChanged(nameof(TaskCountSummary));

    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GrassTaskItemViewModel.IsEnabled))
        {
            NotifyTaskSummaryChanged();
            RaiseQueueCommandStateChanged();
        }
    }

    private void OnLanguageChanged(object? sender, string culture)
    {
        RefreshLocalizedText();
        OnPropertyChanged(nameof(SelectedTaskTitle));
        OnPropertyChanged(nameof(SelectedTaskDescription));
        OnPropertyChanged(nameof(TaskCountSummary));
        OnPropertyChanged(nameof(PageStatus));
    }

    private void OnConnectionStateChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        if (!IsConnected)
        {
            IsQueueRunning = false;
            _runningTask = null;
        }
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(PageStatus));
        RaiseQueueCommandStateChanged();
    }

    private void RaiseQueueCommandStateChanged()
    {
        if (StartCommand is RelayCommand start)
            start.RaiseCanExecuteChanged();
        if (StopCommand is RelayCommand stop)
            stop.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanStartQueue));
        OnPropertyChanged(nameof(CanStopQueue));
    }

    private void RefreshLocalizedText()
    {
        foreach (var task in Tasks)
        {
            var definition = task.Module.Definition;
            task.UpdateText(
                Localize(definition.NameResourceKey, definition.FallbackName),
                Localize(definition.DescriptionResourceKey, definition.FallbackDescription));
        }
    }

    private GrassTaskItemViewModel CreateTaskItem(IGrassTaskModule module)
    {
        var definition = module.Definition;
        var item = new GrassTaskItemViewModel(module, definition.IsEnabledByDefault);
        item.UpdateText(
            Localize(definition.NameResourceKey, definition.FallbackName),
            Localize(definition.DescriptionResourceKey, definition.FallbackDescription));
        item.Status = Localize("GrassTaskIdle", "Idle");
        return item;
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
