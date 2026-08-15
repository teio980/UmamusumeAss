using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.ViewModels;





public sealed class GrassViewModel : INotifyPropertyChanged, IDisposable, IGrassTaskLogSink
{
    private readonly ILocalizationService _localizationService;
    private readonly IGrassTaskCatalog _taskCatalog;
    private readonly IConnectionStateService? _connectionState;
    private readonly ISettingsService? _settingsService;
    private readonly SettingsViewModel? _settingsViewModel;
    private readonly IAdbRuntime? _adbRuntime;
    private GrassTaskItemViewModel? _selectedTask;
    private GrassTaskItemViewModel? _runningTask;
    private Func<IReadOnlyList<IGrassTaskModule>, IGrassTaskModule?>? _requestTaskSelection;
    private bool _isAdvancedSettings;
    private bool _isQueueOperationInProgress;
    private bool _isQueueRunning;
    private bool _stopRequested;
    private CancellationTokenSource? _queueOperationCts;
    private bool _disposed;

    public GrassViewModel(
        LogViewModel logViewModel,
        ILocalizationService localizationService,
        IGrassTaskCatalog taskCatalog)
        : this(logViewModel, localizationService, taskCatalog, null, null)
    {
    }

    public GrassViewModel(
        LogViewModel logViewModel,
        ILocalizationService localizationService,
        IGrassTaskCatalog taskCatalog,
        IConnectionStateService? connectionState)
        : this(logViewModel, localizationService, taskCatalog, connectionState, null)
    {
    }

    public GrassViewModel(
        LogViewModel logViewModel,
        ILocalizationService localizationService,
        IGrassTaskCatalog taskCatalog,
        IConnectionStateService? connectionState,
        ISettingsService? settingsService)
        : this(logViewModel, localizationService, taskCatalog, connectionState, settingsService, null)
    {
    }

    public GrassViewModel(
        LogViewModel logViewModel,
        ILocalizationService localizationService,
        IGrassTaskCatalog taskCatalog,
        IConnectionStateService? connectionState,
        ISettingsService? settingsService,
        SettingsViewModel? settingsViewModel,
        IAdbRuntime? adbRuntime = null)
    {
        ArgumentNullException.ThrowIfNull(logViewModel);
        ArgumentNullException.ThrowIfNull(localizationService);
        ArgumentNullException.ThrowIfNull(taskCatalog);
        _localizationService = localizationService;
        _taskCatalog = taskCatalog;
        _connectionState = connectionState;
        _settingsService = settingsService;
        _settingsViewModel = settingsViewModel;
        _adbRuntime = adbRuntime;
        HachimiShopSettings = new HachimiShopSettingsViewModel(settingsService);

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

        RestoreTaskQueue();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<GrassTaskItemViewModel> Tasks { get; }





    public ObservableCollection<LogEntry> ScriptLogs { get; } = [];



    public ObservableCollection<LogEntry> Logs => ScriptLogs;

    public HachimiShopSettingsViewModel HachimiShopSettings { get; }

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
        && !IsQueueOperationInProgress
        && !IsQueueRunning
        && Tasks.Any(task => task.IsEnabled);

    public bool CanStopQueue =>
        _connectionState is not null
        && IsQueueRunning
        && _runningTask is not null
        && !_stopRequested;

    public bool CanReorderTasks =>
        !IsQueueOperationInProgress
        && !IsQueueRunning;




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
        get => IsGlobalSettings;
        set
        {
            IsGlobalSettings = value;
        }
    }

    public bool IsGeneralSettings
    {
        get => IsTaskSettings;
        set
        {
            if (value == IsGeneralSettings)
                return;
            IsGlobalSettings = !value;
        }
    }

    public bool IsGlobalSettings
    {
        get => _isAdvancedSettings;
        set
        {
            if (_isAdvancedSettings == value)
                return;
            _isAdvancedSettings = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTaskSettings));
            OnPropertyChanged(nameof(IsAdvancedSettings));
            OnPropertyChanged(nameof(IsGeneralSettings));
        }
    }

    public bool IsTaskSettings
    {
        get => !IsGlobalSettings;
        set => IsGlobalSettings = !value;
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
            DetachTaskEvents(task);
    }

    private GrassTaskExecutionContext CurrentContext =>
        new(IsConnected ? _connectionState?.LastVerifiedConnection : null, this);

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
        AttachTaskEvents(item);
        Tasks.Add(item);
        SelectedTask = item;
        SaveTaskQueueCache();
        NotifyTaskSummaryChanged();
        RaiseQueueCommandStateChanged();
    }

    private void RemoveSelectedTask()
    {
        if (SelectedTask is null)
            return;

        var index = Tasks.IndexOf(SelectedTask);
        DetachTaskEvents(SelectedTask);
        Tasks.Remove(SelectedTask);
        SelectedTask = Tasks.ElementAtOrDefault(Math.Max(0, index - 1));
        SaveTaskQueueCache();
        NotifyTaskSummaryChanged();
        RaiseQueueCommandStateChanged();
    }

    private void CopySelectedTask()
    {
        if (SelectedTask is null)
            return;

        var copiedModule = SelectedTask.Module.CreateInstance();
        copiedModule.ImportSettings(SelectedTask.Module.ExportSettings());
        var copy = CreateTaskItem(copiedModule);
        copy.IsEnabled = SelectedTask.IsEnabled;
        AttachTaskEvents(copy);
        var index = Tasks.IndexOf(SelectedTask);
        Tasks.Insert(index + 1, copy);
        SelectedTask = copy;
        SaveTaskQueueCache();
        NotifyTaskSummaryChanged();
        RaiseQueueCommandStateChanged();
    }

    private void SetAllTasks(bool enabled)
    {
        foreach (var task in Tasks)
            task.IsEnabled = enabled;
        SaveTaskQueueCache();
        NotifyTaskSummaryChanged();
        RaiseQueueCommandStateChanged();
    }

    private void InvertTaskSelection()
    {
        foreach (var task in Tasks)
            task.IsEnabled = !task.IsEnabled;
        SaveTaskQueueCache();
        NotifyTaskSummaryChanged();
        RaiseQueueCommandStateChanged();
    }

    private async Task StartQueueAsync()
    {
        if (!CanStartQueue)
            return;

        var queuedTasks = Tasks.Where(task => task.IsEnabled).ToList();
        _queueOperationCts?.Dispose();
        var operationCts = new CancellationTokenSource();
        _queueOperationCts = operationCts;
        _stopRequested = false;
        IsQueueOperationInProgress = true;
        ScriptLogs.Clear();
        AddScriptLog(
            Localize("GrassScriptQueue", "Task queue"),
            string.Format(
                CultureInfo.InvariantCulture,
                Localize("GrassScriptPreparing", "Preparing {0} task(s)"),
                queuedTasks.Count));
        foreach (var task in queuedTasks)
        {
            AddScriptLog(
                task.Name,
                Localize("GrassScriptTaskQueued", "Task queued"));
        }

        var queueSucceeded = false;

        try
        {



            var currentConnection = _connectionState?.LastVerifiedConnection;
            var cachedConnectionReady = currentConnection is not null
                && await IsCachedConnectionReadyAsync(currentConnection)
                    .ConfigureAwait(true);
            if (IsConnected && currentConnection is not null && cachedConnectionReady)
            {
                AddScriptLog(
                    Localize("GrassScriptEmulatorConnected", "Emulator connected"),
                    string.Format(
                        CultureInfo.InvariantCulture,
                        Localize("GrassScriptUsingConnection", "Using {0}"),
                        currentConnection.Serial),
                    LogEntryKind.Success);
            }
            else if (IsConnected && currentConnection is not null)
            {
                _connectionState?.SetState(ConnectionState.Disconnected);
                _connectionState?.ClearLastVerified();
                AddScriptLog(
                    Localize("GrassScriptConnectionStale", "Emulator connection changed"),
                    Localize(
                        "GrassScriptConnectionStaleDetails",
                        "The saved ADB connection is no longer ready; reconnecting"),
                    LogEntryKind.Failure);
            }
            else if (_settingsViewModel is not null)
            {
                if (_settingsViewModel.DraftAutoStartEmulator)
                {
                    var executable = _settingsViewModel.DraftEmulatorExecutablePath;
                    AddScriptLog(
                        Localize("GrassScriptStartingEmulator", "Starting configured emulator"),
                        string.IsNullOrWhiteSpace(executable)
                            ? Localize(
                                "GrassScriptCallingEmulatorStartup",
                                "Calling the emulator startup configured in Settings")
                            : string.Format(
                                CultureInfo.InvariantCulture,
                                Localize(
                                    "GrassScriptCallingEmulatorStartupWithPath",
                                    "Calling the emulator startup configured in Settings: {0}"),
                                executable));
                }
                else
                {
                    AddScriptLog(
                        Localize("GrassScriptConnecting", "Connecting to emulator"),
                        Localize(
                            "GrassScriptUsingSavedSettings",
                            "Using the saved emulator connection settings"));
                }

                AddScriptLog(
                    Localize("GrassScriptWaitingForEmulator", "Waiting for emulator connection"),
                    _settingsViewModel.DraftAutoDetect
                        ? Localize(
                            "GrassScriptWaitingForAdb",
                            "Waiting for the emulator to appear on ADB")
                        : Localize(
                            "GrassScriptWaitingForConfiguredAdb",
                            "Waiting for the configured ADB endpoint"));

                await _settingsViewModel.ConnectAsync(operationCts.Token)
                    .ConfigureAwait(true);

                if (_connectionState?.LastVerifiedConnection is { } connected)
                {
                    AddScriptLog(
                        Localize("GrassScriptEmulatorConnected", "Emulator connected"),
                        string.Format(
                            CultureInfo.InvariantCulture,
                            Localize("GrassScriptConnectedToDevice", "ADB connected to {0}"),
                            connected.Serial),
                        LogEntryKind.Success);
                }
                else
                {
                    AddScriptLog(
                        Localize("GrassScriptConnectionFailed", "Emulator connection failed"),
                        Localize(
                            "GrassScriptConnectionFailedDetails",
                            "The emulator did not become available through ADB"),
                        LogEntryKind.Failure);
                }
            }

            var context = CurrentContext;
            if (context.Connection is null)
            {
                AddScriptLog(
                    Localize("GrassScriptConnectionFailed", "Emulator connection failed"),
                    Localize(
                        "GrassGameConnectionRequired",
                        "Connect a device in Settings to start the queue"),
                    LogEntryKind.Failure);
                return;
            }

            foreach (var task in queuedTasks)
            {
                operationCts.Token.ThrowIfCancellationRequested();

                _runningTask = task;
                IsQueueRunning = true;
                task.Status = Localize("GrassTaskRunning", "Running");
                AddScriptLog(
                    task.Name,
                    Localize("GrassScriptTaskRunning", "Running task"));

                try
                {
                    if (!task.Module.CanExecute(context))
                    {
                        task.Status = Localize("GrassTaskError", "Error");
                        AddScriptLog(
                            task.Name,
                            Localize(
                                "GrassScriptTaskCannotExecute",
                                "Task cannot execute with the current configuration"),
                            LogEntryKind.Failure);
                        continue;
                    }

                    var result = await task.Module.ExecuteAsync(
                        context,
                        operationCts.Token).ConfigureAwait(true);
                    task.Status = result.Succeeded
                        ? Localize("GrassTaskCompleted", "Completed")
                        : Localize("GrassTaskError", "Error");
                    AddScriptLog(
                        task.Name,
                        result.Message,
                        result.Succeeded ? LogEntryKind.Success : LogEntryKind.Failure);

                    if (!result.Succeeded)
                        continue;

                    AddScriptLog(
                        task.Name,
                        Localize("GrassScriptTaskCompleted", "Task completed"),
                        LogEntryKind.Success);
                }
                catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    task.Status = Localize("GrassTaskError", "Error");
                    AddScriptLog(task.Name, exception.Message, LogEntryKind.Failure);
                }
            }

            queueSucceeded = queuedTasks.Count > 0
                && queuedTasks.All(task =>
                    task.Status == Localize("GrassTaskCompleted", "Completed"));
        }
        catch (OperationCanceledException)
        {
            if (_runningTask is not null)
                _runningTask.Status = Localize("GrassTaskIdle", "Idle");
            AddScriptLog(
                Localize("GrassScriptQueue", "Task queue"),
                Localize("GrassScriptCanceled", "Task queue canceled"),
                LogEntryKind.Failure);
        }
        catch (Exception exception)
        {
            if (_runningTask is not null)
                _runningTask.Status = Localize("GrassTaskError", "Error");
            AddScriptLog(
                Localize("GrassScriptQueue", "Task queue"),
                exception.Message,
                LogEntryKind.Failure);
        }
        finally
        {
            if (queueSucceeded)
            {
                AddScriptLog(
                    Localize("GrassScriptQueue", "Task queue"),
                    Localize("GrassScriptCompleted", "Task queue completed"),
                    LogEntryKind.Success);
            }




            _runningTask = null;
            _stopRequested = false;
            IsQueueRunning = false;
            IsQueueOperationInProgress = false;
            if (ReferenceEquals(_queueOperationCts, operationCts))
            {
                _queueOperationCts = null;
                operationCts.Dispose();
            }
            RaiseQueueCommandStateChanged();
        }
    }

    private Task StopQueueAsync()
    {
        var runningTask = _runningTask;
        var operationCts = _queueOperationCts;
        if (!CanStopQueue || operationCts is null)
            return Task.CompletedTask;

        _stopRequested = true;
        RaiseQueueCommandStateChanged();
        AddScriptLog(
            runningTask?.Name ?? Localize("GrassScriptQueue", "Task queue"),
            Localize("GrassScriptStopRequested", "Stop requested; canceling the running task"));
        try
        {
            operationCts.Cancel();
            // Stop cancels the script queue only. It must not call a task
            // module's StopAsync method because that method may stop the game
            // process itself (for example, via ADB force-stop).
        }
        catch (OperationCanceledException)
        {

        }
        catch (Exception exception)
        {
            AddScriptLog(
                runningTask?.Name ?? Localize("GrassScriptQueue", "Task queue"),
                exception.Message,
                LogEntryKind.Failure);
        }
        finally
        {
            RaiseQueueCommandStateChanged();
        }

        return Task.CompletedTask;
    }

    private void RestoreTaskQueue()
    {
        if (_settingsService is null)
            return;

        MigrateLegacyShopTask();

        foreach (var cachedTask in _settingsService.Load().TaskQueue)
        {
            var taskId = MigrateTaskId(cachedTask.TaskId);
            var prototype = _taskCatalog.Modules.FirstOrDefault(module =>
                module.Definition.Id.Equals(taskId, StringComparison.Ordinal));
            if (prototype is null)
                continue;

            var module = prototype.CreateInstance();
            try
            {
                module.ImportSettings(cachedTask.Settings ?? new());
            }
            catch (Exception exception)
            {
                AddScriptLog(
                    Localize("GrassScriptQueue", "Task queue"),
                    $"Could not restore task '{taskId}': {exception.Message}",
                    LogEntryKind.Failure);
                continue;
            }

            var item = CreateTaskItem(module);
            item.IsEnabled = cachedTask.IsEnabled;
            AttachTaskEvents(item);
            Tasks.Add(item);
        }

        if (Tasks.Count > 0)
            SelectedTask = Tasks[0];

        NotifyTaskSummaryChanged();
        RaiseQueueCommandStateChanged();
    }

    private static string MigrateTaskId(string taskId) =>
        string.Equals(taskId, "ura-training", StringComparison.Ordinal)
            ? "career-training"
            : taskId;

    private void MigrateLegacyShopTask()
    {
        var service = _settingsService;
        if (service is null)
            return;

        var settings = service.Load();

        var legacyShop = settings.TaskQueue.FirstOrDefault(task =>
            string.Equals(task.TaskId, "shop", StringComparison.Ordinal));
        if (legacyShop is null)
            return;

        if (HachimiShopSettings.IsDefault)
            HachimiShopSettings.ImportLegacySettings(legacyShop.Settings ?? new());

        settings = service.Load();
        settings.TaskQueue = settings.TaskQueue
            .Where(task => !string.Equals(task.TaskId, "shop", StringComparison.Ordinal))
            .ToList();
        service.Save(settings);
    }

    private void SaveTaskQueueCache()
    {
        if (_settingsService is null)
            return;

        try
        {
            var settings = _settingsService.Load();
            settings.TaskQueue = Tasks.Select(task => new GrassTaskCacheItem
            {
                TaskId = task.Module.Definition.Id,
                IsEnabled = task.IsEnabled,
                Settings = task.Module.ExportSettings(),
            }).ToList();
            _settingsService.Save(settings);
        }
        catch (Exception exception)
        {
            AddScriptLog(
                Localize("GrassScriptQueue", "Task queue"),
                $"Could not save task queue: {exception.Message}",
                LogEntryKind.Failure);
        }
    }






    public void Add(string type, string details, LogEntryKind kind = LogEntryKind.Info)
    {
        if (_disposed)
            return;

        var entry = new LogEntry(DateTimeOffset.UtcNow, type, details, kind);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            try
            {
                dispatcher.Invoke(() => AppendScriptLog(entry));
            }
            catch (InvalidOperationException)
            {


            }

            return;
        }

        AppendScriptLog(entry);
    }

    private void AddScriptLog(
        string type,
        string details,
        LogEntryKind kind = LogEntryKind.Info) =>
        Add(type, details, kind);

    private void AppendScriptLog(LogEntry entry)
    {
        if (_disposed)
            return;

        ScriptLogs.Add(entry);
        if (ScriptLogs.Count > 500)
            ScriptLogs.RemoveAt(0);
    }

    private void AttachTaskEvents(GrassTaskItemViewModel task)
    {
        task.PropertyChanged += OnTaskPropertyChanged;
        if (task.Settings is INotifyPropertyChanged settings)
            settings.PropertyChanged += OnTaskSettingsPropertyChanged;
    }

    private void DetachTaskEvents(GrassTaskItemViewModel task)
    {
        task.PropertyChanged -= OnTaskPropertyChanged;
        if (task.Settings is INotifyPropertyChanged settings)
            settings.PropertyChanged -= OnTaskSettingsPropertyChanged;
    }

    private async Task<bool> IsCachedConnectionReadyAsync(
        LastVerifiedConnection connection)
    {
        if (_adbRuntime is null)
            return true;

        try
        {
            var devices = await _adbRuntime.ListDevicesAsync(
                connection.AdbPath,
                _queueOperationCts?.Token ?? CancellationToken.None)
                .ConfigureAwait(true);
            return devices.Succeeded
                && devices.Devices.Any(device =>
                    device.IsReady
                    && string.Equals(
                        device.Serial,
                        connection.Serial,
                        StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private void NotifyTaskSummaryChanged() => OnPropertyChanged(nameof(TaskCountSummary));

    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GrassTaskItemViewModel.IsEnabled))
        {
            SaveTaskQueueCache();
            NotifyTaskSummaryChanged();
            RaiseQueueCommandStateChanged();
        }
    }

    private void OnTaskSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        SaveTaskQueueCache();

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
        if (!IsConnected && !IsQueueOperationInProgress)
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
        OnPropertyChanged(nameof(CanReorderTasks));
    }

    public bool MoveTask(GrassTaskItemViewModel task, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!CanReorderTasks)
            return false;

        var sourceIndex = Tasks.IndexOf(task);
        if (sourceIndex < 0 || Tasks.Count < 2)
            return false;

        targetIndex = Math.Clamp(targetIndex, 0, Tasks.Count - 1);
        if (sourceIndex == targetIndex)
            return false;

        Tasks.Move(sourceIndex, targetIndex);
        SaveTaskQueueCache();
        RaiseQueueCommandStateChanged();
        return true;
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
