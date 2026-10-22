using Umamusume.CoreBridge;
using System.Text.Json.Nodes;
using Umamusume.AppCore.Services;

namespace Umamusume.AppCore.Models;

/// <summary>A task id and optional editable settings profile captured for one queue run.</summary>
public sealed record UmaTaskRequest
{
    public string TaskId { get; }

    public JsonObject? Settings { get; }

    public string? SettingsJson => Settings?.ToJsonString();

    public UmaTaskRequest(string taskId, JsonObject? settings = null)
    {
        TaskId = taskId;
        Settings = settings is null ? null : (JsonObject)settings.DeepClone();
    }

    /// <summary>Compatibility overload for callers that already have serialized profile JSON.</summary>
    public UmaTaskRequest(string taskId, string settingsJson)
        : this(taskId, UmaTaskSettingsSerializer.Deserialize(settingsJson))
    {
    }
}

public enum UmaTaskQueueEventKind
{
    Started,
    Progress,
    Completed,
    Failed,
    Canceled,
    Skipped,
    Decision,
    ActionConfirmed,
}

public sealed record UmaTaskQueueEvent(
    int TaskIndex,
    string TaskId,
    UmaTaskQueueEventKind Kind,
    int? CompletedUnits = null,
    int? TotalUnits = null,
    string? TaskName = null,
    string? Message = null,
    bool? Succeeded = null,
    ConnectionErrorCode? ErrorCode = null,
    GameTaskEventType? EventType = null);

public sealed record UmaTaskQueueRunResult(
    bool Succeeded,
    bool Canceled,
    int SucceededTasks,
    int FailedTasks,
    int SkippedTasks,
    string Message);
