using System.IO;
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
