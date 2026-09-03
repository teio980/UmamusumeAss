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
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CareerTrainingResult(true, "stopped", 0, "stop"));
    }

    private sealed class FakeIndependentPipeline : IIndependentTrainingPipeline
    {
        public bool RunCalled { get; private set; }

        public Task<IndependentTrainingResult> RunAsync(
            LastVerifiedConnection connection,
            IndependentTrainingSettings settings,
            IGrassTaskLogSink? logSink,
            CancellationToken cancellationToken = default)
        {
            RunCalled = true;
            throw new InvalidOperationException("Independent pipeline should not be called.");
        }

        public Task<IndependentTrainingResult> StopAsync(
            LastVerifiedConnection connection,
            IGrassTaskLogSink? logSink = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new IndependentTrainingResult(true, "stopped", 0, "stop"));
    }
}
