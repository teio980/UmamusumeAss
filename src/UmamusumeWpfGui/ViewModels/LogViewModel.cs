using System.Collections.ObjectModel;
using System.Text.Json;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.ViewModels;







public sealed class LogViewModel : IDisposable
{
    private readonly IUmaService _umaService;
    private readonly ObservableCollection<LogEntry> _entries = [];
    private bool _disposed;






    public LogViewModel(IUmaService umaService)
    {
        ArgumentNullException.ThrowIfNull(umaService);
        _umaService = umaService;
        _umaService.ConnectionEventReceived += OnConnectionEvent;
    }





    public ObservableCollection<LogEntry> Entries => _entries;






    public void AddLocal(string type, string details, LogEntryKind kind = LogEntryKind.Info)
    {
        if (_disposed)
            return;

        _entries.Add(new LogEntry(DateTimeOffset.UtcNow, type, details, kind));
        if (_entries.Count > 500)
            _entries.RemoveAt(0);
    }





    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _umaService.ConnectionEventReceived -= OnConnectionEvent;
    }





    private void OnConnectionEvent(ConnectionEvent connectionEvent)
    {
        if (_disposed)
            return;

        var entry = MapToLogEntry(connectionEvent);




        _entries.Add(entry);


        if (_entries.Count > 500)
        {
            _entries.RemoveAt(0);
        }
    }





    private static LogEntry MapToLogEntry(ConnectionEvent connectionEvent)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(connectionEvent, connectionEvent.GetType());
        var kind = connectionEvent switch
        {
            ConnectionSucceededEvent => LogEntryKind.Success,
            ConnectionFailedEvent => LogEntryKind.Failure,
            _ => LogEntryKind.Info,
        };

        var typeName = connectionEvent.GetType().Name;

        if (typeName.EndsWith("Event", StringComparison.Ordinal))
            typeName = typeName[..^5];
        return new LogEntry(timestamp, typeName, json, kind);
    }
}
