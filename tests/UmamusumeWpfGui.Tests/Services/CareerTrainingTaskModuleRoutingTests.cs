using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerTrainingTaskModuleRoutingTests
{
    [Fact]
    public async Task Normal_mode_routes_to_normal_pipeline()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Module.Settings.CareerMode = CareerTrainingTaskSettingsViewModel.NormalCareerMode;

        var result = await fixture.Module.ExecuteAsync(
            new GrassTaskExecutionContext(fixture.Connection));

        Assert.True(result.Succeeded);
        Assert.Equal("normal-ran", result.Message);
        Assert.True(fixture.Normal.RunCalled);
        Assert.False(fixture.Independent.RunCalled);
    }

    [Fact]
    public async Task Independent_mode_routes_to_independent_pipeline()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Module.Settings.CareerMode = CareerTrainingTaskSettingsViewModel.IndependentCareerMode;
        fixture.Independent.RunResult = new(
            true,
            "independent-ran",
            2,
            "home");

        var result = await fixture.Module.ExecuteAsync(
            new GrassTaskExecutionContext(fixture.Connection));

        Assert.True(result.Succeeded);
        Assert.Equal("independent-ran", result.Message);
        Assert.False(fixture.Normal.RunCalled);
        Assert.True(fixture.Independent.RunCalled);
    }

    [Fact]
    public async Task Stop_in_independent_mode_only_stops_independent_pipeline()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Module.Settings.CareerMode = CareerTrainingTaskSettingsViewModel.IndependentCareerMode;

        var result = await fixture.Module.StopAsync(
            new GrassTaskExecutionContext(fixture.Connection));

        Assert.True(result.Succeeded);
        Assert.Equal("independent-stopped", result.Message);
        Assert.False(fixture.Normal.StopCalled);
        Assert.True(fixture.Independent.StopCalled);
    }

    [Fact]
    public async Task Export_and_import_preserve_independent_agenda_skills_and_support_cards()
    {
        var fixture = await CreateFixtureAsync();
        var settings = fixture.Module.Settings;
        settings.CareerMode = CareerTrainingTaskSettingsViewModel.IndependentCareerMode;
        settings.RestartIndependentTraining = true;
        settings.IndependentTrainingFocus = "stamina";
        settings.IndependentLineupStrategy = "front";

        var agenda = settings.IndependentRaceOptions.First(item => item.IsExecutable);
        var skill = settings.IndependentSkillOptions.First(item => item.IsExecutable);
        agenda.IsSelected = true;
        skill.IsSelected = true;

        var ownCardIds = settings.FilteredSupportCardOptions
            .Take(5)
            .Select(item => item.SupportCardId)
            .ToArray();
        settings.SupportDeckMode = "selected";
        settings.SupportDeckPreset = "custom";
        settings.SupportCardIdsText = string.Join(",", ownCardIds);
        settings.FriendSupportCardId = settings.FriendSupportCardOptions.First().SupportCardId;

        var exported = fixture.Module.ExportSettings();
        var imported = (CareerTrainingTaskModule)fixture.Module.CreateInstance();
        imported.Settings.RefreshIndependentTrainingCatalog(FindSolutionRoot());
        imported.ImportSettings(exported);

        Assert.Equal(settings.CareerMode, imported.Settings.CareerMode);
        Assert.Equal(
            settings.RestartIndependentTraining,
            imported.Settings.RestartIndependentTraining);
        Assert.Equal(settings.IndependentTrainingFocus, imported.Settings.IndependentTrainingFocus);
        Assert.Equal(settings.IndependentLineupStrategy, imported.Settings.IndependentLineupStrategy);
        Assert.Equal(
            settings.ParseIndependentAgendaSelections(),
            imported.Settings.ParseIndependentAgendaSelections());
        Assert.Equal(settings.ParseIndependentSkillIds(), imported.Settings.ParseIndependentSkillIds());
        Assert.Equal(settings.SupportDeckMode, imported.Settings.SupportDeckMode);
        Assert.Equal(settings.SupportDeckPreset, imported.Settings.SupportDeckPreset);
        Assert.Equal(settings.FriendSupportCardId, imported.Settings.FriendSupportCardId);
        Assert.Equal(ownCardIds, imported.Settings.ParseSupportCardIds());
    }

    [Fact]
    public async Task Unknown_mode_is_reported_explicitly()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Module.Settings.CareerMode = "future-career-mode";

        var result = await fixture.Module.ExecuteAsync(
            new GrassTaskExecutionContext(fixture.Connection));

        Assert.False(result.Succeeded);
        Assert.Contains("Unknown Career mode 'future-career-mode'", result.Message);
        Assert.False(fixture.Normal.RunCalled);
        Assert.False(fixture.Independent.RunCalled);
    }

    [Fact]
    public async Task Independent_restart_preserves_career_and_reaches_independent_pipeline()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Module.Settings.CareerMode = CareerTrainingTaskSettingsViewModel.IndependentCareerMode;
        fixture.Module.Settings.RestartIndependentTraining = true;
        fixture.Module.Settings.ContinueExistingCareer = false;
        fixture.Independent.RunResult = new(
            true,
            "independent-ran",
            1,
            "home");

        var result = await fixture.Module.ExecuteAsync(
            new GrassTaskExecutionContext(fixture.Connection));

        Assert.True(result.Succeeded);
        Assert.True(fixture.Independent.RunCalled);
        Assert.NotNull(fixture.Independent.LastSettings);
        Assert.True(fixture.Independent.LastSettings!.ContinueExistingCareer);
        Assert.True(fixture.Independent.LastSettings.RestartIndependentTraining);
        Assert.False(fixture.Normal.RunCalled);
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var root = FindSolutionRoot();
        var database = new UmaDatabaseService();
        await database.LoadAsync(Path.Combine(root, "resource"));
        var trainee = database.Trainees.First(item => item.Available);
        var normal = new FakeCareerPipeline();
        var independent = new FakeIndependentPipeline();
        var module = new CareerTrainingTaskModule(
            new FakeLocalizationService(),
            normal,
            independent,
            database);
        module.Settings.ManifestPath = Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "manifest.json");
        module.Settings.TraineeId = trainee.TraineeId;
        module.Settings.RefreshIndependentTrainingCatalog(root);
        return new Fixture(module, normal, independent, CreateConnection());
    }

    private static LastVerifiedConnection CreateConnection() =>
        new(
            "adb",
            "emulator-5554",
            "android",
            "test",
            900,
            1600,
            900,
            1600,
            DateTimeOffset.UtcNow);

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CMakePresets.json")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed record Fixture(
        CareerTrainingTaskModule Module,
        FakeCareerPipeline Normal,
        FakeIndependentPipeline Independent,
        LastVerifiedConnection Connection);

    private sealed class FakeLocalizationService : ILocalizationService
    {
        public string CurrentCulture => "zh-CN";

        public event EventHandler<string>? LanguageChanged
        {
            add { }
            remove { }
        }

        public string GetString(string key) => key;

        public void Initialize()
        {
        }

        public void SwitchLanguage(string culture)
        {
        }
    }

    private sealed class FakeCareerPipeline : ICareerTrainingPipeline
    {
        public bool RunCalled { get; private set; }

        public bool StopCalled { get; private set; }

        public Task<CareerTrainingResult> RunAsync(
            LastVerifiedConnection connection,
            CareerTrainingSettings settings,
            IGrassTaskLogSink? logSink,
            CancellationToken cancellationToken = default)
        {
            RunCalled = true;
            return Task.FromResult(new CareerTrainingResult(true, "normal-ran", 1, "normal"));
        }

        public Task<CareerTrainingResult> StopAsync(
            LastVerifiedConnection connection,
            IGrassTaskLogSink? logSink = null,
            CancellationToken cancellationToken = default)
        {
            StopCalled = true;
            return Task.FromResult(new CareerTrainingResult(true, "normal-stopped", 0, "stop"));
        }
    }

    private sealed class FakeIndependentPipeline : IIndependentTrainingPipeline
    {
        public bool RunCalled { get; private set; }

        public IndependentTrainingSettings? LastSettings { get; private set; }

        public bool StopCalled { get; private set; }

        public IndependentTrainingResult RunResult { get; set; } =
            new(false, "independent-not-configured", 0, "test");

        public Task<IndependentTrainingResult> RunAsync(
            LastVerifiedConnection connection,
            IndependentTrainingSettings settings,
            IGrassTaskLogSink? logSink,
            CancellationToken cancellationToken = default)
        {
            RunCalled = true;
            LastSettings = settings;
            return Task.FromResult(RunResult);
        }

        public Task<IndependentTrainingResult> StopAsync(
            LastVerifiedConnection connection,
            IGrassTaskLogSink? logSink = null,
            CancellationToken cancellationToken = default)
        {
            StopCalled = true;
            return Task.FromResult(new IndependentTrainingResult(
                true,
                "independent-stopped",
                0,
                "stop"));
        }
    }
}
