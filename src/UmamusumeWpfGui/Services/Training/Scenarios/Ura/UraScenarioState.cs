namespace UmamusumeWpfGui.Services.Training;

public enum UraStateSource
{
    Observed,
    Derived,
    Estimated,
    Unknown,
}

public enum UraCalendarStage
{
    Regular,
    SummerCamp,
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

/// <summary>
/// Canonical state for Normal setup progress. The legacy flat property on
/// <see cref="UraCareerSessionState"/> forwards to <see cref="Stage"/>.
/// </summary>
public sealed class NormalCareerSetupState
{
    public NormalCareerSetupStage Stage { get; set; } = NormalCareerSetupStage.EnterCareer;
}

/// <summary>
/// Canonical state for turn and race transitions, including transient guards.
/// The historical flat UraCareerSessionState properties forward into this
/// object so checkpoints keep their original field names.
/// </summary>
public sealed class UraCareerFlowState
{
    public string? CurrentRaceId { get; set; }
    public int RetryCount { get; set; }
    public bool HasPendingRace { get; set; }
    public int ConsecutiveRaceTurns { get; set; }
    public bool RaceStreakWarningActionIssued { get; set; }
    public UraPlannedAction? PendingTurnAction { get; set; }
    public int? PendingActionTurnIndex { get; set; }
    public int? PendingActionTurnsToGoal { get; set; }
    public int? RaceUnavailableTurnIndex { get; set; }
    public bool RaceStrategyConfigured { get; set; }
    public bool RaceReplayFlowCompleted { get; set; }
    public bool RaceRetryDeclined { get; set; }
    public bool RaceRetryDialogActionIssued { get; set; }
    public int RaceRetryDialogWaitCount { get; set; }
    public bool InheritanceEventPending { get; set; }
    public bool AwaitingRestConfirmationGone { get; set; }
    public bool AwaitingRecreationConfirmationGone { get; set; }
    public bool GoalCompletionProbePending { get; set; }
    public bool GoalCompletionProbeArmed { get; set; }
    public string? PendingTrainingType { get; set; }
    public bool TrainingSelectionEntryConfirmed { get; set; }
    public string? TrainingClickIssuedType { get; set; }
    public bool TrainingClickTargetGone { get; set; }
    public bool TrainingTurnCommitPending { get; set; }
    public string? TrainingTurnCommitType { get; set; }
    public int? TrainingTurnCommitTurnIndex { get; set; }

    // Smart training observations are intentionally run-scoped. They are
    // never serialized into a checkpoint because a resumed picker must be
    // scanned again instead of trusting stale OCR from a prior frame.
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<UraTrainingCandidate> SmartTrainingCandidates { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public string? SmartTrainingChosenType { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool SmartTrainingPreviewReady { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool SmartTrainingFallbackPending { get; set; }
}

public sealed class UraCareerSessionState
{
    private readonly CareerRuntimeState _runtime = new()
    {
        ScenarioId = "ura",
        PhaseId = "career",
        LastScreenId = "unknown",
        LastAction = UraPlannedAction.Training,
        Energy = UraObservedValueFactory.FromObservation(100, 0.5),
    };

    private readonly NormalCareerSetupState _setup = new();
    private readonly UraCareerFlowState _flows = new();

    /// <summary>
    /// The shared runtime state is canonical. It is ignored by JSON because
    /// the forwarding properties below retain the established flat checkpoint
    /// contract.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CareerRuntimeState Runtime => _runtime;

    /// <summary>Normal setup progress; the flat NormalSetupStage is its facade.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public NormalCareerSetupState Setup => _setup;

    /// <summary>Turn and race transition state; flat properties remain the checkpoint facade.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public UraCareerFlowState Flows => _flows;

    public string ScenarioId
    {
        get => _runtime.ScenarioId;
        set => _runtime.ScenarioId = value;
    }

    public int? TraineeId { get; set; }

    public string PhaseId
    {
        get => _runtime.PhaseId;
        set => _runtime.PhaseId = value;
    }

    public int TurnIndex
    {
        get => _runtime.TurnIndex;
        set => _runtime.TurnIndex = value;
    }

    public string CurrentObjectiveId { get; set; } = "debut_race";
    public string? TurnPositionLabel { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public UraCalendarStage CalendarStage { get; set; }

    public int? TurnsToGoal { get; set; }
    public int? FansToGoal { get; set; }
    public int? GradeRaceTimesLeft { get; set; }
    public string? TargetRaceGrade { get; set; }
    public int GradeRaceStartedTurnIndex { get; set; }
    public string? ObservedGoalText { get; set; }
    public string? ObservedGoalKind { get; set; }
    public UraStateSource TurnIndexSource { get; set; } = UraStateSource.Unknown;
    public double TurnIndexConfidence { get; set; }
    public int FinaleStageIndex { get; set; } = -1;

    public string? CurrentRaceId
    {
        get => _flows.CurrentRaceId;
        set => _flows.CurrentRaceId = value;
    }

    public int RetryCount
    {
        get => _flows.RetryCount;
        set => _flows.RetryCount = value;
    }

    public bool HasPendingRace
    {
        get => _flows.HasPendingRace;
        set => _flows.HasPendingRace = value;
    }

    // Saturated at two: the third consecutive race can trigger the game's
    // warning. A turn is counted only after its next main page is observed.
    public int ConsecutiveRaceTurns
    {
        get => _flows.ConsecutiveRaceTurns;
        set => _flows.ConsecutiveRaceTurns = value;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool RaceStreakWarningActionIssued
    {
        get => _flows.RaceStreakWarningActionIssued;
        set => _flows.RaceStreakWarningActionIssued = value;
    }

    public UraPlannedAction? PendingTurnAction
    {
        get => _flows.PendingTurnAction;
        set => _flows.PendingTurnAction = value;
    }

    public int? PendingActionTurnIndex
    {
        get => _flows.PendingActionTurnIndex;
        set => _flows.PendingActionTurnIndex = value;
    }

    public int? PendingActionTurnsToGoal
    {
        get => _flows.PendingActionTurnsToGoal;
        set => _flows.PendingActionTurnsToGoal = value;
    }

    // When the race list has no races, skip another race attempt on this turn.
    [System.Text.Json.Serialization.JsonIgnore]
    public int? RaceUnavailableTurnIndex
    {
        get => _flows.RaceUnavailableTurnIndex;
        set => _flows.RaceUnavailableTurnIndex = value;
    }

    // Run-scoped guard for the reusable race-runner strategy checkpoint.
    public bool RaceStrategyConfigured
    {
        get => _flows.RaceStrategyConfigured;
        set => _flows.RaceStrategyConfigured = value;
    }

    // Confirmed Normal Career skill purchases for this run.
    public List<int> NormalLearnedSkillIds { get; set; } = [];

    // Prevent a stale Replay marker from re-running the Next chain after it
    // already completed. Reset when a new race is entered.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RaceReplayFlowCompleted
    {
        get => _flows.RaceReplayFlowCompleted;
        set => _flows.RaceReplayFlowCompleted = value;
    }

    // A declined retry must proceed to the existing Career settlement pages,
    // without treating a losing result as another ordinary race objective.
    public bool RaceRetryDeclined
    {
        get => _flows.RaceRetryDeclined;
        set => _flows.RaceRetryDeclined = value;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool RaceRetryDialogActionIssued
    {
        get => _flows.RaceRetryDialogActionIssued;
        set => _flows.RaceRetryDialogActionIssued = value;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public int RaceRetryDialogWaitCount
    {
        get => _flows.RaceRetryDialogWaitCount;
        set => _flows.RaceRetryDialogWaitCount = value;
    }

    public bool HasScenarioEvent { get; set; }
    public bool IsCompleted { get; set; }

    // A Classic/Senior late-March action is followed by the inheritance GO
    // overlay before Career Main can accept another turn action.
    public bool InheritanceEventPending
    {
        get => _flows.InheritanceEventPending;
        set => _flows.InheritanceEventPending = value;
    }

    // Indicates that the selected URA career has actually reached the career
    // main screen. It must not be inferred from the Home entry click: Home is
    // both the starting point and the terminal destination.
    public bool CareerStarted
    {
        get => _runtime.CareerStarted;
        set => _runtime.CareerStarted = value;
    }

    public NormalCareerSetupStage NormalSetupStage
    {
        get => _setup.Stage;
        set => _setup.Stage = value;
    }

    public UraPlannedAction LastAction
    {
        get => _runtime.LastAction;
        set => _runtime.LastAction = value;
    }

    // The Rest confirmation tap has been sent. Do not tap OK again while
    // waiting for its button to disappear from a fresh screenshot.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AwaitingRestConfirmationGone
    {
        get => _flows.AwaitingRestConfirmationGone;
        set => _flows.AwaitingRestConfirmationGone = value;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool AwaitingRecreationConfirmationGone
    {
        get => _flows.AwaitingRecreationConfirmationGone;
        set => _flows.AwaitingRecreationConfirmationGone = value;
    }

    // Goal-complete templates are probed during the short window after a
    // non-race turn action or a qualifying race result. Pending is set when
    // a non-race action is selected; Armed begins when it is performed.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool GoalCompletionProbePending
    {
        get => _flows.GoalCompletionProbePending;
        set => _flows.GoalCompletionProbePending = value;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool GoalCompletionProbeArmed
    {
        get => _flows.GoalCompletionProbeArmed;
        set => _flows.GoalCompletionProbeArmed = value;
    }

    public string? PendingTrainingType
    {
        get => _flows.PendingTrainingType;
        set => _flows.PendingTrainingType = value;
    }

    // The Training entry task has already recognized the picker header.
    // Consume this only in the current run; resumed sessions must observe again.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool TrainingSelectionEntryConfirmed
    {
        get => _flows.TrainingSelectionEntryConfirmed;
        set => _flows.TrainingSelectionEntryConfirmed = value;
    }

    // Run-scoped guard: a training tap may leave the picker visible while the
    // game is busy. Do not choose or tap the same training again in that span.
    [System.Text.Json.Serialization.JsonIgnore]
    public string? TrainingClickIssuedType
    {
        get => _flows.TrainingClickIssuedType;
        set => _flows.TrainingClickIssuedType = value;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool TrainingClickTargetGone
    {
        get => _flows.TrainingClickTargetGone;
        set => _flows.TrainingClickTargetGone = value;
    }

    /// <summary>
    /// A training button was submitted, but the game has not yet proved that
    /// the turn was consumed. This survives the result transition so a ratio
    /// strategy advances exactly once after training_result or the next real
    /// Career date is observed.
    /// </summary>
    public bool TrainingTurnCommitPending
    {
        get => _flows.TrainingTurnCommitPending;
        set => _flows.TrainingTurnCommitPending = value;
    }

    public string? TrainingTurnCommitType
    {
        get => _flows.TrainingTurnCommitType;
        set => _flows.TrainingTurnCommitType = value;
    }

    public int? TrainingTurnCommitTurnIndex
    {
        get => _flows.TrainingTurnCommitTurnIndex;
        set => _flows.TrainingTurnCommitTurnIndex = value;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<UraTrainingCandidate> SmartTrainingCandidates
    {
        get => _flows.SmartTrainingCandidates;
        set => _flows.SmartTrainingCandidates = value ?? [];
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? SmartTrainingChosenType
    {
        get => _flows.SmartTrainingChosenType;
        set => _flows.SmartTrainingChosenType = value;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool SmartTrainingPreviewReady
    {
        get => _flows.SmartTrainingPreviewReady;
        set => _flows.SmartTrainingPreviewReady = value;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool SmartTrainingFallbackPending
    {
        get => _flows.SmartTrainingFallbackPending;
        set => _flows.SmartTrainingFallbackPending = value;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFinale => PhaseId.Equals("finale_underway", StringComparison.OrdinalIgnoreCase);

    public string LastScreenId
    {
        get => _runtime.LastScreenId;
        set => _runtime.LastScreenId = value;
    }

    public UraObservedValue<int> Energy
    {
        get => _runtime.Energy;
        set => _runtime.Energy = value;
    }

    public UraObservedValue<CareerMood> Mood { get; set; } =
        UraObservedValueFactory.Unknown<CareerMood>();

    public UraObservedValue<int> Fans { get; set; } =
        UraObservedValueFactory.FromObservation(0, 0.2);

    public UraObservedValue<int> LastRacePlacement { get; set; } =
        UraObservedValueFactory.Unknown<int>();

    public Dictionary<string, int> RacePlacements { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> CompletedObjectiveIds { get; set; } = [];
}
