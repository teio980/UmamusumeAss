namespace UmamusumeWpfGui.Services.Training;

public sealed record CareerStartupRecoveryRule(
    string RecognitionScreenId,
    string ResumeScreenId,
    CareerEntryNavigationStep? EntryStep,
    NormalCareerSetupStage? SetupStage);

public sealed record CareerStartupRecoveryTarget(
    string RecognitionScreenId,
    string ResumeScreenId,
    CareerEntryNavigationStep? EntryStep,
    NormalCareerSetupStage? SetupStage);

/// <summary>
/// Skeleton for one-frame startup recovery detection.
/// </summary>
public sealed class CareerStartupRecoveryDetector
{
    // Detection will return a target without executing actions or checkpoints.
}
