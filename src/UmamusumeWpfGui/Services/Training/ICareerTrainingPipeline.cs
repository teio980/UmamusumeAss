using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public interface ICareerTrainingPipeline
{
    Task<CareerTrainingResult> RunAsync(
        LastVerifiedConnection connection,
        CareerTrainingSettings settings,
        IGrassTaskLogSink? logSink,
        IHachimiTaskLogSink? taskLogSink = null,
        CancellationToken cancellationToken = default);

    Task<CareerTrainingResult> StopAsync(
        LastVerifiedConnection connection,
        IGrassTaskLogSink? logSink = null,
        IHachimiTaskLogSink? taskLogSink = null,
        CancellationToken cancellationToken = default);
}

public static class CareerEventHandlingModes
{
    public const string Default = "default";
}

public sealed record CareerTrainingSettings(
    string ManifestPath,
    int TraineeId,
    bool ContinueExistingCareer,
    IReadOnlyList<int> SupportCardIds,
    string SupportDeckMode,
    string SupportDeckPreset,
    int? FriendSupportCardId,
    string StrategyId,
    bool PauseOnUnknownOutcome,
    bool AllowOptionalRaces,
    string LegacySelectionMode,
    bool UseLegacyGuest,
    bool UseCachedLegacy,
    IReadOnlyList<string> LegacyAttributeSparks,
    IReadOnlyList<string> LegacyAptitudeSparks,
    string LineupStrategy = "pace",
    string EventHandling = CareerEventHandlingModes.Default,
    bool RetryFailedRaceWithAlarmClock = false) : ICareerEntrySelectionSettings;

/// <summary>
/// Settings needed by the shared Home-to-Final-Confirmation navigation.
/// </summary>
public interface ICareerEntrySelectionSettings
{
    int TraineeId { get; }
    bool ContinueExistingCareer { get; }
    IReadOnlyList<int> SupportCardIds { get; }
    string SupportDeckMode { get; }
    string SupportDeckPreset { get; }
    int? FriendSupportCardId { get; }
    string LegacySelectionMode { get; }
    bool UseLegacyGuest { get; }
    bool UseCachedLegacy { get; }
    IReadOnlyList<string> LegacyAttributeSparks { get; }
    IReadOnlyList<string> LegacyAptitudeSparks { get; }
}

public sealed record CareerTrainingResult(
    bool Succeeded,
    string Message,
    int ActionsCompleted,
    string LastScreenId);
