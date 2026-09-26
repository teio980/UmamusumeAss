using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// A stable observation shared by the career runtime and scenario modules.
/// Recognition and action details remain outside the runtime state.
/// </summary>
public sealed record CareerObservation(
    string ScreenId,
    double Score,
    int? EnergyPercent = null,
    double EnergyConfidence = 0,
    string? TurnPositionText = null,
    int? TurnsToGoal = null,
    string? GoalText = null,
    int? FansToGoal = null,
    bool InfirmaryAvailable = false,
    string? MoodText = null)
{
    public CareerScreenKind Kind => CareerScreenClassification.Classify(ScreenId);
}

/// <summary>
/// A semantic decision returned by a career strategy.
/// </summary>
public sealed record CareerDecision(
    string ActionId,
    string? TargetId,
    string Reason);

/// <summary>
/// State owned by the generic career runtime.
/// </summary>
public sealed class CareerRuntimeState
{
    public string ScenarioId { get; set; } = string.Empty;
    public string PhaseId { get; set; } = string.Empty;
    public int TurnIndex { get; set; }
    public int? Energy { get; set; }
    public string LastScreenId { get; set; } = "unknown";
    public string? LastAction { get; set; }
    public bool CareerStarted { get; set; }
}

/// <summary>
/// Runtime state paired with a scenario-owned state object.
/// </summary>
public sealed class CareerSessionState<TScenarioState>
{
    public CareerRuntimeState Runtime { get; set; } = new();
    public TScenarioState Scenario { get; set; } = default!;
}

/// <summary>
/// Versioned Normal checkpoint envelope. Independent Training keeps its own
/// session and checkpoint types.
/// </summary>
public sealed class CareerCheckpoint<TScenarioState>
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Mode { get; set; } = "normal";
    public CareerRuntimeState Runtime { get; set; } = new();
    public TScenarioState Scenario { get; set; } = default!;
}

public interface ICareerScenarioModule<TScenarioState>
{
    string ScenarioId { get; }

    TScenarioState CreateInitialState();

    void Observe(
        CareerSessionState<TScenarioState> session,
        CareerObservation observation);
}

public interface ICareerTrainingStrategy<TScenarioState>
{
    CareerDecision Choose(CareerSessionState<TScenarioState> session);
}

internal sealed record CareerFlowContext(
    LastVerifiedConnection Connection,
    UraScenarioPack Pack,
    bool PauseOnUnknownOutcome,
    UraScenarioModule Scenario,
    UraDefaultStrategy Strategy,
    string LineupStrategy,
    UraCareerSessionState State,
    CareerObservation Observation,
    IGrassTaskLogSink? LogSink,
    CancellationToken CancellationToken,
    string EventHandling = CareerEventHandlingModes.Default,
    bool RetryFailedRaceWithAlarmClock = false);

internal interface ICareerFlowActionRunner
{
    Task<CareerTrainingResult?> RunAsync(
        CareerFlowContext context,
        string screenId,
        string actionId,
        HachimiPipelineRunOptions? options = null);
}

/// <summary>
/// Recognizes and handles events after a successful training action.
/// </summary>
internal interface ICareerEventHandler
{
    Task<CareerTrainingResult?> TryRecognizeAndHandleAsync(
        CareerFlowContext context);
}

internal static class CareerRuntimeResults
{
    public static CareerTrainingResult Failure(
        string message,
        string lastScreenId,
        int actionsCompleted = 0) =>
        new(false, message, actionsCompleted, lastScreenId);
}
