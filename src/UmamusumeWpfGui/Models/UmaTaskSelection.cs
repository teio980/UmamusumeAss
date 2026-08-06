using System.Text.Json.Serialization;

namespace UmamusumeWpfGui.Models;

/// <summary>
/// Per-task selection. It deliberately stores IDs only; master data remains
/// in resource/uma/database and is resolved by IUmaDatabaseService at runtime.
/// </summary>
public sealed class UmaTaskSelection
{
    [JsonPropertyName("region")]
    public string Region { get; set; } = "global";

    [JsonPropertyName("traineeId")]
    public int? TraineeId { get; set; }

    [JsonPropertyName("supportCardIds")]
    public List<int> SupportCardIds { get; set; } = [];
}
