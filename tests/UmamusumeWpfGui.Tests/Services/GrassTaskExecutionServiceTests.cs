using System.Text.Json.Nodes;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class GrassTaskExecutionServiceTests
{
    private static readonly string[] ExpectedCapturedTaskIds = ["mail-collection", "daily-race"];

    [Fact]
    public async Task CapturesAnImmutableQueueAndSubmitsItToCoreOnce()
    {
        var settings = new MemorySettingsService();
        settings.Value.Hachimi.Shop.BuyAlarmClock = true;
        settings.Value.TargetPackageIds.Add("saved.package");
        settings.Value.TargetActivityName = "saved.Activity";
        var first = new RecordingModule("mail-collection");
        var second = new RecordingModule("daily-race")
        {
            Configuration = new JsonObject { ["raceCount"] = 2 },
        };
        var native = new RecordingUmaService();
        var executor = new NativeGrassTaskExecutor(native, settings);
        var service = new GrassTaskExecutionService(executor, localizationService: new ReplayLocalizationService());

        IGrassTaskModule[] modules = [first, second];
        var requests = service.Capture(modules);
        second.Configuration["raceCount"] = 8;
        settings.Value.Hachimi.Shop.BuyAlarmClock = false;
        settings.Value.TargetPackageIds[0] = "changed.package";
        settings.Value.TargetActivityName = "changed.Activity";
        var events = new List<GrassTaskQueueEvent>();
        var progress = new List<GrassTaskExecutionProgress>();
        var semanticLogs = new List<(string Phase, string Message, HachimiTaskLogEventKind Kind)>();

        var result = await service.RunAsync(requests, index => new(
            null,
            TaskLogSink: index == 1 ? new RecordingTaskLogSink(semanticLogs) : null,
            ReportProgress: progress.Add), events.Add);

        Assert.True(result.Succeeded);
        Assert.Equal(1, native.RunQueueCallCount);
        Assert.Equal(ExpectedCapturedTaskIds, native.Requests.Select(item => item.TaskId));
        var submitted = JsonNode.Parse(native.Requests[1].SettingsJson)!;
        Assert.Equal(2, submitted["raceCount"]!.GetValue<int>());
        Assert.True(submitted["shop"]!["buyAlarmClock"]!.GetValue<bool>());
        Assert.Equal("saved.package", submitted["packageId"]!.GetValue<string>());
        Assert.Equal("saved.Activity", submitted["activityName"]!.GetValue<string>());
        Assert.Contains(events, item => item.TaskIndex == 0 && item.Kind == GrassTaskQueueEventKind.Completed);
        Assert.Contains(events, item => item.TaskIndex == 1 && item.Kind == GrassTaskQueueEventKind.Completed);
        Assert.Contains(progress, item => item.TaskId == "daily-race" && item.CompletedRuns == 1 && item.TotalRuns == 2);
        Assert.Contains(semanticLogs, item => item.Message == "正在打开每日计划。"
            && item.Kind == HachimiTaskLogEventKind.Info);
        Assert.DoesNotContain(semanticLogs, item => item.Message.Contains("stale", StringComparison.Ordinal));
        Assert.Equal(0, native.SubscriberCount);
    }

    [Fact]
    public async Task CoreCancellationMarksCurrentTaskAndSkipsRestOfQueue()
    {
        var native = new RecordingUmaService { CancelQueue = true };
        var executor = new NativeGrassTaskExecutor(native, new MemorySettingsService());
        var service = new GrassTaskExecutionService(executor);
        var requests = service.Capture([
            new RecordingModule("mail-collection"),
            new RecordingModule("mission-collection"),
        ]);
        var events = new List<GrassTaskQueueEvent>();

        var result = await service.RunAsync(requests, _ => new(null), events.Add);

        Assert.False(result.Succeeded);
        Assert.Equal(GrassTaskQueueStopReason.Canceled, result.StopReason);
        Assert.Contains(events, item => item.TaskIndex == 0 && item.Kind == GrassTaskQueueEventKind.Canceled);
        Assert.Contains(events, item => item.TaskIndex == 1 && item.Kind == GrassTaskQueueEventKind.Skipped);
        Assert.Equal(1, native.RunQueueCallCount);
    }

    [Fact]
    public async Task NormalQueueCompletionWithFailedTaskIsNotReportedAsAllSucceeded()
    {
        var native = new RecordingUmaService { FailFirstTask = true };
        var executor = new NativeGrassTaskExecutor(native, new MemorySettingsService());
        var service = new GrassTaskExecutionService(executor);
        var requests = service.Capture([
            new RecordingModule("mail-collection"),
            new RecordingModule("mission-collection"),
        ]);
        var events = new List<GrassTaskQueueEvent>();

        var result = await service.RunAsync(requests, _ => new(null), events.Add);

        Assert.False(result.Succeeded);
        Assert.Equal(GrassTaskQueueStopReason.None, result.StopReason);
        Assert.Contains(events, item => item.TaskIndex == 0 && item.Kind == GrassTaskQueueEventKind.Failed);
        Assert.Contains(events, item => item.TaskIndex == 1 && item.Kind == GrassTaskQueueEventKind.Completed);
    }

    [Fact]
    public async Task DeviceNotReadyTaskFailureDoesNotMarkQueueDisconnectedAndQueueContinues()
    {
        var native = new RecordingUmaService { FirstTaskErrorCode = ConnectionErrorCode.DeviceNotReady };
        var executor = new NativeGrassTaskExecutor(native, new MemorySettingsService());
        var service = new GrassTaskExecutionService(executor);
        var requests = service.Capture([
            new RecordingModule("mail-collection"),
            new RecordingModule("mission-collection"),
        ]);
        var events = new List<GrassTaskQueueEvent>();

        var result = await service.RunAsync(requests, _ => new(null), events.Add);

        Assert.False(result.Succeeded);
        Assert.Equal(GrassTaskQueueStopReason.None, result.StopReason);
        Assert.Contains(events, item => item.TaskIndex == 0
            && item.Kind == GrassTaskQueueEventKind.Failed
            && item.Result?.ErrorCode == ConnectionErrorCode.DeviceNotReady);
        Assert.Contains(events, item => item.TaskIndex == 1 && item.Kind == GrassTaskQueueEventKind.Completed);
    }

    [Fact]
    public async Task NativeCancellationAfterConnectionLossIsReportedAsDeviceDisconnect()
    {
        var connection = new ConnectionStateService();
        connection.SetState(ConnectionState.Connected);
        var native = new RecordingUmaService { DisconnectConnectionState = connection };
        var executor = new NativeGrassTaskExecutor(native, new MemorySettingsService());
        var service = new GrassTaskExecutionService(executor, connection);
        var requests = service.Capture([
            new RecordingModule("mail-collection"),
            new RecordingModule("mission-collection"),
        ]);
        var events = new List<GrassTaskQueueEvent>();

        var result = await service.RunAsync(requests, _ => new(null), events.Add);

        Assert.False(result.Succeeded);
        Assert.Equal(GrassTaskQueueStopReason.DeviceDisconnected, result.StopReason);
        Assert.Contains(events, item => item.TaskIndex == 0
            && item.Kind == GrassTaskQueueEventKind.Failed
            && item.Result?.ErrorCode == ConnectionErrorCode.DeviceDisconnected);
        Assert.Contains(events, item => item.TaskIndex == 1
            && item.Kind == GrassTaskQueueEventKind.Skipped
            && item.Message == "DeviceDisconnected");
    }

    private sealed class RecordingModule(string taskId) : IGrassTaskModule
    {
        public GrassTaskDefinition Definition { get; } = new(taskId, taskId, taskId, taskId, taskId);
        public object Settings => Configuration;
        public JsonObject Configuration { get; set; } = new();
        public JsonObject ExportSettings() => Configuration;
        public void ImportSettings(JsonObject settings) => Configuration = (JsonObject)settings.DeepClone();
        public IGrassTaskModule CreateInstance() => new RecordingModule(taskId);
    }

    private sealed class MemorySettingsService : ISettingsService
    {
        public ConnectionSettings Value { get; private set; } = new();
        public ConnectionSettings Load() => Value;
        public void Save(ConnectionSettings settings) => Value = settings;
    }

    private sealed class RecordingUmaService : IUmaService
    {
        private Action<ConnectionEvent>? _events;
        public bool CancelQueue { get; init; }
        public bool FailFirstTask { get; init; }
        public ConnectionErrorCode? FirstTaskErrorCode { get; init; }
        public ConnectionStateService? DisconnectConnectionState { get; init; }
        public int RunQueueCallCount { get; private set; }
        public QueueTaskRequest[] Requests { get; private set; } = [];
        public int SubscriberCount { get; private set; }
        public string? CoreVersion => "test";
        public event Action<ConnectionEvent>? ConnectionEventReceived
        {
            add { _events += value; SubscriberCount++; }
            remove { _events -= value; SubscriberCount--; }
        }
        public event Action<BridgeDiagnostic>? DiagnosticReceived { add { } remove { } }
        public Task InitializeAsync(string appBaseDir, string appDataDir, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ConnectionTerminalEvent> ConnectAsync(string adbPath, string serial, string profile, CancellationToken cancellationToken = default) =>
            Task.FromException<ConnectionTerminalEvent>(new NotSupportedException());
        public Task CancelOperationAsync(ulong operationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<QueueRunResult> RunQueueAsync(IReadOnlyList<QueueTaskRequest> tasks, CancellationToken cancellationToken = default)
        {
            RunQueueCallCount++;
            Requests = tasks.Select(item => item with { }).ToArray();
            _events?.Invoke(new TaskStartedEvent(1, 1, 1, tasks[0].TaskId, 0, tasks.Count));
            if (DisconnectConnectionState is not null)
            {
                DisconnectConnectionState.SetState(ConnectionState.Disconnected);
                return Task.FromResult(new QueueRunResult(false, 0, 1, tasks.Count - 1, tasks.Count,
                    ConnectionErrorCode.Canceled, "Canceled"));
            }
            if (CancelQueue)
            {
                _events?.Invoke(new TaskFailedEvent(1, 2, 1, tasks[0].TaskId,
                    ConnectionErrorCode.Canceled, "Canceled"));
                return Task.FromResult(new QueueRunResult(false, 0, 1, tasks.Count - 1, tasks.Count,
                    ConnectionErrorCode.Canceled, "Canceled"));
            }

            var failFirstTask = FailFirstTask || FirstTaskErrorCode is not null;
            var firstTaskErrorCode = FirstTaskErrorCode ?? ConnectionErrorCode.RecognitionFailed;
            if (failFirstTask)
            {
                _events?.Invoke(new TaskFailedEvent(1, 2, 1, tasks[0].TaskId,
                    firstTaskErrorCode, "Could not match the task template"));
            }
            else
            {
                _events?.Invoke(new TaskCompletedEvent(1, 2, 1, tasks[0].TaskId, 1, 1, "done"));
            }
            _events?.Invoke(new TaskStartedEvent(1, 3, 2, tasks[1].TaskId, 1, tasks.Count));
            _events?.Invoke(new GameTaskProgressEvent(
                99, 4, tasks[1].TaskId, GameTaskEventType.TaskEntering, "dailyProgram", "Daily Race",
                0, 1, 2, true, 1, 0, 0, "stale operation", 0, 2));
            _events?.Invoke(new GameTaskProgressEvent(
                1, 5, tasks[1].TaskId, GameTaskEventType.TaskEntering, "dailyProgram", "Daily Race",
                0, 1, 2, true, 1, 0, 0, "opening", 0, 999));
            _events?.Invoke(new GameTaskProgressEvent(
                1, 6, tasks[1].TaskId, GameTaskEventType.TaskEntering, "dailyProgram", "Daily Race",
                0, 1, 2, true, 1, 0, 0, "opening", 0, 2));
            _events?.Invoke(new TaskCompletedEvent(1, 7, 2, tasks[1].TaskId, 1, 1, "done"));
            return Task.FromResult(new QueueRunResult(true,
                tasks.Count - (failFirstTask ? 1 : 0), failFirstTask ? 1 : 0, 0, tasks.Count,
                ConnectionErrorCode.Success, string.Empty));
        }

        public Task<QueueRunSnapshot> GetRunSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var interrupted = CancelQueue || DisconnectConnectionState is not null;
            var statuses = interrupted
                ? Requests.Select((_, index) => index == 0
                    ? QueueTaskStatus.Failed : QueueTaskStatus.Skipped).ToArray()
                : Requests.Select((_, index) => (FailFirstTask || FirstTaskErrorCode is not null) && index == 0
                    ? QueueTaskStatus.Failed : QueueTaskStatus.Succeeded).ToArray();
            var tasks = Requests.Select((item, index) => new QueueTaskSnapshot(
                (ulong)(index + 1), item.TaskId, statuses[index],
                interrupted
                    ? (int)ConnectionErrorCode.Canceled
                    : statuses[index] == QueueTaskStatus.Failed
                    ? (int)(FirstTaskErrorCode ?? ConnectionErrorCode.RecognitionFailed) : 0,
                statuses[index] == QueueTaskStatus.Succeeded ? "done" : string.Empty, 1, 1)).ToArray();
            return Task.FromResult(new QueueRunSnapshot(1, interrupted ? QueueRunState.Canceled : QueueRunState.Completed,
                interrupted ? 1 : null, interrupted ? Requests[0].TaskId : string.Empty,
                0, 0, tasks.Count(task => task.Status == QueueTaskStatus.Succeeded),
                tasks.Count(task => task.Status == QueueTaskStatus.Failed),
                tasks.Count(task => task.Status == QueueTaskStatus.Skipped), tasks));
        }
    }

    private sealed class RecordingTaskLogSink(
        List<(string Phase, string Message, HachimiTaskLogEventKind Kind)> entries) : IHachimiTaskLogSink
    {
        public void Add(string phase, string message, HachimiTaskLogEventKind kind = HachimiTaskLogEventKind.Info) =>
            entries.Add((phase, message, kind));
    }

    private sealed class ReplayLocalizationService : ILocalizationService
    {
        public string CurrentCulture => "zh-CN";
        public event EventHandler<string>? LanguageChanged { add { } remove { } }
        public void Initialize() { }
        public void SwitchLanguage(string culture) { }
        public string GetString(string key) => key switch
        {
            "HachimiTaskLog_Opening_Daily_Program" => "正在打开每日计划。",
            _ => key,
        };
    }
}
