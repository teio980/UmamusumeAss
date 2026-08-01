using System.Text.Json.Nodes;
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
    public void StartIsAvailableBeforeConnectionWhenAnEnabledTaskExists()
    {
        using var log = new LogViewModel(new FakeUmaService());
        var catalog = GrassTaskCatalog.CreateEmpty();
        catalog.Register(new FakeGrassTaskModule(new GrassTaskDefinition(
            "daily-training",
            "GrassTaskDailyTraining",
            "GrassTaskDailyTrainingDescription",
            "Daily Training",
            "Training plan and daily development flow (not connected)")));
        var state = new ConnectionStateService();
        using var viewModel = new GrassViewModel(
            log,
            new FakeLocalizationService(),
            catalog,
            state);
        viewModel.AddTaskCommand.Execute(null);

        Assert.True(viewModel.CanStartQueue);
        Assert.True(viewModel.StartCommand.CanExecute(null));
    }

    [Fact]
    public void TaskQueueRestoresOrderEnabledStateAndModuleSettingsFromSettingsCache()
    {
        using var log = new LogViewModel(new FakeUmaService());
        var settings = new InMemorySettingsService();
        settings.Load().TaskQueue.Add(new GrassTaskCacheItem
        {
            TaskId = "start-game",
            IsEnabled = false,
            Settings = new JsonObject
            {
                ["packageId"] = "com.cached.umamusume",
                ["activityName"] = "com.cached.umamusume/com.cached.MainActivity",
            },
        });
        var catalog = GrassTaskCatalog.CreateEmpty();
        catalog.Register(new StartGameTaskModule(
            new FakeGameLauncher(),
            settings,
            new FakeLocalizationService()));

        using var viewModel = new GrassViewModel(
            log,
            new FakeLocalizationService(),
            catalog,
            null,
            settings);

        var task = Assert.Single(viewModel.Tasks);
        var taskSettings = Assert.IsType<StartGameTaskSettingsViewModel>(task.Settings);
        Assert.False(task.IsEnabled);
        Assert.Equal("com.cached.umamusume", taskSettings.PackageId);
        Assert.Equal(
            "com.cached.umamusume/com.cached.MainActivity",
            taskSettings.ActivityName);

        task.IsEnabled = true;

        Assert.True(settings.Load().TaskQueue[0].IsEnabled);
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
        taskSettings.ActivityName = "com.example.umamusume/com.example.MainActivity";
        Assert.True(viewModel.CanStartQueue);

        viewModel.StartCommand.Execute(null);
        await launcher.Started.Task;

        Assert.Equal("adb.exe", launcher.AdbPath);
        Assert.Equal("emulator-5554", launcher.Serial);
        Assert.Equal("com.example.umamusume", launcher.PackageName);
        Assert.Equal(
            "com.example.umamusume/com.example.MainActivity",
            launcher.ActivityName);
        Assert.Equal("com.example.umamusume", settings.Load().TargetPackageIds[0]);
        Assert.Equal(
            "com.example.umamusume/com.example.MainActivity",
            settings.Load().TargetActivityName);
        while (viewModel.IsQueueOperationInProgress)
            await Task.Delay(10);

        Assert.False(viewModel.IsQueueRunning);
        Assert.True(viewModel.StartCommand.CanExecute(null));
        Assert.NotEmpty(viewModel.ScriptLogs);
        Assert.Contains(
            viewModel.ScriptLogs,
            entry => entry.Type == "Start game" && entry.Details == "Game process detected");
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task StopCancelsTheScriptWithoutCallingTaskStopOrClosingTheGame()
    {
        using var log = new LogViewModel(new FakeUmaService());
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

        var catalog = GrassTaskCatalog.CreateEmpty();
        catalog.Register(new BlockingGrassTaskModule());
        using var viewModel = new GrassViewModel(
            log,
            new FakeLocalizationService(),
            catalog,
            state);

        viewModel.AddTaskCommand.Execute(null);
        var taskModule = Assert.IsType<BlockingGrassTaskModule>(
            viewModel.SelectedTask!.Module);

        viewModel.StartCommand.Execute(null);
        await taskModule.Started.Task;

        Assert.True(viewModel.IsQueueRunning);
        Assert.True(viewModel.StopCommand.CanExecute(null));

        viewModel.StopCommand.Execute(null);
        while (viewModel.IsQueueOperationInProgress)
            await Task.Delay(10);

        Assert.Equal(0, taskModule.StopCallCount);
        Assert.False(viewModel.IsQueueRunning);
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
        public string? ActivityName { get; private set; }

        public Task<GameLaunchResult> StartAsync(
            string adbPath,
            string serial,
            string packageName,
            CancellationToken cancellationToken = default) =>
            StartAsync(adbPath, serial, packageName, null, cancellationToken);

        public Task<GameLaunchResult> StartAsync(
            string adbPath,
            string serial,
            string packageName,
            string? activityName,
            CancellationToken cancellationToken = default)
        {
            AdbPath = adbPath;
            Serial = serial;
            PackageName = packageName;
            ActivityName = activityName;
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

        public JsonObject ExportSettings() => new();

        public void ImportSettings(JsonObject settings)
        {
        }

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

    private sealed class BlockingGrassTaskModule : IGrassTaskModule
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCallCount { get; private set; }

        public GrassTaskDefinition Definition { get; } = new(
            "blocking-task",
            "BlockingTask",
            "BlockingTaskDescription",
            "Blocking task",
            "A task that waits for cancellation");

        public object Settings { get; } = new();

        public JsonObject ExportSettings() => new();

        public void ImportSettings(JsonObject settings)
        {
        }

        public IGrassTaskModule CreateInstance() => new BlockingGrassTaskModule();

        public bool CanExecute(GrassTaskExecutionContext context) =>
            context.Connection is not null;

        public async Task<GrassTaskExecutionResult> ExecuteAsync(
            GrassTaskExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new GrassTaskExecutionResult(true, false, "completed");
        }

        public Task<GrassTaskExecutionResult> StopAsync(
            GrassTaskExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            StopCallCount++;
            return Task.FromResult(new GrassTaskExecutionResult(true, false, "stopped"));
        }
    }

    private sealed class InMemorySettingsService : ISettingsService
    {
        private readonly ConnectionSettings _settings = new();

        public ConnectionSettings Load() => _settings;

        public void Save(ConnectionSettings settings)
        {
            _settings.TargetPackageIds = [.. settings.TargetPackageIds];
            _settings.TargetActivityName = settings.TargetActivityName;
            _settings.TaskQueue = settings.TaskQueue
                .Select(item => new GrassTaskCacheItem
                {
                    TaskId = item.TaskId,
                    IsEnabled = item.IsEnabled,
                    Settings = item.Settings.DeepClone().AsObject(),
                })
                .ToList();
        }
    }
}
