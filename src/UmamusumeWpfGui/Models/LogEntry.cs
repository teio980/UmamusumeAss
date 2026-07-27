namespace UmamusumeWpfGui.Models;

/// <summary>
/// A timestamped log entry representing a single Core callback event.
/// </summary>
/// <param name="Timestamp">When the event occurred.</param>
/// <param name="Type">The event type string (e.g. "ConnectionStarted").</param>
/// <param name="Details">Human-readable details extracted from the event payload.</param>
/// <param name="Kind">Color category: Info (gray), Success (pink), Failure (red).</param>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    string Type,
    string Details,
    LogEntryKind Kind);
