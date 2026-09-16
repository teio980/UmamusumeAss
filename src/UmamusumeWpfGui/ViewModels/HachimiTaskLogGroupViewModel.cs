using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.ViewModels;

public sealed class HachimiTaskLogGroupViewModel : INotifyPropertyChanged
{
    private HachimiTaskLogStatus _status;
    private string _statusText;

    internal HachimiTaskLogGroupViewModel(
        int order,
        string taskId,
        string taskName,
        string pendingStatus)
    {
        Order = order;
        TaskId = taskId;
        TaskName = taskName;
        _status = HachimiTaskLogStatus.Pending;
        _statusText = pendingStatus;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Order { get; }

    public string TaskId { get; }

    public string TaskName { get; }

    public HachimiTaskLogStatus Status
    {
        get => _status;
        internal set
        {
            if (_status == value)
                return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        internal set
        {
            if (_statusText == value)
                return;
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<HachimiTaskLogEntry> Entries { get; } = [];

    public bool HasEntries => Entries.Count > 0;

    public bool IsWaiting => Entries.Count == 0;

    internal void AddEntry(HachimiTaskLogEntry entry)
    {
        Entries.Add(entry);
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(IsWaiting));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
