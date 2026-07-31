using System.Text.Json.Nodes;

namespace UmamusumeWpfGui.Models;





public sealed class GrassTaskCacheItem
{
    public string TaskId { get; set; } = "";

    public bool IsEnabled { get; set; } = true;

    public JsonObject Settings { get; set; } = new();
}
