namespace UmamusumeWpfGui.Models;








public sealed record LogEntry(
    DateTimeOffset Timestamp,
    string Type,
    string Details,
    LogEntryKind Kind);
