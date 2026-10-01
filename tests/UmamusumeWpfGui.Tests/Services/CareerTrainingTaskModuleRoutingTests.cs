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

    [Theory]
    [InlineData("normal")]
    [InlineData("independent")]
    public async Task Run_count_repeats_the_selected_pipeline_and_reports_completed_runs(string mode)
    {
        var fixture = await CreateFixtureAsync();
        fixture.Module.Settings.CareerMode = mode;
        fixture.Module.Settings.RunCountText = "3";
        fixture.Independent.RunResult = new(true, "independent-ran", 2, "home");
        var progress = new List<GrassTaskExecutionProgress>();

        var result = await fixture.Module.ExecuteAsync(
            new GrassTaskExecutionContext(fixture.Connection, ReportProgress: progress.Add));

        Assert.True(result.Succeeded);
        Assert.Contains("3/3", result.Message);
        Assert.Equal(mode == "normal" ? 3 : 0, fixture.Normal.RunSettings.Count);
        Assert.Equal(mode == "independent" ? 3 : 0, fixture.Independent.RunSettings.Count);
        Assert.Equal(
            [new(0, 3), new(1, 3), new(2, 3), new(3, 3)],
            progress);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("independent")]
    public async Task A_failed_run_stops_repetition_and_does_not_increment_completed_runs(string mode)
    {
        var fixture = await CreateFixtureAsync();
        fixture.Module.Settings.CareerMode = mode;
        fixture.Module.Settings.RunCountText = "3";
        fixture.Normal.ResultFactory = run => new(run == 1, "normal-result", 0, "home");
        fixture.Independent.ResultFactory = run => new(run == 1, "independent-result", 0, "home");
        var progress = new List<GrassTaskExecutionProgress>();

        var result = await fixture.Module.ExecuteAsync(
            new GrassTaskExecutionContext(fixture.Connection, ReportProgress: progress.Add));

        Assert.False(result.Succeeded);
        Assert.Equal(mode == "normal" ? 2 : 0, fixture.Normal.RunSettings.Count);
        Assert.Equal(mode == "independent" ? 2 : 0, fixture.Independent.RunSettings.Count);
        Assert.Equal([new(0, 3), new(1, 3)], progress);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("independent")]
    public async Task Cancellation_between_runs_keeps_the_completed_count_and_stops_repetition(string mode)
    {
        var fixture = await CreateFixtureAsync();
        fixture.Module.Settings.CareerMode = mode;
        fixture.Module.Settings.RunCountText = "3";
        fixture.Independent.RunResult = new(true, "independent-ran", 2, "home");
        using var cancellation = new CancellationTokenSource();
        var progress = new List<GrassTaskExecutionProgress>();

        var result = await fixture.Module.ExecuteAsync(
            new GrassTaskExecutionContext(fixture.Connection, ReportProgress: update =>
            {
                progress.Add(update);
                if (update.CompletedRuns == 1)
                    cancellation.Cancel();
            }),
            cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Contains("canceled", result.Message);
        Assert.Equal(mode == "normal" ? 1 : 0, fixture.Normal.RunSettings.Count);
        Assert.Equal(mode == "independent" ? 1 : 0, fixture.Independent.RunSettings.Count);
        Assert.Equal([new(0, 3), new(1, 3)], progress);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("abc")]
    public async Task Invalid_run_count_does_not_start_a_pipeline(string runCount)
    {
        var fixture = await CreateFixtureAsync();
        fixture.Module.Settings.RunCountText = runCount;
        var context = new GrassTaskExecutionContext(fixture.Connection);

        Assert.False(fixture.Module.CanExecute(context));
        Assert.False(fixture.Module.Settings.IsValid);
        var result = await fixture.Module.ExecuteAsync(context);

        Assert.False(result.Succeeded);
        Assert.Contains("positive whole number", result.Message);
        Assert.False(fixture.Normal.RunCalled);
        Assert.False(fixture.Independent.RunCalled);
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
        Assert.Null(exported["restartIndependentTraining"]);
        var imported = (CareerTrainingTaskModule)fixture.Module.CreateInstance();
        imported.Settings.RefreshIndependentTrainingCatalog(FindSolutionRoot());
        imported.ImportSettings(exported);

        Assert.Equal(settings.CareerMode, imported.Settings.CareerMode);
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
        public bool RunCalled => RunSettings.Count > 0;

        public List<CareerTrainingSettings> RunSettings { get; } = [];

        public Func<int, CareerTrainingResult>? ResultFactory { get; set; }

        public bool StopCalled { get; private set; }

        public Task<CareerTrainingResult> RunAsync(
            LastVerifiedConnection connection,
            CareerTrainingSettings settings,
            IGrassTaskLogSink? logSink,
            IHachimiTaskLogSink? taskLogSink = null,
            CancellationToken cancellationToken = default)
        {
            RunSettings.Add(settings);
            return Task.FromResult(ResultFactory?.Invoke(RunSettings.Count)
                ?? new CareerTrainingResult(true, "normal-ran", 1, "normal"));
        }

        public Task<CareerTrainingResult> StopAsync(
            LastVerifiedConnection connection,
            IGrassTaskLogSink? logSink = null,
            IHachimiTaskLogSink? taskLogSink = null,
            CancellationToken cancellationToken = default)
        {
            StopCalled = true;
            return Task.FromResult(new CareerTrainingResult(true, "normal-stopped", 0, "stop"));
        }
    }

    private sealed class FakeIndependentPipeline : IIndependentTrainingPipeline
    {
        public bool RunCalled => RunSettings.Count > 0;

        public List<IndependentTrainingSettings> RunSettings { get; } = [];

        public Func<int, IndependentTrainingResult>? ResultFactory { get; set; }

        public bool StopCalled { get; private set; }

        public IndependentTrainingResult RunResult { get; set; } =
            new(false, "independent-not-configured", 0, "test");

        public Task<IndependentTrainingResult> RunAsync(
            LastVerifiedConnection connection,
            IndependentTrainingSettings settings,
            IGrassTaskLogSink? logSink,
            IHachimiTaskLogSink? taskLogSink = null,
            CancellationToken cancellationToken = default)
        {
            RunSettings.Add(settings);
            return Task.FromResult(ResultFactory?.Invoke(RunSettings.Count) ?? RunResult);
        }

        public Task<IndependentTrainingResult> StopAsync(
            LastVerifiedConnection connection,
            IGrassTaskLogSink? logSink = null,
            IHachimiTaskLogSink? taskLogSink = null,
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
