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
    string? MoodText = null,
    string? EventId = null,
    string? EventTitle = null)
{
    /// <summary>
    /// Category stamped by the observer using the active scenario profile.
    /// Directly-created observations retain the built-in catalog fallback.
    /// </summary>
    public CareerScreenKind? ClassifiedKind { get; init; }

    internal bool ConfirmedByAction { get; init; }

    public CareerScreenKind Kind => ClassifiedKind
        ?? CareerScreenClassification.Classify(ScreenId);
}

/// <summary>
/// State owned by the generic career runtime.
/// </summary>
public sealed class CareerRuntimeState
{
    public string ScenarioId { get; set; } = string.Empty;
    public string PhaseId { get; set; } = string.Empty;
    public int TurnIndex { get; set; }
    public UraObservedValue<int> Energy { get; set; } =
        UraObservedValueFactory.Unknown<int>();
    public string LastScreenId { get; set; } = "unknown";
    public UraPlannedAction LastAction { get; set; }
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
    UraActionIntent Choose(
        CareerSessionState<TScenarioState> session,
        ICareerScenarioModule<TScenarioState> scenario);
}

internal sealed record CareerFlowContext(
    LastVerifiedConnection Connection,
    UraScenarioPack Pack,
    bool PauseOnUnknownOutcome,
    UraScenarioModule Scenario,
    ICareerTrainingStrategy<UraCareerSessionState> Strategy,
    string LineupStrategy,
    UraCareerSessionState State,
    CareerObservation Observation,
    IGrassTaskLogSink? LogSink,
    CancellationToken CancellationToken,
    string EventHandling = CareerEventHandlingModes.Default,
    bool RetryFailedRaceWithAlarmClock = false,
    IReadOnlyList<int>? NormalSkillIds = null,
    Func<int, Task>? RememberNormalSkillAsync = null)
{
    public UraActionIntent ChooseAction()
    {
        var session = new CareerSessionState<UraCareerSessionState>
        {
            Runtime = State.Runtime,
            Scenario = State,
        };
        return Strategy.Choose(session, Scenario);
    }
}

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
