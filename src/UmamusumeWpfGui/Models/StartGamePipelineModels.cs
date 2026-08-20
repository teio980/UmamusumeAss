using System.Text.Json.Serialization;

namespace UmamusumeWpfGui.Models;





public sealed class StartGamePipelineDefinition
{
    [JsonPropertyName("start")]
    public string Start { get; set; } = "StartGame";

    [JsonPropertyName("tasks")]
    public Dictionary<string, StartGamePipelineTask> Tasks { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class StartGamePipelineTask
{
    [JsonPropertyName("algorithm")]
    public string Algorithm { get; set; } = "JustReturn";

    [JsonPropertyName("action")]
    public string Action { get; set; } = "DoNothing";

    [JsonPropertyName("template")]
    public string? Template { get; set; }

    [JsonPropertyName("monitorTasks")]
    public List<string> MonitorTasks { get; set; } = [];

    [JsonPropertyName("triggerTask")]
    public string? TriggerTask { get; set; }

    [JsonPropertyName("triggerChain")]
    public List<string> TriggerChain { get; set; } = [];

    [JsonPropertyName("templThreshold")]
    public double TemplateThreshold { get; set; } = 0.85;

    [JsonPropertyName("roi")]
    public int[]? Roi { get; set; }

    [JsonPropertyName("referenceWidth")]
    public int ReferenceWidth { get; set; } = 1280;

    [JsonPropertyName("referenceHeight")]
    public int ReferenceHeight { get; set; } = 720;

    [JsonPropertyName("specificRect")]
    public int[]? SpecificRect { get; set; }

    [JsonPropertyName("rectMove")]
    public int[]? RectMove { get; set; }

    [JsonPropertyName("inputText")]
    public string? InputText { get; set; }

    [JsonPropertyName("keyCode")]
    public string? KeyCode { get; set; }

    [JsonPropertyName("preDelay")]
    public int PreDelay { get; set; }

    [JsonPropertyName("postDelay")]
    public int PostDelay { get; set; }

    [JsonPropertyName("waitMs")]
    public int WaitMilliseconds { get; set; }

    [JsonPropertyName("timeoutMs")]
    public int TimeoutMilliseconds { get; set; } = 60_000;

    [JsonPropertyName("pollIntervalMs")]
    public int PollIntervalMilliseconds { get; set; } = 700;

    [JsonPropertyName("successConfirmDelayMs")]
    public int SuccessConfirmationDelayMilliseconds { get; set; } = 5_000;

    [JsonPropertyName("maxTimes")]
    public int MaxTimes { get; set; } = 1;

    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("next")]
    public List<string> Next { get; set; } = [];

    [JsonPropertyName("onErrorNext")]
    public List<string> OnErrorNext { get; set; } = [];
}

public sealed record StartGamePipelineResult(
    bool Succeeded,
    bool HomeDetected,
    string Message);

public sealed record TemplateMatchResult(
    bool Found,
    double Score,
    int X,
    int Y,
    int Width,
    int Height)
{
    public int CenterX => X + Width / 2;

    public int CenterY => Y + Height / 2;
}
