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

    [JsonPropertyName("uma")]
    public UmaTaskSelection? Uma { get; set; }

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

    [JsonPropertyName("pipeline")]
    public string? Pipeline { get; set; }

    [JsonPropertyName("entry")]
    public string? Entry { get; set; }

    [JsonPropertyName("swipe")]
    public int[]? Swipe { get; set; }

    /// <summary>
    /// Text supplied to the JSON Input action.  The value is kept in the
    /// definition for simple static tasks; data-driven callers may provide
    /// an <see cref="HachimiPipelineRunOptions"/> override without putting
    /// user data or an interaction decision in C#.
    /// </summary>
    [JsonPropertyName("inputText")]
    public string? InputText { get; set; }

    [JsonPropertyName("keyCode")]
    public string? KeyCode { get; set; }

    [JsonPropertyName("template")]
    public string? Template { get; set; }

    [JsonPropertyName("templThreshold")]
    public double TemplateThreshold { get; set; } = 0.86;

    [JsonPropertyName("roi")]
    public int[]? Roi { get; set; }

    /// <summary>
    /// Optional second-click search region used by actions that may need one
    /// retry after the first tap did not leave the current screen.
    /// </summary>
    [JsonPropertyName("fallbackRoi")]
    public int[]? FallbackRoi { get; set; }

    /// <summary>
    /// Screen templates that prove a click entered the next page.  The
    /// transition-aware click action stops after the first tap when any of
    /// these templates is observed; it never blindly taps twice.
    /// </summary>
    [JsonPropertyName("transitionTemplates")]
    public List<string> TransitionTemplates { get; set; } = [];

    [JsonPropertyName("transitionThreshold")]
    public double TransitionThreshold { get; set; } = 0.78;

    [JsonPropertyName("transitionTimeoutMs")]
    public int TransitionTimeoutMilliseconds { get; set; } = 2_600;

    [JsonPropertyName("transitionPollIntervalMs")]
    public int TransitionPollIntervalMilliseconds { get; set; } = 250;

    [JsonPropertyName("specificRect")]
    public int[]? SpecificRect { get; set; }

    [JsonPropertyName("searchRois")]
    public List<int[]> SearchRois { get; set; } = [];

    [JsonPropertyName("minScoreGap")]
    public double MinimumScoreGap { get; set; }

    [JsonPropertyName("scaleCandidates")]
    public List<double> ScaleCandidates { get; set; } = [];

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

    [JsonPropertyName("monitorTasks")]
    public List<string> MonitorTasks { get; set; } = [];

    [JsonPropertyName("successTask")]
    public string? SuccessTask { get; set; }

    [JsonPropertyName("successTasks")]
    public List<string> SuccessTasks { get; set; } = [];

    [JsonPropertyName("maxTimes")]
    public int MaxTimes { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("countAs")]
    public string? CountAs { get; set; }

    [JsonPropertyName("countKey")]
    public string? CountKey { get; set; }

    [JsonPropertyName("targetText")]
    public string? TargetText { get; set; }

    [JsonPropertyName("ocrLanguage")]
    public string? OcrLanguage { get; set; }

    [JsonPropertyName("fuzzyThreshold")]
    public double FuzzyThreshold { get; set; } = 0.86;

    [JsonPropertyName("unique")]
    public bool Unique { get; set; } = true;

    [JsonPropertyName("clickOffset")]
    public int[]? ClickOffset { get; set; }

    /// <summary>
    /// Optional reference-space anchor for OCR clicks.  When present, the
    /// runner keeps the OCR match's Y coordinate but taps this X coordinate.
    /// This is useful for rows whose selectable checkbox is fixed to the
    /// left of a variable-length label.
    /// </summary>
    [JsonPropertyName("clickAnchor")]
    public int[]? ClickAnchor { get; set; }

    /// <summary>
    /// Optional, data-driven post-click state verification.  The visual
    /// runtime probes a small HSV region around the click anchor and retries
    /// only while the expected state is not observed.
    /// </summary>
    [JsonPropertyName("clickVerification")]
    public HachimiClickVerification? ClickVerification { get; set; }

    [JsonPropertyName("rowExpansion")]
    public int[]? RowExpansion { get; set; }

    [JsonPropertyName("maxScrolls")]
    public int MaxScrolls { get; set; }

    /// <summary>
    /// Optional vertical span, in reference pixels, used to combine OCR lines
    /// that belong to one visual row/card before matching the target text.
    /// Zero keeps the ordinary one-detection-per-candidate behavior.
    /// </summary>
    [JsonPropertyName("ocrGroupRowHeight")]
    public int OcrGroupRowHeight { get; set; }

    /// <summary>
    /// Maximum vertical gap, in reference pixels, between adjacent OCR lines
    /// in one grouped row/card.  Zero derives the allowance from the group
    /// height.
    /// </summary>
    [JsonPropertyName("ocrRowGap")]
    public int OcrRowGap { get; set; }

    /// <summary>
    /// OCR matching policy.  The default is line-level fuzzy matching;
    /// tokenCoverage is an opt-in unordered token policy for multi-line cards.
    /// </summary>
    [JsonPropertyName("ocrMatchMode")]
    public string? OcrMatchMode { get; set; }

    /// <summary>
    /// Token-coverage tasks normally require every target token to be present
    /// in the grouped text.  This prevents a same-category card (for example
    /// another Junior Stakes race) from satisfying a location-specific target.
    /// </summary>
    [JsonPropertyName("ocrRequireAllTokens")]
    public bool OcrRequireAllTokens { get; set; } = true;
}

/// <summary>
/// Generic color-state probe settings for an anchored visual click.  Values
/// are authored in the pipeline's reference coordinate/color space; no skill
/// names or fixed game coordinates belong in the runner.
/// </summary>
public sealed class HachimiClickVerification
{
    /// <summary>Probe before the first tap so an already-selected row is not toggled off.</summary>
    [JsonPropertyName("preCheck")]
    public bool PreCheck { get; set; } = true;

    /// <summary>Square probe radius in reference pixels.</summary>
    [JsonPropertyName("probeRadius")]
    public int ProbeRadius { get; set; } = 8;

    /// <summary>Optional reference-space offset from the click anchor to the probe center.</summary>
    [JsonPropertyName("probeOffset")]
    public int[]? ProbeOffset { get; set; }

    /// <summary>Inclusive HSV hue bounds in degrees. Bounds may wrap across 360.</summary>
    [JsonPropertyName("hueMin")]
    public double HueMin { get; set; } = 70d;

    [JsonPropertyName("hueMax")]
    public double HueMax { get; set; } = 170d;

    /// <summary>Inclusive HSV saturation/value bounds, normalized to 0..1.</summary>
    [JsonPropertyName("saturationMin")]
    public double SaturationMin { get; set; } = 0.25d;

    [JsonPropertyName("saturationMax")]
    public double SaturationMax { get; set; } = 1d;

    [JsonPropertyName("valueMin")]
    public double ValueMin { get; set; } = 0.20d;

    [JsonPropertyName("valueMax")]
    public double ValueMax { get; set; } = 1d;

    /// <summary>Fraction of sampled pixels that must satisfy the HSV bounds.</summary>
    [JsonPropertyName("minimumMatchRatio")]
    public double MinimumMatchRatio { get; set; } = 0.20d;

    /// <summary>Delay after each tap before taking the state probe.</summary>
    [JsonPropertyName("settleDelayMs")]
    public int SettleDelayMilliseconds { get; set; } = 150;

    /// <summary>Maximum time to poll for the expected state after a tap.</summary>
    [JsonPropertyName("verifyTimeoutMs")]
    public int VerifyTimeoutMilliseconds { get; set; } = 1_000;

    /// <summary>Polling interval used by the post-click state probe.</summary>
    [JsonPropertyName("verifyPollIntervalMs")]
    public int VerifyPollIntervalMilliseconds { get; set; } = 100;

    /// <summary>Maximum number of additional taps after the initial tap.</summary>
    [JsonPropertyName("maxRetries")]
    public int MaxRetries { get; set; }

    /// <summary>Delay before a retry probe/tap cycle.</summary>
    [JsonPropertyName("retryDelayMs")]
    public int RetryDelayMilliseconds { get; set; } = 300;
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
