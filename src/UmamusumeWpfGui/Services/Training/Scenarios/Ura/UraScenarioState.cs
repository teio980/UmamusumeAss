using System.IO;

namespace UmamusumeWpfGui.Services.Training;

public enum UraStateSource
{
    Observed,
    Derived,
    Estimated,
    Unknown,
}

/// <summary>
/// Progress through the small setup page that appears after the shared
/// support-deck entry flow for a Normal Career. Keeping this separate from
/// the turn-engine state prevents the current run from replaying completed
/// setup actions while the live screen settles.
/// </summary>
public enum NormalCareerSetupStage
{
    EnterCareer = 0,
    ConfigureMode = 1,
    ConfigureStrategy = 2,
    StartCareer = 3,
    ConfirmStart = 4,
    // Keep the historical numeric values stable for existing checkpoints.
    AwaitCareerMain = 5,
    InCareer = 6,
    SkipIntro = 7,
    ConfigureQuickMode = 8,
    SetQuickMode = 9,
    ConfirmQuickMode = 10,
}

public sealed record UraObservedValue<T>(
    T? Value,
    UraStateSource Source,
    double Confidence,
    DateTimeOffset ObservedAt)
    where T : struct
{
}

public static class UraObservedValueFactory
{
    public static UraObservedValue<T> Unknown<T>()
        where T : struct =>
        new(default, UraStateSource.Unknown, 0, DateTimeOffset.UtcNow);

    public static UraObservedValue<T> FromObservation<T>(T value, double confidence)
        where T : struct =>
        new(value, UraStateSource.Observed, Math.Clamp(confidence, 0, 1), DateTimeOffset.UtcNow);

    public static UraObservedValue<T> FromDerived<T>(T value, double confidence)
        where T : struct =>
        new(value, UraStateSource.Derived, Math.Clamp(confidence, 0, 1), DateTimeOffset.UtcNow);
}

public sealed class UraCareerSessionState
{
    public string ScenarioId { get; set; } = "ura";
    public int? TraineeId { get; set; }
    public string PhaseId { get; set; } = "career";
    public int TurnIndex { get; set; }
    public string CurrentObjectiveId { get; set; } = "debut_race";
    public string? TurnPositionLabel { get; set; }
    public int? TurnsToGoal { get; set; }
    public int? FansToGoal { get; set; }
    public int? GradeRaceTimesLeft { get; set; }
    public string? TargetRaceGrade { get; set; }
    public int? TargetRaceGradeCode { get; set; }
    public int GradeRaceStartedTurnIndex { get; set; }
    public string? ObservedGoalText { get; set; }
    public string? ObservedGoalKind { get; set; }
    public UraStateSource TurnIndexSource { get; set; } = UraStateSource.Unknown;
    public double TurnIndexConfidence { get; set; }
    public int FinaleStageIndex { get; set; } = -1;
    public string? CurrentRaceId { get; set; }
    public int RetryCount { get; set; }
    public bool HasPendingRace { get; set; }
    // Run-scoped guard for the reusable race-runner strategy checkpoint.
    public bool RaceStrategyConfigured { get; set; }
    // Prevent a stale Replay marker from re-running the Next chain after it
    // already completed. Reset when a new race is entered.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RaceReplayFlowCompleted { get; set; }
    public bool HasScenarioEvent { get; set; }
    public bool IsCompleted { get; set; }
    // Indicates that the selected URA career has actually reached the career
    // main screen. It must not be inferred from the Home entry click: Home is
    // both the starting point and the terminal destination.
    public bool CareerStarted { get; set; }
    public NormalCareerSetupStage NormalSetupStage { get; set; } =
        NormalCareerSetupStage.EnterCareer;
    public UraPlannedAction LastAction { get; set; }
    // The Rest confirmation tap has been sent. Do not tap OK again while
    // waiting for its button to disappear from a fresh screenshot.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AwaitingRestConfirmationGone { get; set; }
    // Goal-complete templates are probed only during the short window after
    // an eligible action. Pending is set from the visible turn/fan objective;
    // Armed begins when the selected action is actually performed.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool GoalCompletionProbePending { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool GoalCompletionProbeArmed { get; set; }
    public string? PendingTrainingType { get; set; }
    // Run-scoped guard: a training tap may leave the picker visible while the
    // game is busy. Do not choose or tap the same training again in that span.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? TrainingClickIssuedType { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool TrainingClickTargetGone { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFinale => PhaseId.Equals("finale_underway", StringComparison.OrdinalIgnoreCase);
    public string LastScreenId { get; set; } = "unknown";
    public UraObservedValue<int> Energy { get; set; } =
        UraObservedValueFactory.FromObservation(100, 0.5);
    public UraObservedValue<int> Fans { get; set; } =
        UraObservedValueFactory.FromObservation(0, 0.2);
    public UraObservedValue<int> LastRacePlacement { get; set; } =
        UraObservedValueFactory.Unknown<int>();
    public Dictionary<string, int> RacePlacements { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<string> CompletedObjectiveIds { get; set; } = [];
}
