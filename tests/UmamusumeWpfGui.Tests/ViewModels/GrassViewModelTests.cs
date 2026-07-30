using Umamusume.CoreBridge;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class GrassViewModelTests
{
    [Fact]
    public void InitializesQueueAndKeepsExecutionCommandsDisabled()
    {
        using var log = new LogViewModel(new FakeUmaService());
        using var viewModel = new GrassViewModel(log, new FakeLocalizationService());

        Assert.Equal(3, viewModel.Tasks.Count);
        Assert.Same(viewModel.Tasks[0], viewModel.SelectedTask);
        Assert.False(viewModel.StartCommand.CanExecute(null));
        Assert.False(viewModel.StopCommand.CanExecute(null));
    }

    [Fact]
    public void QueueCommandsUpdateSelectionAndSummary()
    {
        using var log = new LogViewModel(new FakeUmaService());
        using var viewModel = new GrassViewModel(log, new FakeLocalizationService());

        var original = viewModel.SelectedTask;
        viewModel.CopyTaskCommand.Execute(null);

        Assert.Equal(4, viewModel.Tasks.Count);
        Assert.NotSame(original, viewModel.SelectedTask);
        Assert.Contains("4 enabled", viewModel.TaskCountSummary);

        viewModel.SelectedTask!.IsEnabled = false;
        Assert.Contains("3 enabled", viewModel.TaskCountSummary);

        viewModel.RemoveTaskCommand.Execute(null);
        Assert.Equal(3, viewModel.Tasks.Count);
    }

    [Fact]
    public void InvertCommandTogglesAllTaskSelections()
    {
        using var log = new LogViewModel(new FakeUmaService());
        using var viewModel = new GrassViewModel(log, new FakeLocalizationService());

        viewModel.InvertSelectionCommand.Execute(null);

        Assert.All(viewModel.Tasks, task => Assert.False(task.IsEnabled));
        Assert.Contains("0 enabled", viewModel.TaskCountSummary);
    }

    [Fact]
    public void LanguageChangedRefreshesTaskPresentation()
    {
        using var log = new LogViewModel(new FakeUmaService());
        var localization = new FakeLocalizationService();
        localization.Values["GrassTaskDailyTraining"] = "Daily Training";
        using var viewModel = new GrassViewModel(log, localization);

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
}
