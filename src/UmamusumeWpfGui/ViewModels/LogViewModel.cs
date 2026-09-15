using System.Collections.ObjectModel;
using System.Windows;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.ViewModels;

public sealed class LogViewModel : IDisposable
{
    private readonly ObservableCollection<LogEntry> _entries = [];
    private bool _disposed;

    public ObservableCollection<LogEntry> Entries => _entries;

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
                dispatcher.Invoke(() => Append(entry));
            }
            catch (InvalidOperationException)
            {
                // The application can close while a task is finishing.
            }

            return;
        }

        Append(entry);
    }

    public void Clear()
    {
        if (!_disposed)
            _entries.Clear();
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void Append(LogEntry entry)
    {
        if (_disposed)
            return;

        _entries.Add(entry);
        if (_entries.Count > 500)
            _entries.RemoveAt(0);
    }
}
