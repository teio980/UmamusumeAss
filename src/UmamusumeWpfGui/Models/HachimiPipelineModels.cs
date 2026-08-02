using System.Text.Json.Serialization;

namespace UmamusumeWpfGui.Models;

/// <summary>
/// Shared definition for ordinary Hachimi pipelines.
/// StartGame intentionally keeps its legacy definition because its startup
/// monitor has special same-frame priority and trigger-chain semantics.
/// </summary>
public sealed class HachimiPipelineDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("referenceWidth")]
    public int ReferenceWidth { get; set; } = 900;

    [JsonPropertyName("referenceHeight")]
    public int ReferenceHeight { get; set; } = 1600;

    [JsonPropertyName("templates")]
    public HachimiPipelineTemplates Templates { get; set; } = new();

    [JsonPropertyName("tasks")]
    public Dictionary<string, HachimiPipelineTask> Tasks { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("timing")]
    public HachimiPipelineTiming Timing { get; set; } = new();

    [JsonIgnore]
    public string BaseDirectory { get; set; } = AppContext.BaseDirectory;

    public HachimiPipelineTask GetTask(string name)
    {
        if (!Tasks.TryGetValue(name, out var task))
            throw new InvalidOperationException($"Pipeline task '{name}' is not defined.");

        return task;
    }

    public bool TryGetTask(string name, out HachimiPipelineTask? task) =>
        Tasks.TryGetValue(name, out task);
}

/// <summary>
/// MAA-compatible visual task fields plus Hachimi runtime timing fields.
/// </summary>
public sealed class HachimiPipelineTask
{
    [JsonPropertyName("algorithm")]
    public string Algorithm { get; set; } = "MatchTemplate";

    [JsonPropertyName("action")]
    public string Action { get; set; } = "ClickSelf";

    [JsonPropertyName("template")]
    public string? Template { get; set; }

    [JsonPropertyName("templThreshold")]
    public double TemplateThreshold { get; set; } = 0.86;

    [JsonPropertyName("roi")]
    public int[]? Roi { get; set; }

    [JsonPropertyName("specificRect")]
    public int[]? SpecificRect { get; set; }

    [JsonPropertyName("preDelay")]
    public int PreDelay { get; set; }

    [JsonPropertyName("postDelay")]
    public int PostDelay { get; set; }

    [JsonPropertyName("waitMs")]
    public int WaitMilliseconds { get; set; }

    [JsonPropertyName("timeoutMs")]
    public int TimeoutMilliseconds { get; set; } = 10_000;

    [JsonPropertyName("pollIntervalMs")]
    public int PollIntervalMilliseconds { get; set; }

    // MAA-style state-machine transitions. They are optional so the existing
    // task JSON remains valid and can be extended without changing its shape.
    [JsonPropertyName("next")]
    public List<string> Next { get; set; } = [];

    [JsonPropertyName("onErrorNext")]
    public List<string> OnErrorNext { get; set; } = [];

    [JsonPropertyName("exceededNext")]
    public List<string> ExceededNext { get; set; } = [];

    [JsonPropertyName("sub")]
    public List<string> Sub { get; set; } = [];

    [JsonPropertyName("maxTimes")]
    public int MaxTimes { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("countAs")]
    public string? CountAs { get; set; }
}

public sealed class HachimiPipelineTemplates
{
    [JsonPropertyName("raceResult")]
    public string? RaceResult { get; set; }

    [JsonPropertyName("randomShop")]
    public string? RandomShop { get; set; }
}

/// <summary>
/// Domain timing values are kept in one shared shape so every ordinary
/// pipeline has the same JSON envelope. Unused values retain their defaults.
/// </summary>
public sealed class HachimiPipelineTiming
{
    [JsonPropertyName("navigationMs")]
    public int NavigationMilliseconds { get; set; } = 1_200;

    [JsonPropertyName("mailboxLoadMs")]
    public int MailboxLoadMilliseconds { get; set; } = 1_800;

    [JsonPropertyName("collectionSettleMs")]
    public int CollectionSettleMilliseconds { get; set; } = 1_200;

    [JsonPropertyName("homeTimeoutMs")]
    public int HomeTimeoutMilliseconds { get; set; } = 5_000;

    [JsonPropertyName("homeRetryTimeoutMs")]
    public int HomeRetryTimeoutMilliseconds { get; set; } = 2_500;

    [JsonPropertyName("homeVerifyTimeoutMs")]
    public int HomeVerifyTimeoutMilliseconds { get; set; } = 3_000;

    [JsonPropertyName("backAttempts")]
    public int BackAttempts { get; set; } = 3;

    [JsonPropertyName("backSettleMs")]
    public int BackSettleMilliseconds { get; set; } = 600;

    [JsonPropertyName("pollIntervalMs")]
    public int PollIntervalMilliseconds { get; set; } = 300;

    [JsonPropertyName("teamDownloadMs")]
    public int TeamDownloadMilliseconds { get; set; } = 10_000;

    [JsonPropertyName("nextRaceLoadMs")]
    public int NextRaceLoadMilliseconds { get; set; } = 10_000;

    [JsonPropertyName("playbackLoadMs")]
    public int PlaybackLoadMilliseconds { get; set; } = 20_000;

    [JsonPropertyName("skipSettleMs")]
    public int SkipSettleMilliseconds { get; set; } = 2_500;

    [JsonPropertyName("raceTimeoutMs")]
    public int RaceTimeoutMilliseconds { get; set; } = 60_000;

    [JsonPropertyName("shopProbeMs")]
    public int ShopProbeMilliseconds { get; set; } = 1_500;

    [JsonPropertyName("betweenRacesMs")]
    public int BetweenRacesMilliseconds { get; set; } = 1_200;
}
