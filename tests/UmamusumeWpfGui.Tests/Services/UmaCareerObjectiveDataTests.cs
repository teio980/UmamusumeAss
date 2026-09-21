using System.IO;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UmaCareerObjectiveDataTests
{
    [Fact]
    public async Task Global_database_contains_character_objectives_and_deduplicated_races()
    {
        var database = await LoadDatabaseAsync();

        Assert.Equal(71, database.Trainees.Count);
        Assert.Equal(95, database.Races.Count);
        Assert.All(database.Trainees, trainee =>
        {
            Assert.NotEmpty(trainee.CareerObjectives);
            Assert.False(string.IsNullOrWhiteSpace(trainee.CareerObjectivesSourceUrl));
            Assert.All(trainee.CareerObjectives, objective =>
                Assert.NotNull(objective.Target));
        });

        var objectiveIds = database.Trainees
            .SelectMany(trainee => trainee.CareerObjectives)
            .Select(objective => objective.ObjectiveId)
            .ToArray();
        Assert.Equal(objectiveIds.Length, objectiveIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(database.Trainees.Single(item => item.TraineeId == 105201).CareerObjectives,
            objective => objective.Target.Minimum == 5000);
        Assert.Contains(database.Trainees.Single(item => item.TraineeId == 105201).CareerObjectives,
            objective => objective.RaceIds.Count == 4);
    }

    [Fact]
    public async Task Ura_module_updates_current_objective_and_race_from_observed_turn()
    {
        var database = await LoadDatabaseAsync();
        var trainee = database.Trainees.Single(item => item.TraineeId == 100101);
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            FindWorkspaceRoot(), "resource", "hachimi", "ura", "manifest.json"));
        var module = new UraScenarioModule(pack, trainee, database.Races);
        var state = module.CreateInitialState();

        Assert.Equal("career_100101_01", state.CurrentObjectiveId);
        Assert.Equal("9187", state.CurrentRaceId);

        module.ObserveScreen(
            state,
            "career_main",
            confidence: 0.99,
            energyPercent: 80,
            energyConfidence: 0.99,
            turnPositionText: "Junior Year Late Jun");

        Assert.Equal("career_100101_01", state.CurrentObjectiveId);
        Assert.Equal("9187", state.CurrentRaceId);
        Assert.True(state.HasPendingRace);

        // Simulate the target race flow returning to career_main before
        // observing the next scheduled objective.
        state.HasPendingRace = false;

        module.ObserveScreen(
            state,
            "career_main",
            confidence: 0.99,
            energyPercent: 80,
            energyConfidence: 0.99,
            turnPositionText: "Classic Year Late Jan");

        Assert.Equal("career_100101_02", state.CurrentObjectiveId);
        Assert.Equal("3009", state.CurrentRaceId);
        Assert.False(state.HasPendingRace);
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
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "resource",
                    "hachimi",
                    "ura",
                    "manifest.json")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository workspace.");
    }
}
