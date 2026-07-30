using System.Text.Json.Nodes;

namespace UmamusumeWpfGui.Models;

/// <summary>
/// Persisted representation of one queued task. The module owns the shape of
/// <see cref="Settings"/> so new task types can evolve independently.
/// </summary>
public sealed class GrassTaskCacheItem
{
    public string TaskId { get; set; } = "";

    public bool IsEnabled { get; set; } = true;

    public JsonObject Settings { get; set; } = new();
}
