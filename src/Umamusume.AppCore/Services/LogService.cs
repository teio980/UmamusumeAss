using System;
using System.Collections.Generic;
using Umamusume.AppCore.Models;

namespace Umamusume.AppCore.Services;

public interface ILogService
{
    event Action<LogEntry>? LogReceived;
    IReadOnlyList<LogEntry> Entries { get; }
    void Log(string message, LogEntryKind kind = LogEntryKind.Normal, string? category = null);
    void Clear();
}

public sealed class LogService : ILogService
{
    private readonly List<LogEntry> _entries = [];
    private readonly object _lock = new();
    private const int MaxEntries = 2000;

    public event Action<LogEntry>? LogReceived;

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Log(string message, LogEntryKind kind = LogEntryKind.Normal, string? category = null)
    {
        var entry = new LogEntry(DateTime.Now, kind, message, category);
        lock (_lock)
        {
            if (_entries.Count >= MaxEntries)
            {
                _entries.RemoveRange(0, 100);
            }
            _entries.Add(entry);
        }
        LogReceived?.Invoke(entry);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }
}
