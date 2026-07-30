using System.Collections.ObjectModel;
using System.Text.Json;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.ViewModels;

/// <summary>
/// Displays a timestamped list of callback events received from Core.
/// Binds to <see cref="IUmaService.ConnectionEventReceived"/> and converts
/// each event into a <see cref="LogEntry"/>.
/// Color-coded: Info (gray), Success (pink), Failure (red).
/// </summary>
public sealed class LogViewModel : IDisposable
{
    private readonly IUmaService _umaService;
    private readonly ObservableCollection<LogEntry> _entries = [];
    private bool _disposed;

    /// <summary>
    /// Creates the ViewModel and subscribes to <see cref="IUmaService.ConnectionEventReceived"/>.
    /// </summary>
    /// <param name="umaService">The Core bridge service that provides connection events.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="umaService"/> is null.</exception>
    public LogViewModel(IUmaService umaService)
    {
        ArgumentNullException.ThrowIfNull(umaService);
        _umaService = umaService;
        _umaService.ConnectionEventReceived += OnConnectionEvent;
    }

    /// <summary>
    /// The observable collection of log entries displayed in the view.
    /// Capped at 500 entries; the oldest entry is removed when the cap is exceeded.
    /// </summary>
    public ObservableCollection<LogEntry> Entries => _entries;

    /// <summary>
    /// Adds a GUI-owned event such as Hachimi game launch feedback to the
    /// shared activity log. Core callback handling and the local path use the
    /// same capped collection.
    /// </summary>
    public void AddLocal(string type, string details, LogEntryKind kind = LogEntryKind.Info)
    {
        if (_disposed)
            return;

        _entries.Add(new LogEntry(DateTimeOffset.UtcNow, type, details, kind));
        if (_entries.Count > 500)
            _entries.RemoveAt(0);
    }

    /// <summary>
    /// Disposes the ViewModel and unsubscribes from the service.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _umaService.ConnectionEventReceived -= OnConnectionEvent;
    }

    // ---------------------------------------------------------------
    // Event handler
    // ---------------------------------------------------------------

    private void OnConnectionEvent(ConnectionEvent connectionEvent)
    {
        if (_disposed)
            return;

        var entry = MapToLogEntry(connectionEvent);

        // Thread safety: ObservableCollection must be modified on the
        // UI thread. The WPF dispatcher guarantees that ConnectionEventReceived
        // is raised on the UI thread (via WpfEventDispatcher).
        _entries.Add(entry);

        // Cap at 500 entries — drop the oldest
        if (_entries.Count > 500)
        {
            _entries.RemoveAt(0);
        }
    }

    // ---------------------------------------------------------------
    // Event-to-entry mapping
    // ---------------------------------------------------------------

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
        // Strip "Event" suffix for user-friendly log display (e.g. "ConnectionStarted")
        if (typeName.EndsWith("Event", StringComparison.Ordinal))
            typeName = typeName[..^5];
        return new LogEntry(timestamp, typeName, json, kind);
    }
}
