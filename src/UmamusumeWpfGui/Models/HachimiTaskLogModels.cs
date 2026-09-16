namespace UmamusumeWpfGui.Models;

public enum HachimiTaskLogStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Canceled,
    Skipped,
}

public enum HachimiTaskLogEventKind
{
    Info,
    Action,
    Detection,
    Branch,
    Success,
    Warning,
    Failure,
}

/// <summary>
/// Identifies the user-facing meaning of an ordinary Hachimi JSON graph.
/// Generic graph nodes do not enter the task log unless their profile has an
/// explicit semantic description.
/// </summary>
public enum HachimiTaskLogProfile
{
    None,
    TeamRace,
    DailyRace,
    MailCollection,
    MissionCollection,
    Shop,
    Career,
}

/// <summary>
/// A concise, user-facing execution event for the Hachimi workspace.
/// Technical polling, coordinates, scores and retry details stay in the
/// existing diagnostic log instead of being copied into this model.
/// </summary>
public sealed record HachimiTaskLogEntry(
    DateTimeOffset Timestamp,
    string Step,
    string Message,
    HachimiTaskLogEventKind Kind);
