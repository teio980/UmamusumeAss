using System.Text.Json.Serialization;

namespace UmamusumeWpfGui.Models;

public sealed class UmaDatabaseMeta
{
    [JsonPropertyName("source_name")]
    public string SourceName { get; set; } = string.Empty;

    [JsonPropertyName("source_url")]
    public string SourceUrl { get; set; } = string.Empty;

    [JsonPropertyName("source_type")]
    public string SourceType { get; set; } = string.Empty;

    [JsonPropertyName("region")]
    public string Region { get; set; } = "global";

    [JsonPropertyName("crawled_at_utc")]
    public string CrawledAtUtc { get; set; } = string.Empty;
}

public sealed class UmaBaseCharacterRecord
{
    [JsonPropertyName("base_character_id")]
    public int BaseCharacterId { get; set; }

    [JsonPropertyName("name_en")]
    public string NameEn { get; set; } = string.Empty;

    [JsonPropertyName("name_jp")]
    public string? NameJp { get; set; }

    [JsonPropertyName("trainee_ids")]
    public List<int> TraineeIds { get; set; } = [];

    [JsonPropertyName("region")]
    public string Region { get; set; } = "global";

    [JsonPropertyName("available")]
    public bool Available { get; set; }
}

public sealed class UmaTraineeStarStats
{
    [JsonPropertyName("star_level")]
    public int StarLevel { get; set; }

    [JsonPropertyName("stats")]
    public Dictionary<string, int?> Stats { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class UmaTraineeAptitudes
{
    [JsonPropertyName("surface")]
    public Dictionary<string, string> Surface { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("distance")]
    public Dictionary<string, string> Distance { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("strategy")]
    public Dictionary<string, string> Strategy { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class UmaTraineeRecord
{
    [JsonPropertyName("trainee_id")]
    public int TraineeId { get; set; }

    [JsonPropertyName("base_character_id")]
    public int BaseCharacterId { get; set; }

    [JsonPropertyName("name_en")]
    public string NameEn { get; set; } = string.Empty;

    [JsonPropertyName("name_jp")]
    public string? NameJp { get; set; }

    [JsonPropertyName("rarity")]
    public int Rarity { get; set; }

    [JsonPropertyName("strategies")]
    public List<string> Strategies { get; set; } = [];

    [JsonPropertyName("base_stats")]
    public Dictionary<string, int?> BaseStats { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("detail_stats")]
    public List<UmaTraineeStarStats> DetailStats { get; set; } = [];

    [JsonPropertyName("growth_rates")]
    public Dictionary<string, int?> GrowthRates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("aptitudes")]
    public UmaTraineeAptitudes Aptitudes { get; set; } = new();

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("detail_url")]
    public string? DetailUrl { get; set; }

    [JsonPropertyName("region")]
    public string Region { get; set; } = "global";

    [JsonPropertyName("available")]
    public bool Available { get; set; }
}

public sealed class UmaSupportTrainingEffect
{
    [JsonPropertyName("effect")]
    public string Effect { get; set; } = string.Empty;

    [JsonPropertyName("initial")]
    public string? Initial { get; set; }

    [JsonPropertyName("lv10")]
    public string? Level10 { get; set; }

    [JsonPropertyName("lv20")]
    public string? Level20 { get; set; }

    [JsonPropertyName("lv30")]
    public string? Level30 { get; set; }

    [JsonPropertyName("lv40")]
    public string? Level40 { get; set; }

    [JsonPropertyName("lv50")]
    public string? Level50 { get; set; }
}

public sealed class UmaSupportKeyEffect
{
    [JsonPropertyName("effect")]
    public string Effect { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

public sealed class UmaSupportCardRecord
{
    [JsonPropertyName("support_card_id")]
    public int SupportCardId { get; set; }

    [JsonPropertyName("name_en")]
    public string NameEn { get; set; } = string.Empty;

    [JsonPropertyName("featured_character_id")]
    public int? FeaturedCharacterId { get; set; }

    [JsonPropertyName("featured_character_name_en")]
    public string? FeaturedCharacterNameEn { get; set; }

    [JsonPropertyName("rarity")]
    public string Rarity { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("detail_url")]
    public string? DetailUrl { get; set; }

    [JsonPropertyName("training_effects")]
    public List<UmaSupportTrainingEffect> TrainingEffects { get; set; } = [];

    [JsonPropertyName("key_effects")]
    public List<UmaSupportKeyEffect> KeyEffects { get; set; } = [];

    [JsonPropertyName("region")]
    public string Region { get; set; } = "global";

    [JsonPropertyName("available")]
    public bool Available { get; set; }
}
