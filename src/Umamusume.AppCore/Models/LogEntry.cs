using System;

namespace Umamusume.AppCore.Models;

public enum LogEntryKind
{
    Normal,
    Success,
    Warning,
    Error,
    System,
}

public sealed record LogEntry(
    DateTime Timestamp,
    LogEntryKind Kind,
    string Message,
    string? Category = null);
