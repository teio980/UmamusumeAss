using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

public interface IIndependentTrainingPipeline
{
    Task<IndependentTrainingResult> RunAsync(
        LastVerifiedConnection connection,
        IndependentTrainingSettings settings,
        IGrassTaskLogSink? logSink,
        IHachimiTaskLogSink? taskLogSink = null,
        CancellationToken cancellationToken = default);

    Task<IndependentTrainingResult> StopAsync(
        LastVerifiedConnection connection,
        IGrassTaskLogSink? logSink = null,
        IHachimiTaskLogSink? taskLogSink = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Settings owned exclusively by the Independent Training pipeline.
/// </summary>
public sealed record IndependentTrainingSettings(
    string ManifestPath,
    int TraineeId,
    bool ContinueExistingCareer,
    IReadOnlyList<int> SupportCardIds,
    string SupportDeckMode,
    string SupportDeckPreset,
    int? FriendSupportCardId,
    string LegacySelectionMode,
    bool UseLegacyGuest,
    bool UseCachedLegacy,
    IReadOnlyList<string> LegacyAttributeSparks,
    IReadOnlyList<string> LegacyAptitudeSparks,
    string TrainingFocus = "balanced",
    string LineupStrategy = "pace",
    IReadOnlyList<IndependentTrainingAgendaSelection>? AgendaSelections = null,
    IReadOnlyList<int>? SkillIds = null) : ICareerEntrySelectionSettings
{
    public string IndependentTrainingFocus => TrainingFocus;
    public string IndependentLineupStrategy => LineupStrategy;
    public IReadOnlyList<IndependentTrainingAgendaSelection> IndependentAgendaSelections =>
        EffectiveAgendaSelections;
    public IReadOnlyList<int> IndependentSkillIds => EffectiveSkillIds;

    public IReadOnlyList<IndependentTrainingAgendaSelection> EffectiveAgendaSelections =>
        AgendaSelections ?? [];

    public IReadOnlyList<int> EffectiveSkillIds => SkillIds ?? [];
}

public sealed record IndependentTrainingResult(
    bool Succeeded,
    string Message,
    int ActionsCompleted,
    string LastScreenId);
