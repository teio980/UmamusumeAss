using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerRaceStreakPolicyTests
{
    [Fact]
    public void Resuming_without_turn_history_requires_a_non_race_break()
    {
        var state = new UraCareerSessionState();

        CareerRaceStreakPolicy.InitializeForRun(state, continueExistingCareer: true);
        Assert.Equal(2, state.ConsecutiveRaceTurns);

        CareerRaceStreakPolicy.InitializeForRun(state, continueExistingCareer: false);
        Assert.Equal(0, state.ConsecutiveRaceTurns);
    }

    [Fact]
    public void Counts_each_confirmed_race_turn_once_and_resets_after_a_non_race_turn()
    {
        var state = new UraCareerSessionState { TurnIndex = 10 };

        CareerRaceStreakPolicy.BeginTurnAction(state, UraPlannedAction.Race);
        CareerRaceStreakPolicy.BeginTurnAction(state, UraPlannedAction.Race);
        CareerRaceStreakPolicy.ConfirmTurnAdvance(state, 10);
        Assert.Equal(0, state.ConsecutiveRaceTurns);

        CareerRaceStreakPolicy.ConfirmTurnAdvance(state, 11);
        CareerRaceStreakPolicy.ConfirmTurnAdvance(state, 11);
        Assert.Equal(1, state.ConsecutiveRaceTurns);

        state.TurnIndex = 11;
        CareerRaceStreakPolicy.BeginTurnAction(state, UraPlannedAction.Race);
        CareerRaceStreakPolicy.ConfirmTurnAdvance(state, 12);
        Assert.Equal(2, state.ConsecutiveRaceTurns);

        state.TurnIndex = 12;
        CareerRaceStreakPolicy.BeginTurnAction(state, UraPlannedAction.Rest);
        CareerRaceStreakPolicy.ConfirmTurnAdvance(state, 13);
        Assert.Equal(0, state.ConsecutiveRaceTurns);
    }

    [Fact]
    public async Task Observed_main_turn_commits_the_pending_action_only_once()
    {
        var scenario = new UraScenarioModule(await LoadPackAsync(),
            useTraineeObjectives: false);
        var state = scenario.CreateInitialState();
        state.TurnIndex = 1;
        state.ConsecutiveRaceTurns = 1;
        CareerRaceStreakPolicy.BeginTurnAction(state, UraPlannedAction.Race);

        scenario.ObserveScreen(state, "career_main", 1,
            turnPositionText: "Junior Year Late Jan");
        scenario.ObserveScreen(state, "career_main", 1,
            turnPositionText: "Junior Year Late Jan");

        Assert.Equal(2, state.ConsecutiveRaceTurns);
        Assert.Null(state.PendingTurnAction);
    }

    [Theory]
    [InlineData(UraPlannedAction.Training)]
    [InlineData(UraPlannedAction.Rest)]
    public async Task Pre_debut_countdown_confirms_a_non_race_turn(
        UraPlannedAction action)
    {
        var scenario = new UraScenarioModule(await LoadPackAsync(),
            useTraineeObjectives: false);
        var state = scenario.CreateInitialState();
        CareerRaceStreakPolicy.InitializeForRun(state, continueExistingCareer: true);
        scenario.ObserveScreen(state, "career_main", 1,
            turnPositionText: "Junior Year Pre-Debut",
            turnsToGoal: 4,
            goalText: "Run in Junior Make Debut");
        CareerRaceStreakPolicy.BeginTurnAction(state, action);

        scenario.ObserveScreen(state, "career_main", 1,
            turnPositionText: "Junior Year Pre-Debut",
            turnsToGoal: 4,
            goalText: "Run in Junior Make Debut");
        Assert.Equal(2, state.ConsecutiveRaceTurns);

        scenario.ObserveScreen(state, "career_main", 1,
            turnPositionText: "Junior Year Pre-Debut",
            turnsToGoal: 3,
            goalText: "Run in Junior Make Debut");
        Assert.Equal(0, state.ConsecutiveRaceTurns);
        Assert.Null(state.PendingTurnAction);
        Assert.Null(state.PendingActionTurnsToGoal);

        scenario.ObserveScreen(state, "career_main", 1,
            turnPositionText: "Junior Year Pre-Debut",
            turnsToGoal: 3,
            goalText: "Run in Junior Make Debut");
        Assert.Equal(0, state.ConsecutiveRaceTurns);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(4)]
    [InlineData(2)]
    public async Task Pre_debut_countdown_requires_a_confirmed_single_turn_advance(
        int? observedTurnsToGoal)
    {
        var scenario = new UraScenarioModule(await LoadPackAsync(),
            useTraineeObjectives: false);
        var state = scenario.CreateInitialState();
        CareerRaceStreakPolicy.InitializeForRun(state, continueExistingCareer: true);
        scenario.ObserveScreen(state, "career_main", 1,
            turnPositionText: "Junior Year Pre-Debut",
            turnsToGoal: 4);
        CareerRaceStreakPolicy.BeginTurnAction(state, UraPlannedAction.Training);

        scenario.ObserveScreen(state, "career_main", 1,
            turnPositionText: "Junior Year Pre-Debut",
            turnsToGoal: observedTurnsToGoal);

        Assert.Equal(2, state.ConsecutiveRaceTurns);
        Assert.Equal(UraPlannedAction.Training, state.PendingTurnAction);
    }

    [Fact]
    public async Task Run_in_debut_goal_is_allowed_despite_unknown_resumed_race_history()
    {
        var scenario = new UraScenarioModule(await LoadPackAsync(),
            useTraineeObjectives: false);
        var state = scenario.CreateInitialState();
        CareerRaceStreakPolicy.InitializeForRun(state, continueExistingCareer: true);
        scenario.ObserveScreen(state, "race_day", 1,
            goalText: "Run in Junior Make Debut");
        scenario.ObserveScreen(state, "race_list", 1,
            goalText: "Run in Junior Make Debut");

        Assert.Equal(CareerGoalTextParser.Race, state.ObservedGoalKind);
        Assert.False(CareerRaceStreakPolicy.ShouldDeferRace(scenario, state));

        var actions = new RecordingActions();
        var context = new CareerFlowContext(null!, null!, true, scenario,
            new UraDefaultStrategy(), string.Empty, state,
            new CareerObservation("race_list", 1), null, CancellationToken.None);
        Assert.Null(await new CareerRaceFlow(ThrowingRuntime.Create(), actions)
            .HandleAsync(context));
        Assert.Equal(["race_list.race.recommended_entry"], actions.Calls);
    }

    [Fact]
    public async Task Fans_race_is_deferred_after_two_races_but_runs_at_the_deadline()
    {
        var scenario = new UraScenarioModule(await LoadPackAsync());
        var state = scenario.CreateInitialState();
        state.ConsecutiveRaceTurns = 2;
        state.HasPendingRace = true;
        state.ObservedGoalKind = CareerGoalTextParser.Fans;
        state.FansToGoal = 1739;
        state.TurnsToGoal = 9;
        state.Energy = UraObservedValueFactory.FromObservation(30, 1);

        Assert.Equal(UraPlannedAction.Rest,
            new UraDefaultStrategy().ChooseTurnAction(scenario, state).Action);

        state.TurnsToGoal = 1;
        Assert.Equal(UraPlannedAction.Race,
            new UraDefaultStrategy().ChooseTurnAction(scenario, state).Action);
    }

    [Fact]
    public async Task A_current_turn_race_entry_is_not_mistaken_for_a_previous_race()
    {
        var scenario = new UraScenarioModule(await LoadPackAsync());
        var state = scenario.CreateInitialState();
        state.ConsecutiveRaceTurns = 1;
        state.HasPendingRace = true;
        state.ObservedGoalKind = CareerGoalTextParser.Fans;
        state.TurnsToGoal = 8;
        state.TurnIndex = 20;

        CareerRaceStreakPolicy.BeginTurnAction(state, UraPlannedAction.Race);
        Assert.False(CareerRaceStreakPolicy.ShouldDeferRace(scenario, state));

        var actions = new RecordingActions();
        var context = new CareerFlowContext(null!, null!, true, scenario,
            new UraDefaultStrategy(), string.Empty, state,
            new CareerObservation("race_list", 1), null, CancellationToken.None);
        Assert.Null(await new CareerRaceFlow(ThrowingRuntime.Create(), actions)
            .HandleAsync(context));
        Assert.Equal(["race_list.race.recommended_entry"], actions.Calls);

        state.RaceReplayFlowCompleted = true;
        Assert.True(CareerRaceStreakPolicy.ShouldDeferRace(scenario, state));
    }

    [Fact]
    public async Task Graded_race_is_deferred_only_when_later_races_can_finish_the_goal()
    {
        var schedule = new CareerRaceGradeSchedule(
        [
            Race("06_01"),
            Race("07_01"),
        ]);
        var scenario = new UraScenarioModule(await LoadPackAsync(),
            useTraineeObjectives: false, raceGradeSchedule: schedule);
        var state = scenario.CreateInitialState();
        state.ConsecutiveRaceTurns = 2;
        state.HasPendingRace = true;
        state.ObservedGoalKind = CareerGoalTextParser.GradeRaceCount;
        state.TurnIndex = 59;
        state.TurnIndexSource = UraStateSource.Observed;
        state.TurnsToGoal = 4;
        state.TargetRaceGrade = "G1";
        state.GradeRaceTimesLeft = 1;

        Assert.Equal(UraPlannedAction.Training,
            new UraDefaultStrategy().ChooseTurnAction(scenario, state).Action);

        state.GradeRaceTimesLeft = 2;
        Assert.Equal(UraPlannedAction.Race,
            new UraDefaultStrategy().ChooseTurnAction(scenario, state).Action);

        state.TurnIndexSource = UraStateSource.Unknown;
        state.GradeRaceTimesLeft = 1;
        Assert.Equal(UraPlannedAction.Training,
            new UraDefaultStrategy().ChooseTurnAction(scenario, state).Action);

        var deadlineOnlySchedule = new CareerRaceGradeSchedule(
        [
            Race("06_01"),
            Race("08_01"),
        ]);
        var deadlineOnlyScenario = new UraScenarioModule(await LoadPackAsync(),
            useTraineeObjectives: false, raceGradeSchedule: deadlineOnlySchedule);
        state.TurnIndexSource = UraStateSource.Observed;
        Assert.Equal(UraPlannedAction.Race,
            new UraDefaultStrategy().ChooseTurnAction(deadlineOnlyScenario, state).Action);
    }

    [Theory]
    [InlineData(CareerGoalTextParser.Race, "career", UraPlannedAction.Race)]
    [InlineData(CareerGoalTextParser.Unknown, "finale_underway", UraPlannedAction.FinaleRace)]
    public async Task Required_races_take_priority_even_after_two_races(
        string goalKind,
        string phase,
        UraPlannedAction expected)
    {
        var scenario = new UraScenarioModule(await LoadPackAsync());
        var state = scenario.CreateInitialState();
        state.ConsecutiveRaceTurns = 2;
        state.HasPendingRace = true;
        state.ObservedGoalKind = goalKind;
        state.PhaseId = phase;

        var decision = new UraDefaultStrategy().ChooseTurnAction(scenario, state);

        Assert.Equal(expected, decision.Action);
        Assert.Contains("two consecutive races", decision.Reason);
    }

    [Fact]
    public async Task Unknown_resumed_streak_blocks_an_optional_race_before_a_list_click()
    {
        var scenario = new UraScenarioModule(await LoadPackAsync());
        var state = scenario.CreateInitialState();
        state.ConsecutiveRaceTurns = 2;
        state.HasPendingRace = true;
        state.ObservedGoalKind = CareerGoalTextParser.Fans;
        state.TurnsToGoal = null;
        var actions = new RecordingActions();
        var flow = new CareerRaceFlow(ThrowingRuntime.Create(), actions);
        var context = new CareerFlowContext(null!, null!, true, scenario,
            new UraDefaultStrategy(), string.Empty, state,
            new CareerObservation("race_list", 1), null, CancellationToken.None);

        var result = await flow.HandleAsync(context);

        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Empty(actions.Calls);
    }

    private static IndependentTrainingRace Race(string turn) =>
        new("test race", "G1", "third year", turn, "Turf", "", "Mile", "1600",
            IsGameAvailable: true, GameOrder: 0);

    private static async Task<UraScenarioPack> LoadPackAsync() =>
        await UraScenarioPackLoader.LoadAsync(Path.Combine(
            FindWorkspaceRoot(), "resource", "hachimi", "ura", "manifest.json"));

    private static string FindWorkspaceRoot([CallerFilePath] string sourceFile = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "resource", "hachimi", "ura", "manifest.json")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate URA resources.");
    }

    private sealed class RecordingActions : ICareerFlowActionRunner
    {
        public List<string> Calls { get; } = [];

        public Task<CareerTrainingResult?> RunAsync(
            CareerFlowContext context,
            string screenId,
            string actionId,
            HachimiPipelineRunOptions? options = null)
        {
            Calls.Add($"{screenId}.{actionId}");
            return Task.FromResult<CareerTrainingResult?>(null);
        }
    }

    public class ThrowingRuntime : DispatchProxy
    {
        public static IVisualPipelineRuntime Create() =>
            DispatchProxy.Create<IVisualPipelineRuntime, ThrowingRuntime>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"Unexpected runtime call: {targetMethod?.Name}.");
    }
}
