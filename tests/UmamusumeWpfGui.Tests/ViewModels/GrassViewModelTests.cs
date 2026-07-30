using Umamusume.CoreBridge;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.ViewModels;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class GrassViewModelTests
{
    [Fact]
    public void InitializesQueueAndKeepsExecutionCommandsDisabled()
    {
        using var log = new LogViewModel(new FakeUmaService());
        using var viewModel = new GrassViewModel(
            log,
            new FakeLocalizationService(),
            GrassTaskCatalog.CreateEmpty());

        Assert.Empty(viewModel.Tasks);
        Assert.Null(viewModel.SelectedTask);
        Assert.False(viewModel.CanAddTask);
        Assert.False(viewModel.StartCommand.CanExecute(null));
        Assert.False(viewModel.StopCommand.CanExecute(null));
    }

    [Fact]
    public void AddDoesNotCreatePlaceholderTaskWhenNoModuleIsRegistered()
    {
        using var log = new LogViewModel(new FakeUmaService());
        using var viewModel = new GrassViewModel(
            log,
            new FakeLocalizationService(),
            GrassTaskCatalog.CreateEmpty());

        viewModel.AddTaskCommand.Execute(null);

        Assert.Empty(viewModel.Tasks);
        Assert.Null(viewModel.SelectedTask);
    }

    [Fact]
    public void QueueCommandsUpdateSelectionAndSummaryForRegisteredModule()
    {
        using var log = new LogViewModel(new FakeUmaService());
        var catalog = GrassTaskCatalog.CreateEmpty();
        catalog.Register(new FakeGrassTaskModule(new GrassTaskDefinition(
            "daily-training",
            "GrassTaskDailyTraining",
            "GrassTaskDailyTrainingDescription",
            "Daily Training",
            "Training plan and daily development flow (not connected)")));
        using var viewModel = new GrassViewModel(log, new FakeLocalizationService(), catalog);
        viewModel.RequestTaskSelection = modules => modules[0];

        viewModel.AddTaskCommand.Execute(null);
        var original = viewModel.SelectedTask;
        Assert.Single(viewModel.Tasks);
        viewModel.CopyTaskCommand.Execute(null);

        Assert.Equal(2, viewModel.Tasks.Count);
        Assert.NotSame(original, viewModel.SelectedTask);
        Assert.Contains("2 enabled", viewModel.TaskCountSummary);

        viewModel.SelectedTask!.IsEnabled = false;
        Assert.Contains("1 enabled", viewModel.TaskCountSummary);

        viewModel.RemoveTaskCommand.Execute(null);
        Assert.Single(viewModel.Tasks);
    }

    [Fact]
    public void InvertCommandTogglesAllTaskSelections()
    {
        using var log = new LogViewModel(new FakeUmaService());
        using var viewModel = new GrassViewModel(
            log,
            new FakeLocalizationService(),
            GrassTaskCatalog.CreateEmpty());

        viewModel.InvertSelectionCommand.Execute(null);

        Assert.All(viewModel.Tasks, task => Assert.False(task.IsEnabled));
        Assert.Contains("0 enabled", viewModel.TaskCountSummary);
    }

    [Fact]
    public async Task StartGamePassesConfiguredPackageToLauncherWhenConnected()
    {
        using var log = new LogViewModel(new FakeUmaService());
        var localization = new FakeLocalizationService();
        var state = new ConnectionStateService();
        state.UpdateLastVerified(new LastVerifiedConnection(
            "adb.exe",
            "emulator-5554",
            "android-id",
            "35",
            1080,
            1920,
            1080,
            1920,
            DateTimeOffset.UtcNow));
        state.SetState(ConnectionState.Connected);
        var settings = new InMemorySettingsService();
        var catalog = GrassTaskCatalog.CreateEmpty();
        var launcher = new FakeGameLauncher();
        var startGameModule = new StartGameTaskModule(
            launcher,
            settings,
            localization);
        catalog.Register(startGameModule);
        using var viewModel = new GrassViewModel(
            log,
            localization,
            catalog,
            state);

        viewModel.AddTaskCommand.Execute(null);
        var taskSettings = Assert.IsType<StartGameTaskSettingsViewModel>(
            viewModel.SelectedTask!.Settings);
        taskSettings.PackageId = "com.example.umamusume";
        Assert.True(viewModel.CanStartQueue);

        viewModel.StartCommand.Execute(null);
        await launcher.Started.Task;

        Assert.Equal("adb.exe", launcher.AdbPath);
        Assert.Equal("emulator-5554", launcher.Serial);
        Assert.Equal("com.example.umamusume", launcher.PackageName);
        Assert.Equal("com.example.umamusume", settings.Load().TargetPackageIds[0]);
        Assert.True(viewModel.IsQueueRunning);
    }

    [Fact]
    public void LanguageChangedRefreshesTaskPresentation()
    {
        using var log = new LogViewModel(new FakeUmaService());
        var localization = new FakeLocalizationService();
        localization.Values["GrassTaskDailyTraining"] = "Daily Training";
        var catalog = GrassTaskCatalog.CreateEmpty();
        catalog.Register(new FakeGrassTaskModule(new GrassTaskDefinition(
            "daily-training",
            "GrassTaskDailyTraining",
            "GrassTaskDailyTrainingDescription",
            "Daily Training",
            "Training plan and daily development flow (not connected)")));
        using var viewModel = new GrassViewModel(log, localization, catalog);
        viewModel.RequestTaskSelection = modules => modules[0];
        viewModel.AddTaskCommand.Execute(null);

        localization.Values["GrassTaskDailyTraining"] = "每日训练";
        localization.SwitchLanguage("zh-CN");

        Assert.Equal("每日训练", viewModel.Tasks[0].Name);
    }

    private sealed class FakeUmaService : IUmaService
    {
        public string? CoreVersion => "test";
        public string? ResourcePath => null;

        private Action<ConnectionEvent>? _connectionEventReceived;
        private Action<BridgeDiagnostic>? _diagnosticReceived;

        public event Action<ConnectionEvent>? ConnectionEventReceived
        {
            add => _connectionEventReceived += value;
            remove => _connectionEventReceived -= value;
        }

        public event Action<BridgeDiagnostic>? DiagnosticReceived
        {
            add => _diagnosticReceived += value;
            remove => _diagnosticReceived -= value;
        }

        public Task InitializeAsync(string appBaseDir, string appDataDir, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ConnectionTerminalEvent> ConnectAsync(
            string adbPath,
            string serial,
            string profile,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ConnectionTerminalEvent>(new NotSupportedException());

        public Task CancelOperationAsync(ulong operationId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLocalizationService : ILocalizationService
    {
        public string CurrentCulture { get; private set; } = "en-US";
        public Dictionary<string, string> Values { get; } = new();

        public event EventHandler<string>? LanguageChanged;

        public void Initialize()
        {
        }

        public void SwitchLanguage(string culture)
        {
            CurrentCulture = culture;
            LanguageChanged?.Invoke(this, culture);
        }

        public string GetString(string key) => Values.TryGetValue(key, out var value) ? value : key;
    }

    private sealed class FakeGameLauncher : IGameLauncher
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? AdbPath { get; private set; }
        public string? Serial { get; private set; }
        public string? PackageName { get; private set; }

        public Task<GameLaunchResult> StartAsync(
            string adbPath,
            string serial,
            string packageName,
            CancellationToken cancellationToken = default)
        {
            AdbPath = adbPath;
            Serial = serial;
            PackageName = packageName;
            Started.TrySetResult(true);
            return Task.FromResult(new GameLaunchResult(
                true,
                true,
                "started",
                new AdbCommandResult("", "", 0, false, null)));
        }

        public Task<GameLaunchResult> StopAsync(
            string adbPath,
            string serial,
            string packageName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameLaunchResult(
                true,
                false,
                "stopped",
                new AdbCommandResult("", "", 0, false, null)));
    }

    private sealed class FakeGrassTaskModule : IGrassTaskModule
    {
        public FakeGrassTaskModule(GrassTaskDefinition definition)
        {
            Definition = definition;
        }

        public GrassTaskDefinition Definition { get; }

        public object Settings { get; } = new();

        public IGrassTaskModule CreateInstance() => new FakeGrassTaskModule(Definition);

        public bool CanExecute(GrassTaskExecutionContext context) => true;

        public Task<GrassTaskExecutionResult> ExecuteAsync(
            GrassTaskExecutionContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GrassTaskExecutionResult(true, false, "done"));

        public Task<GrassTaskExecutionResult> StopAsync(
            GrassTaskExecutionContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GrassTaskExecutionResult(true, false, "stopped"));
    }

    private sealed class InMemorySettingsService : ISettingsService
    {
        private readonly ConnectionSettings _settings = new();

        public ConnectionSettings Load() => _settings;

        public void Save(ConnectionSettings settings)
        {
            _settings.TargetPackageIds = [.. settings.TargetPackageIds];
        }
    }
}
