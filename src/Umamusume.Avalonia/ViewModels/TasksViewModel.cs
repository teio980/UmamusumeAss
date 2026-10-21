using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Platform;
using Umamusume.AppCore.Services;
using Umamusume.Avalonia.Services;

namespace Umamusume.Avalonia.ViewModels;

public sealed partial class TasksViewModel : ViewModelBase, IDisposable
{
    private readonly IUmaAutomationService _automationService;
    private readonly ILogService _logService;
    private readonly IConnectionStateService _connectionStateService;
    private readonly ISettingsService _settingsService;
    private readonly ILocalizationService _localizationService;
    private CancellationTokenSource? _queueCancellation;
    private List<TaskQueueItemViewModel> _runningTasks = [];
    private readonly EventHandler _connectionStateChangedHandler;
    private readonly Action<bool> _automationBusyChangedHandler;
    private readonly Action _languageChangedHandler;
    private bool _disposed;

    [ObservableProperty]
    private TaskQueueItemViewModel? _selectedTask;

    [ObservableProperty]
    private int _selectedTaskTypeIndex;

    [ObservableProperty]
    private TaskTypeOption? _selectedTaskType;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isAutomationBusy;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private bool _isNotificationOpen;

    [ObservableProperty]
    private string _notificationTitle = string.Empty;

    [ObservableProperty]
    private string _notificationMessage = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _notificationSeverity = InfoBarSeverity.Informational;

    public ObservableCollection<TaskQueueItemViewModel> Tasks { get; } = new();

    public ObservableCollection<string> ExecutionSteps { get; } = new();

    public HachimiTaskLogViewModel HachimiTaskLog { get; }

    [ObservableProperty]
    private bool _isTaskSettingsOpen;

    public ObservableCollection<TaskTypeOption> AvailableTaskTypes { get; } = new();

    public ShopTaskSettingsViewModel ShopSettings { get; }

    public bool HasTasks => Tasks.Count > 0;

    private bool IsEnglish => _localizationService.CurrentLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase);
    public string QueueSummary => IsEnglish
        ? $"{Tasks.Count} tasks · {Tasks.Count(task => task.IsEnabled)} enabled"
        : $"{Tasks.Count} 项任务 · {Tasks.Count(task => task.IsEnabled)} 项启用";

    public bool IsShopSelected => SelectedTask?.TaskId == "shop-purchase";

    public bool IsNotShopSelected => !IsShopSelected;

    public bool CanStartQueue => !IsRunning && !IsAutomationBusy
        && Tasks.Any(task => task.IsEnabled)
        && Tasks.Where(task => task.IsEnabled).All(task => task.SettingsEditor.IsValid);

    public bool CanStopQueue => _queueCancellation is not null;

    public string QueueValidationMessage
    {
        get
        {
            var invalid = Tasks.FirstOrDefault(task => task.IsEnabled && !task.SettingsEditor.IsValid);
            if (invalid is not null)
                return $"{invalid.Name}：{invalid.SettingsEditor.ValidationMessage}";
            if (IsAutomationBusy && !IsRunning)
                return IsEnglish ? "Another operation is running. Wait for it to stop before starting the queue." : "另一个自动化操作正在运行，完成或停止后才能启动任务队列。";
            return Tasks.Any(task => task.IsEnabled) ? string.Empty : IsEnglish ? "Enable at least one task." : "至少启用一个任务后才能启动队列。";
        }
    }

    public bool CanMoveSelectedTaskUp => !IsRunning && SelectedTask is not null && Tasks.IndexOf(SelectedTask) > 0;

    public bool CanMoveSelectedTaskDown => !IsRunning && SelectedTask is not null && Tasks.IndexOf(SelectedTask) < Tasks.Count - 1;

    public TasksViewModel(
        IUmaAutomationService automationService,
        ILogService logService,
        IConnectionStateService connectionStateService,
        ILocalizationService localizationService,
        ISettingsService settingsService)
    {
        _automationService = automationService;
        _logService = logService;
        _connectionStateService = connectionStateService;
        _settingsService = settingsService;
        _localizationService = localizationService;
        foreach (var option in TaskTypeOption.Build(localizationService.CurrentLanguage))
        {
            AvailableTaskTypes.Add(option);
        }
        _selectedTaskTypeIndex = 0;
        _selectedTaskType = AvailableTaskTypes.Count > 0 ? AvailableTaskTypes[0] : null;
        ShopSettings = new ShopTaskSettingsViewModel(settingsService);
        HachimiTaskLog = new HachimiTaskLogViewModel(localizationService);
        _statusText = string.Empty;

        _connectionStateChangedHandler = (_, _) => Dispatcher.UIThread.Post(() =>
        {
            IsConnected = _connectionStateService.State == ConnectionState.Connected;
            RaiseQueueCommandStates();
        });
        _automationBusyChangedHandler = isBusy => Dispatcher.UIThread.Post(() => IsAutomationBusy = isBusy);
        _languageChangedHandler = OnLanguageChanged;

        RestoreQueue();
        _isConnected = connectionStateService.State == ConnectionState.Connected;
        _isAutomationBusy = automationService.IsBusy;
        _connectionStateService.StateChanged += _connectionStateChangedHandler;
        _automationService.BusyChanged += _automationBusyChangedHandler;
        _localizationService.LanguageChanged += _languageChangedHandler;
    }

    [RelayCommand]
    public void OpenTaskSettings(TaskQueueItemViewModel? task = null)
    {
        if (task is not null)
            SelectedTask = task;
        if (SelectedTask is not null)
            IsTaskSettingsOpen = true;
    }

    [RelayCommand]
    public void CloseTaskSettings()
    {
        IsTaskSettingsOpen = false;
    }

    partial void OnSelectedTaskChanged(TaskQueueItemViewModel? oldValue, TaskQueueItemViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;
        if (newValue is not null)
            newValue.IsSelected = true;
        if (newValue is null)
            IsTaskSettingsOpen = false;
        OnPropertyChanged(nameof(IsShopSelected));
        OnPropertyChanged(nameof(IsNotShopSelected));
        SaveQueue();
        RaiseQueueCommandStates();
    }

    partial void OnIsRunningChanged(bool value) => RaiseQueueCommandStates();

    partial void OnIsAutomationBusyChanged(bool value) => RaiseQueueCommandStates();

    partial void OnSelectedTaskTypeIndexChanged(int value)
    {
        if (value >= 0 && value < AvailableTaskTypes.Count)
        {
            SelectedTaskType = AvailableTaskTypes[value];
        }
    }

    [RelayCommand]
    private void AddTask()
    {
        var taskType = SelectedTaskType ?? AvailableTaskTypes.ElementAtOrDefault(SelectedTaskTypeIndex);
        if (taskType is null)
            return;

        try
        {
            var data = UmaTaskCatalog.CreateDefaultTask(taskType.Id, _settingsService.Load());
            var definition = UmaTaskCatalog.Get(data.TaskId);
            var item = new TaskQueueItemViewModel(definition, data.IsEnabled, data.Settings, _localizationService);
            AttachTask(item);
            Tasks.Add(item);
            SelectedTask = item;
            OnPropertyChanged(nameof(HasTasks));
            OnPropertyChanged(nameof(QueueSummary));
            SaveQueue();
            AddLog(IsEnglish ? $"Task added: {item.Name}" : $"已添加任务：{item.Name}");
        }
        catch (Exception exception)
        {
            ShowNotification(IsEnglish ? "Unable to add task" : "无法添加任务", exception.Message, InfoBarSeverity.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopyOrRemoveTask))]
    private void CopyTask()
    {
        if (SelectedTask is null)
            return;
        var index = Tasks.IndexOf(SelectedTask);
        var copy = SelectedTask.Clone();
        AttachTask(copy);
        Tasks.Insert(index + 1, copy);
        SelectedTask = copy;
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(QueueSummary));
        SaveQueue();
        AddLog(IsEnglish ? $"Task copied: {copy.Name}" : $"已复制任务：{copy.Name}");
    }

    [RelayCommand(CanExecute = nameof(CanCopyOrRemoveTask))]
    private void RemoveTask()
    {
        if (SelectedTask is null)
            return;
        var removedIndex = Tasks.IndexOf(SelectedTask);
        DetachTask(SelectedTask);
        Tasks.Remove(SelectedTask);
        SelectedTask = Tasks.ElementAtOrDefault(Math.Max(0, removedIndex - 1));
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(QueueSummary));
        SaveQueue();
        AddLog(IsEnglish ? "Selected task removed from queue." : "已从队列移除选中任务。");
    }

    private bool CanCopyOrRemoveTask() => !IsRunning && SelectedTask is not null;

    [RelayCommand(CanExecute = nameof(CanMoveTaskUp))]
    private void MoveTaskUp()
    {
        if (SelectedTask is null)
            return;
        var index = Tasks.IndexOf(SelectedTask);
        if (index > 0)
        {
            Tasks.Move(index, index - 1);
            SaveQueue();
            RaiseQueueCommandStates();
        }
    }

    [RelayCommand(CanExecute = nameof(CanMoveTaskDown))]
    private void MoveTaskDown()
    {
        if (SelectedTask is null)
            return;
        var index = Tasks.IndexOf(SelectedTask);
        if (index >= 0 && index < Tasks.Count - 1)
        {
            Tasks.Move(index, index + 1);
            SaveQueue();
            RaiseQueueCommandStates();
        }
    }

    [RelayCommand]
    private void SelectAllTasks() => SetEnabledForAll(true);

    [RelayCommand]
    private void InvertTaskSelection()
    {
        foreach (var task in Tasks)
            task.IsEnabled = !task.IsEnabled;
        OnPropertyChanged(nameof(QueueSummary));
        SaveQueue();
        RaiseQueueCommandStates();
    }

    private void SetEnabledForAll(bool enabled)
    {
        foreach (var task in Tasks)
            task.IsEnabled = enabled;
        OnPropertyChanged(nameof(QueueSummary));
        SaveQueue();
        RaiseQueueCommandStates();
    }

    [RelayCommand(CanExecute = nameof(CanStartQueueCommand))]
    private async Task StartQueueAsync()
    {
        var enabled = Tasks.Where(task => task.IsEnabled).ToList();
        if (!CanStartQueue)
        {
            ShowNotification(IsEnglish ? "Unable to start task queue" : "无法启动任务队列", QueueValidationMessage, InfoBarSeverity.Warning);
            return;
        }

        foreach (var task in Tasks)
            task.SetStatus(task.IsEnabled ? TaskItemStatus.Queued : TaskItemStatus.Disabled);
        _runningTasks = enabled;
        _queueCancellation?.Dispose();
        _queueCancellation = new CancellationTokenSource();
        IsRunning = true;
        ErrorText = string.Empty;
        ExecutionSteps.Clear();
        HachimiTaskLog.BeginRun(enabled);
        StatusText = IsEnglish ? $"Starting {enabled.Count} task(s)..." : $"正在提交 {enabled.Count} 项任务到 Native UmaCore…";
        AddLog(StatusText);
        _logService.Log($"Starting Hachimi task queue ({enabled.Count} tasks).", LogEntryKind.Normal);
        var requests = enabled.Select(task => new UmaTaskRequest(task.TaskId, task.Settings)).ToArray();

        try
        {
            if (!_automationService.IsConnected)
            {
                StatusText = IsEnglish ? "Connecting to the configured emulator..." : "正在连接已配置的模拟器…";
                AddLog(StatusText);

                var connectionError = await ConnectForQueueAsync(_queueCancellation.Token);
                if (connectionError is not null)
                {
                    for (var index = 0; index < enabled.Count; index++)
                    {
                        enabled[index].SetStatus(TaskItemStatus.Skipped);
                        if (index < HachimiTaskLog.Tasks.Count)
                        {
                            var logTask = HachimiTaskLog.Tasks[index];
                            logTask.SetStatus(IsEnglish ? "Skipped" : "已跳过", false, false, false, false);
                            logTask.AddEntry(
                                IsEnglish ? "Connection" : "连接",
                                connectionError,
                                LogEntryKind.Warning);
                        }
                    }

                    var failedRun = new UmaTaskQueueRunResult(
                        Succeeded: false,
                        Canceled: false,
                        SucceededTasks: 0,
                        FailedTasks: 0,
                        SkippedTasks: enabled.Count,
                        Message: connectionError);
                    HachimiTaskLog.AddQueueEntry(
                        IsEnglish ? "Connection Failed" : "连接失败",
                        connectionError,
                        LogEntryKind.Error);
                    HachimiTaskLog.EndRun(failedRun);
                    StatusText = IsEnglish ? "Queue was not run because the device could not connect." : "设备连接失败，队列未执行。";
                    ErrorText = connectionError;
                    AddLog(StatusText);
                    ShowNotification(
                        IsEnglish ? "Connection failed" : "连接失败",
                        connectionError,
                        InfoBarSeverity.Error);
                    return;
                }

                IsConnected = _automationService.IsConnected;
                RaiseQueueCommandStates();
            }

            var result = await _automationService.RunTaskQueueAsync(
                requests,
                OnQueueEvent,
                _queueCancellation.Token);

            HachimiTaskLog.EndRun(result);
            StatusText = result.Canceled
                ? (IsEnglish ? "Task queue canceled." : "任务队列已取消。")
                : result.Succeeded
                    ? (IsEnglish ? $"Queue completed: {result.SucceededTasks} succeeded." : $"队列完成：成功 {result.SucceededTasks} 项。")
                    : (IsEnglish ? $"Queue finished: {result.SucceededTasks} succeeded, {result.FailedTasks} failed, {result.SkippedTasks} skipped." : $"队列结束：成功 {result.SucceededTasks} 项，失败 {result.FailedTasks} 项，跳过 {result.SkippedTasks} 项。");
            if (!result.Succeeded && !result.Canceled)
                ErrorText = result.Message;
            AddLog(StatusText);
            ShowNotification(
                result.Succeeded
                    ? (IsEnglish ? "Queue Completed" : "任务队列完成")
                    : result.Canceled
                        ? (IsEnglish ? "Queue Canceled" : "任务队列已取消")
                        : (IsEnglish ? "Queue Finished" : "任务队列结束"),
                result.Message,
                result.Succeeded ? InfoBarSeverity.Success : result.Canceled ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
        }
        catch (OperationCanceledException)
        {
            StatusText = IsEnglish ? "Task queue canceled." : "任务队列已取消。";
            HachimiTaskLog.EndRun(new UmaTaskQueueRunResult(
                Succeeded: false,
                Canceled: true,
                SucceededTasks: 0,
                FailedTasks: 0,
                SkippedTasks: _runningTasks.Count,
                Message: StatusText));
            AddLog(StatusText);
            ShowNotification(IsEnglish ? "Queue Canceled" : "任务队列已取消", IsEnglish ? "Stop request sent to Native UmaCore." : "已向 Native UmaCore 发送停止请求。", InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            StatusText = IsEnglish ? $"Queue error: {exception.Message}" : $"任务队列异常：{exception.Message}";
            ErrorText = exception.ToString();
            AddLog(StatusText);
            _logService.Log($"Hachimi task queue failed: {exception}", LogEntryKind.Error);
            ShowNotification(IsEnglish ? "Queue Error" : "任务队列异常", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            foreach (var task in _runningTasks)
            {
                if (task.Status == TaskItemStatus.Queued || task.Status == TaskItemStatus.Running)
                    task.SetStatus(TaskItemStatus.Idle);
            }
            _runningTasks = [];
            IsRunning = false;
            _queueCancellation?.Dispose();
            _queueCancellation = null;
            RaiseQueueCommandStates();
        }
    }

    private void OnQueueEvent(UmaTaskQueueEvent update)
    {
        var runningTasks = _runningTasks;
        Dispatcher.UIThread.Post(() =>
        {
            var task = update.TaskIndex >= 0 && update.TaskIndex < runningTasks.Count
                ? runningTasks[update.TaskIndex]
                : null;
            var taskName = task?.Name ?? TaskLabels.GetName(update.TaskId, update.TaskName ?? update.TaskId, _localizationService.CurrentLanguage);
            var taskProgress = update.TotalUnits is > 0 && update.CompletedUnits is { } completed
                ? $" · {Math.Min(completed, update.TotalUnits.Value)}/{update.TotalUnits.Value}"
                : string.Empty;

            HachimiTaskLog.OnQueueEvent(update, runningTasks);

            switch (update.Kind)
            {
                case UmaTaskQueueEventKind.Started:
                    task?.SetStatus(TaskItemStatus.Running);
                    StatusText = IsEnglish ? $"Running: {taskName}" : $"正在执行：{taskName}";
                    AddLog(StatusText);
                    break;
                case UmaTaskQueueEventKind.Progress:
                    if (task is not null)
                    {
                        var suffix = string.IsNullOrEmpty(taskProgress) ? null : taskProgress.TrimStart(' ', '·');
                        task.SetStatus(TaskItemStatus.Running, suffix);
                    }
                    StatusText = update.Message is { Length: > 0 } message
                        ? $"{taskName}：{message}"
                        : (IsEnglish ? $"Running: {taskName}{taskProgress}" : $"正在执行：{taskName}{taskProgress}");
                    break;
                case UmaTaskQueueEventKind.Completed:
                    task?.SetStatus(TaskItemStatus.Completed);
                    StatusText = update.Message is { Length: > 0 } completedMessage
                        ? $"{taskName}：{completedMessage}"
                        : (IsEnglish ? $"Completed: {taskName}" : $"已完成：{taskName}");
                    AddLog(StatusText);
                    break;
                case UmaTaskQueueEventKind.Failed:
                    task?.SetStatus(TaskItemStatus.Failed);
                    ErrorText = update.Message ?? (IsEnglish ? "Native UmaCore did not provide a failure reason." : "Native UmaCore 未提供失败原因。");
                    StatusText = IsEnglish ? $"Task failed: {taskName}" : $"任务失败：{taskName}";
                    AddLog($"{StatusText} — {ErrorText}");
                    _logService.Log($"Hachimi task failed ({update.TaskId}): {update.Message}", LogEntryKind.Error);
                    break;
                case UmaTaskQueueEventKind.Canceled:
                    task?.SetStatus(TaskItemStatus.Canceled);
                    StatusText = IsEnglish ? $"Canceled: {taskName}" : $"已取消：{taskName}";
                    AddLog(StatusText);
                    break;
                case UmaTaskQueueEventKind.Skipped:
                    task?.SetStatus(TaskItemStatus.Skipped);
                    StatusText = IsEnglish ? $"Skipped: {taskName}" : $"已跳过：{taskName}";
                    AddLog(update.Message is { Length: > 0 } skippedMessage
                        ? $"{StatusText} — {skippedMessage}"
                        : StatusText);
                    break;
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanStopQueueCommand))]
    private void StopQueue()
    {
        if (_queueCancellation is null)
            return;
        _queueCancellation?.Cancel();
        StatusText = IsEnglish ? "Stopping current task..." : "正在停止当前任务…";
        AddLog(StatusText);
        _logService.Log("Hachimi task queue cancellation requested.", LogEntryKind.Warning);
    }

    [RelayCommand]
    public void ClearHistory()
    {
        ExecutionSteps.Clear();
        HachimiTaskLog.Clear();
    }

    [RelayCommand]
    private void DismissNotification() => IsNotificationOpen = false;

    private bool CanStartQueueCommand() => CanStartQueue;

    private bool CanStopQueueCommand() => CanStopQueue;

    private bool CanMoveTaskUp() => CanMoveSelectedTaskUp;

    private bool CanMoveTaskDown() => CanMoveSelectedTaskDown;

    private async Task<string?> ConnectForQueueAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsService.Load();
        var adbPath = PlatformPaths.FindAdbExecutable(settings.AdbPath);
        if (string.IsNullOrWhiteSpace(settings.ConnectAddress))
            return IsEnglish ? "A device address is not configured in Settings." : "设置中没有配置设备地址。";

        if (settings.AutoStartEmulator)
        {
            try
            {
                DesktopActions.StartEmulator(settings.EmulatorExecutablePath);
            }
            catch (Exception exception)
            {
                return IsEnglish
                    ? $"Could not start the configured emulator: {exception.Message}"
                    : $"无法启动设置中的模拟器：{exception.Message}";
            }

            var wait = Math.Clamp(
                settings.AutoStartEmulatorWaitSeconds,
                0,
                ConnectionSettings.MaxAutoStartEmulatorWaitSeconds);
            if (wait > 0)
            {
                StatusText = IsEnglish
                    ? $"Waiting {wait} seconds for the emulator to start..."
                    : $"等待模拟器启动（{wait} 秒）…";
                AddLog(StatusText);
                await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken);
            }
        }

        StatusText = IsEnglish ? "Connecting to the device..." : "正在连接设备…";
        AddLog(StatusText);
        var connected = await _automationService.ConnectAsync(
            adbPath,
            settings.ConnectAddress.Trim(),
            settings.ConnectConfig,
            cancellationToken);

        return connected && _automationService.IsConnected
            ? null
            : string.IsNullOrWhiteSpace(_automationService.CurrentStatus)
                ? (IsEnglish ? "The configured device could not be connected." : "无法连接已配置的设备。")
                : _automationService.CurrentStatus;
    }

    private void RestoreQueue()
    {
        var settings = _settingsService.Load();
        var definitions = UmaTaskCatalog.All.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var saved in settings.Hachimi.TaskQueue)
        {
            if (!definitions.TryGetValue(saved.TaskId, out var definition))
                continue;
            var task = new TaskQueueItemViewModel(definition, saved.IsEnabled, saved.Settings, _localizationService);
            AttachTask(task);
            Tasks.Add(task);
        }

        var savedSelection = settings.Hachimi.SelectedTaskIndex is { } savedIndex
            && savedIndex >= 0 && savedIndex < Tasks.Count
                ? Tasks[savedIndex]
                : Tasks.FirstOrDefault(task => task.TaskId == settings.Hachimi.SelectedTaskId);
        SelectedTask = savedSelection ?? Tasks.FirstOrDefault();
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(QueueSummary));
        RaiseQueueCommandStates();
    }

    private void AttachTask(TaskQueueItemViewModel task)
    {
        task.SettingsChanged += OnTaskSettingsChanged;
        task.PropertyChanged += OnTaskPropertyChanged;
    }

    private void DetachTask(TaskQueueItemViewModel task)
    {
        task.SettingsChanged -= OnTaskSettingsChanged;
        task.PropertyChanged -= OnTaskPropertyChanged;
    }

    private void OnTaskSettingsChanged(object? sender, EventArgs args)
    {
        SaveQueue();
        RaiseQueueCommandStates();
    }

    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TaskQueueItemViewModel.IsEnabled))
        {
            OnPropertyChanged(nameof(QueueSummary));
            SaveQueue();
            RaiseQueueCommandStates();
        }
    }

    private void SaveQueue()
    {
        try
        {
            var settings = _settingsService.Load();
            settings.Hachimi.TaskQueue = Tasks.Select(task => new HachimiTaskQueueItem
            {
                TaskId = task.TaskId,
                IsEnabled = task.IsEnabled,
                Settings = task.Settings.DeepClone().AsObject(),
            }).ToList();
            settings.Hachimi.SelectedTaskId = SelectedTask?.TaskId;
            settings.Hachimi.SelectedTaskIndex = SelectedTask is null ? null : Tasks.IndexOf(SelectedTask);
            _settingsService.Save(settings);
        }
        catch (Exception exception)
        {
            _logService.Log($"Could not persist the Hachimi task queue: {exception.Message}", LogEntryKind.Error);
            ShowNotification(IsEnglish ? "Queue save failed" : "队列保存失败", exception.Message, InfoBarSeverity.Error);
        }
    }

    private void AddLog(string text)
    {
        ExecutionSteps.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {text}");
        while (ExecutionSteps.Count > 100)
            ExecutionSteps.RemoveAt(ExecutionSteps.Count - 1);
    }

    private void ShowNotification(string title, string message, InfoBarSeverity severity)
    {
        NotificationTitle = title;
        NotificationMessage = message;
        NotificationSeverity = severity;
        IsNotificationOpen = true;
    }

    private void RaiseQueueCommandStates()
    {
        OnPropertyChanged(nameof(CanStartQueue));
        OnPropertyChanged(nameof(CanStopQueue));
        OnPropertyChanged(nameof(QueueValidationMessage));
        OnPropertyChanged(nameof(CanMoveSelectedTaskUp));
        OnPropertyChanged(nameof(CanMoveSelectedTaskDown));
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(QueueSummary));
        StartQueueCommand.NotifyCanExecuteChanged();
        StopQueueCommand.NotifyCanExecuteChanged();
        CopyTaskCommand.NotifyCanExecuteChanged();
        RemoveTaskCommand.NotifyCanExecuteChanged();
        MoveTaskUpCommand.NotifyCanExecuteChanged();
        MoveTaskDownCommand.NotifyCanExecuteChanged();
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(() =>
    {
        var selectedId = SelectedTaskType?.Id;
        AvailableTaskTypes.Clear();
        foreach (var option in TaskTypeOption.Build(_localizationService.CurrentLanguage))
        {
            AvailableTaskTypes.Add(option);
        }
        var newIndex = -1;
        if (selectedId is not null)
        {
            for (var i = 0; i < AvailableTaskTypes.Count; i++)
            {
                if (AvailableTaskTypes[i].Id == selectedId)
                {
                    newIndex = i;
                    break;
                }
            }
        }
        SelectedTaskTypeIndex = newIndex >= 0 ? newIndex : (AvailableTaskTypes.Count > 0 ? 0 : -1);
        SelectedTaskType = SelectedTaskTypeIndex >= 0 && SelectedTaskTypeIndex < AvailableTaskTypes.Count
            ? AvailableTaskTypes[SelectedTaskTypeIndex]
            : null;
        foreach (var task in Tasks)
            task.RefreshLabels();
        if (!IsRunning)
            StatusText = string.Empty;
        OnPropertyChanged(nameof(QueueSummary));
        OnPropertyChanged(nameof(QueueValidationMessage));
    });

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _connectionStateService.StateChanged -= _connectionStateChangedHandler;
        _automationService.BusyChanged -= _automationBusyChangedHandler;
        _localizationService.LanguageChanged -= _languageChangedHandler;
        foreach (var task in Tasks)
            DetachTask(task);
        if (_queueCancellation is not null)
        {
            _queueCancellation.Cancel();
            if (!IsRunning)
            {
                _queueCancellation.Dispose();
                _queueCancellation = null;
            }
        }
    }
}
