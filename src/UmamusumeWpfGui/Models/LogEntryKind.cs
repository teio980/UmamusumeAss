namespace UmamusumeWpfGui.Models;

/// <summary>
/// Categorizes a log entry for color coding.
/// Info = gray, Success = pink (#E91E8C), Failure = red (#F44336).
/// </summary>
public enum LogEntryKind
{
    Info,
    Success,
    Failure,
}
