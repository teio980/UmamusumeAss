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
        using var log = new LogViewModel();
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
        using var log = new LogViewModel();
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
        using var log = new LogViewModel();
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
    public void TaskSettingsPage_OpensForExplicitTaskAndCanReturnToQueue()
    {
        using var log = new LogViewModel();
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
        var task = Assert.Single(viewModel.Tasks);

        Assert.False(viewModel.IsTaskSettingsPage);
        viewModel.OpenTaskSettings(task);

        Assert.True(viewModel.IsTaskSettingsPage);
        Assert.Same(task, viewModel.SelectedTask);

        viewModel.CloseTaskSettings();

        Assert.False(viewModel.IsTaskSettingsPage);
    }

    [Fact]
    public void InvertCommandTogglesAllTaskSelections()
    {
        using var log = new LogViewModel();
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
        using var log = new LogViewModel();
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
        using var log = new LogViewModel();
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
        catalog.Register(new StartGameTaskModule(settings));

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
    public void LegacyShopTaskMovesToGlobalHachimiSettings()
    {
        using var log = new LogViewModel();
        var settings = new InMemorySettingsService();
        settings.Load().TaskQueue.Add(new GrassTaskCacheItem
        {
            TaskId = "shop",
            IsEnabled = true,
            Settings = new JsonObject
            {
                ["buyShoes"] = true,
            },
        });

        using var viewModel = new GrassViewModel(
            log,
            new FakeLocalizationService(),
            GrassTaskCatalog.CreateEmpty(),
            null,
            settings);

        Assert.Empty(viewModel.Tasks);
        Assert.Empty(settings.Load().TaskQueue);
        Assert.True(settings.Load().Hachimi.Shop.BuyShoes);
    }

    [Fact]
    public void LanguageChangedRefreshesTaskPresentation()
    {
        using var log = new LogViewModel();
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
