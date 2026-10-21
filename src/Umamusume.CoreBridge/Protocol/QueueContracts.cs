using System.Text.Json;

namespace Umamusume.CoreBridge;

/// <summary>A task and its immutable settings payload for one Core queue submission.</summary>
public sealed record QueueTaskRequest(string TaskId, string SettingsJson);

public enum QueueRunState
{
    Idle,
    Running,
    Completed,
    Failed,
    Canceled,
}

public enum QueueTaskStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
}

public sealed record QueueTaskSnapshot(
    ulong Id,
    string Name,
    QueueTaskStatus Status,
    int ErrorCode,
    string Message,
    int CompletedUnits,
    int TotalUnits);

/// <summary>A consistent copy of Core's most recent queue run and task states.</summary>
public sealed record QueueRunSnapshot(
    ulong? OperationId,
    QueueRunState State,
    ulong? CurrentTaskId,
    string CurrentTaskName,
    int CurrentCompletedUnits,
    int CurrentTotalUnits,
    int SucceededCount,
    int FailedCount,
    int SkippedCount,
    IReadOnlyList<QueueTaskSnapshot> Tasks);

/// <summary>
/// Catalog rows remain JSON values because the shipped game database adds fields
/// as resources evolve. The typed categories provide a stable Bridge surface
/// while preserving every native field for selectors and settings editors.
/// Each JsonElement is cloned and remains valid after the native buffer is freed.
/// Non-row catalog values such as metadata objects are exposed in AdditionalCategories.
/// </summary>
public sealed record CoreCatalog(
    IReadOnlyList<JsonElement> Characters,
    IReadOnlyList<JsonElement> BaseCharacters,
    IReadOnlyList<JsonElement> Races,
    IReadOnlyList<JsonElement> IndependentRaces,
    IReadOnlyList<JsonElement> Skills,
    IReadOnlyList<JsonElement> SupportCards,
    IReadOnlyList<JsonElement> Events,
    IReadOnlyList<JsonElement> Strategies,
    IReadOnlyDictionary<string, JsonElement> AdditionalCategories)
{
    public static CoreCatalog Empty { get; } = new(
        Array.Empty<JsonElement>(), Array.Empty<JsonElement>(), Array.Empty<JsonElement>(),
        Array.Empty<JsonElement>(), Array.Empty<JsonElement>(), Array.Empty<JsonElement>(),
        Array.Empty<JsonElement>(), Array.Empty<JsonElement>(),
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, JsonElement>(
            new Dictionary<string, JsonElement>()));
}
