using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.ViewModels;

/// <summary>
/// Owns the Hachimi workspace log for the current queue run. It is not a
/// wrapper around the legacy global log; pipeline code writes semantic events
/// directly through <see cref="IHachimiTaskLogSink"/> instances.
/// </summary>
public sealed class HachimiTaskLogViewModel : INotifyPropertyChanged
{
    private readonly Dictionary<string, HachimiTaskLogGroupViewModel> _groups =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    private DispatcherTimer? _durationTimer;
    private long _runStartedAt;
    private TimeSpan _finalRunDuration;
    private bool _isTiming;
    private string _runStatus = string.Empty;
    private string _pendingStatus = "Pending";
    private string _runningStatus = "Running";
    private string _completedStatus = "Completed";
    private string _failedStatus = "Failed";
    private string _canceledStatus = "Canceled";
    private string _skippedStatus = "Skipped";

    public HachimiTaskLogViewModel() : this(TimeProvider.System)
    {
    }

    internal HachimiTaskLogViewModel(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<HachimiTaskLogGroupViewModel> Tasks { get; } = [];

    public ObservableCollection<HachimiTaskLogEntry> QueueEntries { get; } = [];

    public bool HasContent => Tasks.Count > 0 || QueueEntries.Count > 0;

    public bool IsEmpty => !HasContent;

    public TimeSpan TotalRunDuration => _isTiming
        ? _timeProvider.GetElapsedTime(_runStartedAt)
        : _finalRunDuration;

    public string TotalRunDurationText
    {
        get
        {
            var duration = TotalRunDuration;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{(long)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}");
        }
    }

    public string RunStatus
    {
        get => _runStatus;
        private set
        {
            if (_runStatus == value)
                return;
            _runStatus = value;
            OnPropertyChanged();
        }
    }

    public void BeginRun(
        IEnumerable<(string Id, string Name)> tasks,
        string pendingStatus,
        string runningStatus,
        string completedStatus,
        string failedStatus,
        string canceledStatus,
        string skippedStatus,
        string runStatus)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        _durationTimer?.Stop();
        _runStartedAt = _timeProvider.GetTimestamp();
        _finalRunDuration = TimeSpan.Zero;
        _isTiming = true;
        if (_durationTimer is null && Application.Current?.Dispatcher is { } dispatcher)
        {
            _durationTimer = new DispatcherTimer(
                TimeSpan.FromSeconds(1),
                DispatcherPriority.Background,
                (_, _) => NotifyRunDurationChanged(),
                dispatcher);
        }
        _durationTimer?.Start();
        NotifyRunDurationChanged();

        _pendingStatus = pendingStatus;
        _runningStatus = runningStatus;
        _completedStatus = completedStatus;
        _failedStatus = failedStatus;
        _canceledStatus = canceledStatus;
        _skippedStatus = skippedStatus;

        Tasks.Clear();
        QueueEntries.Clear();
        _groups.Clear();

        var order = 1;
        foreach (var (id, name) in tasks)
        {
            var group = new HachimiTaskLogGroupViewModel(order++, id, name, _pendingStatus);
            Tasks.Add(group);
            _groups[id] = group;
        }

        RunStatus = runStatus;
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public IHachimiTaskLogSink ForTask(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return new GroupSink(this, taskId);
    }

    public void SetRunStatus(string status) => RunStatus = status;

    public void EndRun()
    {
        if (!_isTiming)
            return;

        _finalRunDuration = TotalRunDuration;
        _isTiming = false;
        _durationTimer?.Stop();
        NotifyRunDurationChanged();
    }

    private void NotifyRunDurationChanged()
    {
        OnPropertyChanged(nameof(TotalRunDuration));
        OnPropertyChanged(nameof(TotalRunDurationText));
    }

    public void AddQueueStep(
        string step,
        string message,
        HachimiTaskLogEventKind kind = HachimiTaskLogEventKind.Info)
    {
        AddToCollection(
            QueueEntries,
            new HachimiTaskLogEntry(DateTimeOffset.Now, step, message, kind));
    }

    public void SetTaskStatus(string taskId, HachimiTaskLogStatus status)
    {
        if (!_groups.TryGetValue(taskId, out var group))
            return;

        Dispatch(() =>
        {
            group.Status = status;
            group.StatusText = StatusTextFor(status);
        });
    }

    public void SetTaskProgress(string taskId, GrassTaskExecutionProgress progress)
    {
        if (!_groups.TryGetValue(taskId, out var group))
            return;

        Dispatch(() => group.SetExecutionProgress(progress));
    }

    public void AddTaskStep(
        string taskId,
        string step,
        string message,
        HachimiTaskLogEventKind kind = HachimiTaskLogEventKind.Info)
    {
        if (!_groups.TryGetValue(taskId, out var group))
            return;

        var entry = new HachimiTaskLogEntry(DateTimeOffset.Now, step, message, kind);
        Dispatch(() => group.AddEntry(entry));
    }

    public void RefreshStatusText(
        string pendingStatus,
        string runningStatus,
        string completedStatus,
        string failedStatus,
        string canceledStatus,
        string skippedStatus)
    {
        _pendingStatus = pendingStatus;
        _runningStatus = runningStatus;
        _completedStatus = completedStatus;
        _failedStatus = failedStatus;
        _canceledStatus = canceledStatus;

        _skippedStatus = skippedStatus;
        foreach (var group in Tasks)
            group.StatusText = StatusTextFor(group.Status);
    }

    private string StatusTextFor(HachimiTaskLogStatus status) => status switch
    {
        HachimiTaskLogStatus.Pending => _pendingStatus,
        HachimiTaskLogStatus.Running => _runningStatus,
        HachimiTaskLogStatus.Completed => _completedStatus,
        HachimiTaskLogStatus.Failed => _failedStatus,
        HachimiTaskLogStatus.Canceled => _canceledStatus,
        HachimiTaskLogStatus.Skipped => _skippedStatus,
        _ => _pendingStatus,
    };

    private void AddToCollection(
        ObservableCollection<HachimiTaskLogEntry> collection,
        HachimiTaskLogEntry entry)
    {
        Dispatch(() =>
        {
            collection.Add(entry);
            OnPropertyChanged(nameof(HasContent));
            OnPropertyChanged(nameof(IsEmpty));
        });
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.InvokeAsync(action);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class GroupSink : IHachimiTaskLogSink
    {
        private readonly HachimiTaskLogViewModel _owner;
        private readonly string _taskId;

        public GroupSink(HachimiTaskLogViewModel owner, string taskId)
        {
            _owner = owner;
            _taskId = taskId;
        }

        public void Add(
            string step,
            string message,
            HachimiTaskLogEventKind kind = HachimiTaskLogEventKind.Info) =>
            _owner.AddTaskStep(_taskId, step, message, kind);
    }
}
