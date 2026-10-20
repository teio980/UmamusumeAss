using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Umamusume.AppCore.Services;

public static class UmaTaskSettingsSerializer
{
    public static JsonObject Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new JsonObject();

        var node = JsonNode.Parse(json);
        return node as JsonObject
            ?? throw new JsonException("Task settings must be a JSON object.");
    }

    public static string Serialize(JsonObject? settings) =>
        settings?.ToJsonString() ?? "{}";
}
