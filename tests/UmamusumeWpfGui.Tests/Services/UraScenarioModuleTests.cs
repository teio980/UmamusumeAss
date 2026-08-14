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
