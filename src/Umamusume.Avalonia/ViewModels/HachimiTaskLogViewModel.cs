using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Services;

namespace Umamusume.Avalonia.ViewModels;

public sealed partial class HachimiTaskLogEntry : ObservableObject
{
    public string Step { get; }
    public string Message { get; }
    public LogEntryKind Kind { get; }
    public DateTime Timestamp { get; }

    public HachimiTaskLogEntry(string step, string message, LogEntryKind kind, DateTime timestamp)
    {
        Step = step;
        Message = message;
        Kind = kind;
        Timestamp = timestamp;
    }
}

public sealed partial class HachimiTaskLogGroupViewModel : ObservableObject
{
    private readonly ILocalizationService? _localizationService;
    public string TaskId { get; }

    [ObservableProperty]
    private string _taskName;

    [ObservableProperty]
    private string _displayStatus;

    [ObservableProperty]
    private bool _isWaiting = true;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isCompleted;

    [ObservableProperty]
    private bool _isFailed;

    public ObservableCollection<HachimiTaskLogEntry> Entries { get; } = [];

    public HachimiTaskLogGroupViewModel(string taskId, string taskName, ILocalizationService? localizationService = null)
    {
        _localizationService = localizationService;
        TaskId = taskId;
        _taskName = taskName;
        var isEn = localizationService?.CurrentLanguage?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true;
        _displayStatus = isEn ? "Waiting" : "等待中";
    }

    public void SetStatus(string status, bool isWaiting, bool isRunning, bool isCompleted, bool isFailed)
    {
        DisplayStatus = status;
        IsWaiting = isWaiting;
        IsRunning = isRunning;
        IsCompleted = isCompleted;
        IsFailed = isFailed;
    }

    public void AddEntry(string step, string message, LogEntryKind kind)
    {
        Entries.Add(new HachimiTaskLogEntry(step, message, kind, DateTime.Now));
    }
}

public sealed partial class HachimiTaskLogViewModel : ObservableObject
{
    private readonly ILocalizationService? _localizationService;
    private readonly DispatcherTimer _durationTimer;
    private DateTime _runStartedAt;
    private TimeSpan _finalRunDuration;
    private bool _isTiming;

    private bool IsEnglish => _localizationService?.CurrentLanguage?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true;

    [ObservableProperty]
    private string _runStatus;

    [ObservableProperty]
    private string _totalRunDurationText = "00:00:00";

    public ObservableCollection<HachimiTaskLogGroupViewModel> Tasks { get; } = [];

    public ObservableCollection<HachimiTaskLogEntry> QueueEntries { get; } = [];

    public bool HasContent => Tasks.Count > 0 || QueueEntries.Count > 0;
    public bool IsEmpty => !HasContent;

    public HachimiTaskLogViewModel(ILocalizationService? localizationService = null)
    {
        _localizationService = localizationService;
        _runStatus = IsEnglish ? "Ready" : "就绪";
        if (_localizationService != null)
        {
            _localizationService.LanguageChanged += () => Dispatcher.UIThread.Post(() =>
            {
                if (!_isTiming)
                    RunStatus = IsEnglish ? "Ready" : "就绪";
            });
        }
        _durationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _durationTimer.Tick += OnDurationTimerTick;
    }

    private void OnDurationTimerTick(object? sender, EventArgs e)
    {
        if (!_isTiming)
            return;
        var elapsed = DateTime.Now - _runStartedAt;
        TotalRunDurationText = FormatDuration(elapsed);
    }

    public void BeginRun(IReadOnlyList<TaskQueueItemViewModel> tasks)
    {
        _durationTimer.Stop();
        _runStartedAt = DateTime.Now;
        _finalRunDuration = TimeSpan.Zero;
        _isTiming = true;
        RunStatus = IsEnglish ? "Running" : "运行中";
        TotalRunDurationText = "00:00:00";

        Tasks.Clear();
        QueueEntries.Clear();

        foreach (var task in tasks)
        {
            Tasks.Add(new HachimiTaskLogGroupViewModel(task.TaskId, task.Name, _localizationService));
        }

        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(IsEmpty));
        _durationTimer.Start();
    }

    public void OnQueueEvent(UmaTaskQueueEvent update, IReadOnlyList<TaskQueueItemViewModel> runningTasks)
    {
        var group = update.TaskIndex >= 0 && update.TaskIndex < Tasks.Count
            ? Tasks[update.TaskIndex]
            : null;

        var stepName = update.TaskName ?? update.TaskId;
        var msg = update.Message ?? string.Empty;

        switch (update.Kind)
        {
            case UmaTaskQueueEventKind.Started:
                if (group is not null)
                {
                    group.SetStatus(IsEnglish ? "Running" : "运行中", isWaiting: false, isRunning: true, isCompleted: false, isFailed: false);
                    group.AddEntry(stepName, msg.Length > 0 ? msg : (IsEnglish ? "Task started" : "任务开始运行"), LogEntryKind.Normal);
                }
                break;

            case UmaTaskQueueEventKind.Progress:
                if (group is not null)
                {
                    var progress = update.TotalUnits is > 0 && update.CompletedUnits is { } completed
                        ? $" ({completed}/{update.TotalUnits})"
                        : string.Empty;
                    group.SetStatus(IsEnglish ? $"Running{progress}" : $"运行中{progress}", isWaiting: false, isRunning: true, isCompleted: false, isFailed: false);
                    if (msg.Length > 0)
                        group.AddEntry(stepName, msg, LogEntryKind.Normal);
                }
                break;

            case UmaTaskQueueEventKind.Completed:
                if (group is not null)
                {
                    group.SetStatus(IsEnglish ? "Completed" : "已完成", isWaiting: false, isRunning: false, isCompleted: true, isFailed: false);
                    group.AddEntry(stepName, msg.Length > 0 ? msg : (IsEnglish ? "Task completed" : "任务顺利完成"), LogEntryKind.Normal);
                }
                break;

            case UmaTaskQueueEventKind.Failed:
                if (group is not null)
                {
                    group.SetStatus(IsEnglish ? "Failed" : "失败", isWaiting: false, isRunning: false, isCompleted: false, isFailed: true);
                    group.AddEntry(stepName, msg.Length > 0 ? msg : (IsEnglish ? "Task failed" : "任务执行失败"), LogEntryKind.Error);
                }
                AddQueueEntry(IsEnglish ? "Task Failed" : "任务失败", IsEnglish ? $"{stepName}: {msg}" : $"{stepName}：{msg}", LogEntryKind.Error);
                break;

            case UmaTaskQueueEventKind.Canceled:
                if (group is not null)
                {
                    group.SetStatus(IsEnglish ? "Canceled" : "已取消", isWaiting: false, isRunning: false, isCompleted: false, isFailed: false);
                    group.AddEntry(stepName, IsEnglish ? "Task canceled" : "任务已取消", LogEntryKind.Warning);
                }
                break;

            case UmaTaskQueueEventKind.Skipped:
                if (group is not null)
                {
                    group.SetStatus(IsEnglish ? "Skipped" : "已跳过", isWaiting: false, isRunning: false, isCompleted: false, isFailed: false);
                    group.AddEntry(stepName, msg.Length > 0 ? msg : (IsEnglish ? "Task skipped" : "任务已跳过"), LogEntryKind.Warning);
                }
                break;
        }

        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void EndRun(UmaTaskQueueRunResult result)
    {
        _durationTimer.Stop();
        if (_isTiming)
        {
            _finalRunDuration = DateTime.Now - _runStartedAt;
            _isTiming = false;
            TotalRunDurationText = FormatDuration(_finalRunDuration);
        }

        RunStatus = result.Canceled
            ? (IsEnglish ? "Canceled" : "已取消")
            : result.Succeeded
                ? (IsEnglish ? "Completed" : "已完成")
                : (IsEnglish ? "Finished" : "已结束");

        var summary = result.Canceled
            ? (IsEnglish ? "Task queue canceled." : "任务队列已取消。")
            : result.Succeeded
                ? (IsEnglish ? $"Queue completed, {result.SucceededTasks} task(s) succeeded." : $"队列执行完成，共成功 {result.SucceededTasks} 项任务。")
                : (IsEnglish
                    ? $"Queue finished: {result.SucceededTasks} succeeded, {result.FailedTasks} failed, {result.SkippedTasks} skipped."
                    : $"队列执行结束：成功 {result.SucceededTasks} 项，失败 {result.FailedTasks} 项，跳过 {result.SkippedTasks} 项。");

        var header = result.Succeeded
            ? (IsEnglish ? "Queue Completed" : "队列完成")
            : result.Canceled
                ? (IsEnglish ? "Queue Canceled" : "队列取消")
                : (IsEnglish ? "Queue Finished" : "队列结束");

        AddQueueEntry(
            header,
            summary,
            result.Succeeded ? LogEntryKind.Normal : result.Canceled ? LogEntryKind.Warning : LogEntryKind.Error);

        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void AddQueueEntry(string step, string message, LogEntryKind kind)
    {
        QueueEntries.Add(new HachimiTaskLogEntry(step, message, kind, DateTime.Now));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void Clear()
    {
        _durationTimer.Stop();
        _isTiming = false;
        TotalRunDurationText = "00:00:00";
        RunStatus = IsEnglish ? "Ready" : "就绪";
        Tasks.Clear();
        QueueEntries.Clear();
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static string FormatDuration(TimeSpan duration) =>
        string.Create(CultureInfo.InvariantCulture, $"{(long)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}");
}
