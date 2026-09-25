using System.IO;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraScenarioModuleTests
{
    [Fact]
    public async Task DebutResultAdvancesFansObjectiveAndResolvesNextRaceFromData()
    {
        var pack = await LoadPackAsync();
        var module = new UraScenarioModule(pack);
        var state = module.CreateInitialState();

        Assert.Equal("debut_race", state.CurrentObjectiveId);
        Assert.Equal("junior_debut", module.CurrentRace(state)?.RaceId);

        module.ApplyRaceResult(state, 1, 0.99);

        Assert.Contains("debut_race", state.CompletedObjectiveIds);
        Assert.Contains("fans_3000", state.CompletedObjectiveIds);
        Assert.Equal("nhk_mile_cup", state.CurrentObjectiveId);
        Assert.Equal("nhk_mile_cup", module.CurrentRace(state)?.RaceId);
    }

    [Fact]
    public async Task RetryableRaceDoesNotAdvanceObjectiveUntilResultIsConfirmed()
    {
        var pack = await LoadPackAsync();
        var module = new UraScenarioModule(pack);
        var state = module.CreateInitialState();
        state.CurrentObjectiveId = "senior_arima_top3";
        state.CurrentRaceId = "arima_kinen_goal";

        module.ApplyRaceResult(state, 4, 0.98);

        Assert.Equal("senior_arima_top3", state.CurrentObjectiveId);
        Assert.Equal(1, state.RetryCount);
        Assert.True(state.HasPendingRace);

        module.ApplyRaceResult(state, 1, 0.98);

        Assert.Equal("g1_top3_twice", state.CurrentObjectiveId);
        Assert.Equal("kawasaki_kinen", state.CurrentRaceId);
        Assert.Equal(0, state.RetryCount);
    }

    [Fact]
    public async Task FinaleStagesAdvanceSequentiallyAndFinishOnlyAfterFinals()
    {
        var pack = await LoadPackAsync();
        var module = new UraScenarioModule(pack);
        var state = module.CreateInitialState();
        state.PhaseId = "finale_underway";
        state.FinaleStageIndex = 0;
        state.CurrentObjectiveId = "ura_finale_qualifier";
        state.CurrentRaceId = "ura_finale_qualifier";
        state.HasPendingRace = true;

        module.ApplyRaceResult(state, 1, 0.99);
        Assert.Equal("ura_finale_semifinal", state.CurrentObjectiveId);
        Assert.False(state.IsCompleted);

        module.ApplyRaceResult(state, 1, 0.99);
        Assert.Equal("ura_finale_finals", state.CurrentObjectiveId);
        Assert.False(state.IsCompleted);

        module.ApplyRaceResult(state, 1, 0.99);
        Assert.True(state.IsCompleted);
        Assert.Equal("finished", state.PhaseId);
    }

    [Fact]
    public async Task CareerStartedIsSetOnlyAfterCareerMainIsObserved()
    {
        var pack = await LoadPackAsync();
        var module = new UraScenarioModule(pack);
        var state = module.CreateInitialState();

        module.ObserveScreen(state, "home", 0.99);
        Assert.False(state.CareerStarted);

        module.ObserveScreen(state, "career_main", 0.99);
        Assert.True(state.CareerStarted);
    }

    [Theory]
    [InlineData("default-speed-medium", "speed")]
    [InlineData("default-stamina-medium", "stamina")]
    [InlineData("default-power-medium", "power")]
    [InlineData("default-guts-medium", "guts")]
    [InlineData("default-wit-medium", "wit")]
    public async Task Registered_normal_strategies_select_their_configured_training_type(
        string strategyId,
        string expectedTrainingType)
    {
        var pack = await LoadPackAsync();
        var module = new UraScenarioModule(pack);
        var state = module.CreateInitialState();
        var strategy = UraStrategyRegistry.Create(strategyId);

        var decision = strategy.ChooseTurnAction(module, state);

        Assert.Equal(UraPlannedAction.Training, decision.Action);
        Assert.Equal(expectedTrainingType, decision.TargetId);
    }

    [Fact]
    public async Task G1_count_goal_only_requests_a_race_on_a_catalog_g1_turn()
    {
        var pack = await LoadPackAsync();
        var schedule = new CareerRaceGradeSchedule(
            IndependentTrainingCatalog.Load(FindWorkspaceRoot()).Races);
        var module = new UraScenarioModule(
            pack,
            useTraineeObjectives: false,
            raceGradeSchedule: schedule);
        var state = module.CreateInitialState();
        const string goal = "In G1, place within the top 3 2 time(s) Progress 2 time(s) left";

        Assert.True(schedule.HasData);
        Assert.False(schedule.HasQualifyingFirstCard(55, "G1"));
        Assert.True(schedule.HasQualifyingFirstCard(56, "G1"));
        Assert.True(schedule.HasQualifyingFirstCard(44, "G1"));
        module.ObserveScreen(state, "career_main", 0.98,
            turnPositionText: "Senior Year Early Apr", turnsToGoal: 6, goalText: goal);
        Assert.False(state.HasPendingRace);
        Assert.Equal(2, state.GradeRaceTimesLeft);
        Assert.Equal("G1", state.TargetRaceGrade);

        module.ObserveScreen(state, "career_main", 0.98,
            turnPositionText: "Senior Year Late Apr", turnsToGoal: 5, goalText: goal);
        Assert.True(state.HasPendingRace);
        Assert.Equal(UraPlannedAction.Race,
            new UraDefaultStrategy().ChooseTurnAction(module, state).Action);

        state.LastAction = UraPlannedAction.Race;
        state.GradeRaceStartedTurnIndex = state.TurnIndex;
        state.GoalCompletionProbeArmed = true;
        module.ObserveScreen(state, "career_main", 0.98,
            turnPositionText: "Senior Year Early May", turnsToGoal: 4,
            goalText: "In G1, place within the top 3 2 time(s) Progress 1 time(s) left");
        Assert.False(state.GoalCompletionProbeArmed);
        Assert.True(state.HasPendingRace);

        module.ObserveScreen(state, "career_main", 0.98,
            turnPositionText: "Senior Year Early May", turnsToGoal: 4,
            goalText: "In G1, place within the top 3 2 time(s) Progress 0 time(s) left");
        Assert.False(state.HasPendingRace);
    }

    [Fact]
    public async Task Oguri_count_goal_matches_when_date_ocr_joins_senior_and_year()
    {
        var pack = await LoadPackAsync();
        var module = new UraScenarioModule(pack, useTraineeObjectives: false);
        var state = module.CreateInitialState();

        module.ObserveScreen(state, "career_main", 1,
            turnPositionText: "SeniorYear Early Jan",
            turnsToGoal: 12,
            goalText: "In GI , place within the top 3 2 time(s) Detai • 2 time(s) left Progress");

        Assert.Equal(UraStateSource.Observed, state.TurnIndexSource);
        Assert.Equal(49, state.TurnIndex);
        Assert.Equal(CareerGoalTextParser.GradeRaceCount, state.ObservedGoalKind);
        Assert.Equal(2, state.GradeRaceTimesLeft);
        Assert.Equal("G1", state.TargetRaceGrade);
    }

    [Fact]
    public async Task Goal_achieved_on_career_main_resumes_normal_turn_actions()
    {
        var pack = await LoadPackAsync();
        var schedule = new CareerRaceGradeSchedule(
            IndependentTrainingCatalog.Load(FindWorkspaceRoot()).Races);
        var module = new UraScenarioModule(pack,
            useTraineeObjectives: false,
            raceGradeSchedule: schedule);
        var state = module.CreateInitialState();
        state.LastAction = UraPlannedAction.Race;
        state.GoalCompletionProbeArmed = true;
        state.HasPendingRace = true;

        module.ObserveScreen(state, "career_main", 1,
            energyPercent: 100,
            turnPositionText: "Senior Year Early Mar",
            turnsToGoal: 8,
            goalText: "In G1, place within the top 3 2 time(s) Goal Achieved!");

        Assert.Equal(CareerGoalTextParser.Completed, state.ObservedGoalKind);
        Assert.Null(state.GradeRaceTimesLeft);
        Assert.False(state.HasPendingRace);
        Assert.False(state.GoalCompletionProbeArmed);
        Assert.Equal(UraPlannedAction.Training,
            new UraDefaultStrategy().ChooseTurnAction(module, state).Action);
    }

    [Fact]
    public async Task Live_ocr_gi_goal_on_senior_early_jun_selects_a_g1_race()
    {
        var pack = await LoadPackAsync();
        var schedule = new CareerRaceGradeSchedule(
            IndependentTrainingCatalog.Load(FindWorkspaceRoot()).Races);
        var module = new UraScenarioModule(pack,
            useTraineeObjectives: false,
            raceGradeSchedule: schedule);
        var state = module.CreateInitialState();

        module.ObserveScreen(state, "career_main", 1,
            energyPercent: 50,
            turnPositionText: "Senior Year Early Jun",
            turnsToGoal: 2,
            goalText: "In GI , place within the top 3 2 time(s) Detai <JIV2 time(s) left Progress");

        Assert.Equal(CareerGoalTextParser.GradeRaceCount, state.ObservedGoalKind);
        Assert.Equal(59, state.TurnIndex);
        Assert.Equal(2, state.GradeRaceTimesLeft);
        Assert.Equal("G1", state.TargetRaceGrade);
        Assert.True(state.HasPendingRace);
        Assert.Equal(UraPlannedAction.Race,
            new UraDefaultStrategy().ChooseTurnAction(module, state).Action);
    }

    [Fact]
    public async Task Race_count_without_visible_grade_does_not_use_trainee_data()
    {
        var pack = await LoadPackAsync();
        var database = await LoadDatabaseAsync();
        var trainee = database.Trainees.Single(item => item.TraineeId == 101901);
        var schedule = new CareerRaceGradeSchedule(
            IndependentTrainingCatalog.Load(FindWorkspaceRoot()).Races);
        var module = new UraScenarioModule(pack, trainee, database.Races,
            useTraineeObjectives: false,
            raceGradeSchedule: schedule);
        var state = module.CreateInitialState();

        module.ObserveScreen(state, "career_main", 0.98,
            turnPositionText: "Senior Year Early Jun",
            turnsToGoal: 2,
            goalText: "Place within top 3. Progress 3 time(s) left");

        Assert.Equal(CareerGoalTextParser.Race, state.ObservedGoalKind);
        Assert.Null(state.TargetRaceGrade);
        Assert.Equal(3, state.GradeRaceTimesLeft);
        Assert.False(state.HasPendingRace);
    }

    [Fact]
    public async Task G2_count_goal_uses_a_g2_date_without_trainee_data()
    {
        var pack = await LoadPackAsync();
        var schedule = new CareerRaceGradeSchedule(
            IndependentTrainingCatalog.Load(FindWorkspaceRoot()).Races);
        var module = new UraScenarioModule(pack,
            useTraineeObjectives: false,
            raceGradeSchedule: schedule);
        var state = module.CreateInitialState();

        module.ObserveScreen(state, "career_main", 0.98,
            turnPositionText: "Senior Year Early Jan",
            turnsToGoal: 10,
            goalText: "Place within top 3 in 4 GII races. Progress 4 time(s) left");

        Assert.Equal("G2", state.TargetRaceGrade);
        Assert.True(schedule.HasQualifyingFirstCard(state.TurnIndex, "G2"));
        Assert.False(schedule.HasQualifyingFirstCard(state.TurnIndex, "G1"));
        Assert.True(state.HasPendingRace);
    }

    [Fact]
    public async Task G3_count_goal_can_use_a_g2_date_without_trainee_data()
    {
        var pack = await LoadPackAsync();
        var schedule = new CareerRaceGradeSchedule(
            IndependentTrainingCatalog.Load(FindWorkspaceRoot()).Races);
        var module = new UraScenarioModule(pack,
            useTraineeObjectives: false,
            raceGradeSchedule: schedule);
        var state = module.CreateInitialState();

        module.ObserveScreen(state, "career_main", 0.98,
            turnPositionText: "Senior Year Early Mar",
            turnsToGoal: 10,
            goalText: "Place within top 3 in 4 GIII races. Progress 4 time(s) left");

        Assert.Equal("G3", state.TargetRaceGrade);
        Assert.True(schedule.HasQualifyingFirstCard(state.TurnIndex, "G3"));
        Assert.False(schedule.HasQualifyingFirstCard(state.TurnIndex, "G1"));
        Assert.True(state.HasPendingRace);
    }

    [Fact]
    public async Task Predicted_scenario_event_does_not_force_an_event_action()
    {
        var pack = await LoadPackAsync();
        var module = new UraScenarioModule(pack);
        var state = module.CreateInitialState();
        state.HasScenarioEvent = true;
        var strategy = UraStrategyRegistry.Create("default-speed-medium");

        var decision = strategy.ChooseTurnAction(module, state);

        Assert.Equal(UraPlannedAction.Training, decision.Action);
        Assert.Equal("speed", decision.TargetId);
    }

    private static async Task<UraScenarioPack> LoadPackAsync()
    {
        var root = FindWorkspaceRoot();
        return await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
    }

    private static async Task<UmaDatabaseService> LoadDatabaseAsync()
    {
        var database = new UmaDatabaseService();
        await database.LoadAsync(Path.Combine(FindWorkspaceRoot(), "resource"));
        return database;
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "resource", "hachimi", "ura", "manifest.json")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository workspace.");
    }
}
