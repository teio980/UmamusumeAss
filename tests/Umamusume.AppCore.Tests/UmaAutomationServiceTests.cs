using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Services;
using Umamusume.CoreBridge;
using Xunit;

namespace Umamusume.AppCore.Tests;

public sealed class FakeUmaService : IUmaService
{
    public string? CoreVersion => "1.0.0-test";
    public DeviceSessionIdentity? ActiveDeviceIdentity { get; private set; }
    public bool Initialized { get; private set; }
    public string? LastAppBaseDirectory { get; private set; }
    public string? LastAdbPath { get; private set; }
    public string? LastSerial { get; private set; }
    public string? LastProfile { get; private set; }

    public ConnectionTerminalEvent ConnectResult { get; set; } =
        new ConnectionSucceededEvent(1, "127.0.0.1:5555", "android-1", "12", 720, 1280, 1080, 1920, DisplaySizeSource.Physical);

    public DeviceOperationEvent CaptureResult { get; set; } =
        new FrameCapturedEvent(1, 1, new DeviceFrameMetadata(1, 1, 1, 720, 1280, 0, 0, new DeviceDisplayTransform(720, 1280, 0, 720, 1280, 1, 0, 0, 720, 1280)), new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

    public TextRecognitionResult OcrResult { get; set; } =
        new TextRecognitionResult(new[] { new TextDetection("Start", new TextRecognitionRect(10, 20, 100, 30), 0.95) }, "eng", 720, 1280);

    public PipelineRunResult PipelineResult { get; set; } =
        new PipelineRunResult(true, "home_screen", 5, "Completed");

    public List<QueueTaskRequest> LastQueueRequests { get; } = [];

    public Func<IReadOnlyList<QueueTaskRequest>, CancellationToken, Task<QueueRunResult>>? QueueRunHandler { get; set; }

    public Func<string, string, Action<GameTaskProgressEvent>?, CancellationToken, Task<GameTaskRunResult>>? GameTaskRunHandler { get; set; }

    public string? LastGameTaskId { get; private set; }

    public string? LastGameTaskSettingsJson { get; private set; }

    public string? LastPackageOperationId { get; private set; }

    public QueueRunSnapshot RunSnapshot { get; set; } = new(
        null, QueueRunState.Idle, null, string.Empty, 0, 0, 0, 0, 0, Array.Empty<QueueTaskSnapshot>());

#pragma warning disable CS0067
    public event Action<ConnectionEvent>? ConnectionEventReceived;
    public event Action<BridgeDiagnostic>? DiagnosticReceived;
#pragma warning restore CS0067

    public Task InitializeAsync(string appBaseDir, string appDataDir, CancellationToken cancellationToken = default)
    {
        Initialized = true;
        LastAppBaseDirectory = appBaseDir;
        return Task.CompletedTask;
    }

    public Task<ConnectionTerminalEvent> ConnectAsync(string adbPath, string serial, string profile, CancellationToken cancellationToken = default)
    {
        LastAdbPath = adbPath;
        LastSerial = serial;
        LastProfile = profile;
        if (ConnectResult is ConnectionSucceededEvent succeeded)
            ActiveDeviceIdentity = new DeviceSessionIdentity(adbPath, serial, (ulong)succeeded.OperationId);
        else
            ActiveDeviceIdentity = null;
        return Task.FromResult(ConnectResult);
    }

    public Task<DeviceOperationEvent> CaptureAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(CaptureResult);
    }

    public TextRecognitionResult RecognizeText(RecognitionImage image, string language) => OcrResult;

    public Task<PipelineRunResult> RunPipelineAsync(string pipelineJson, string? entryTask = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(PipelineResult);
    }

    public Task<QueueRunResult> RunQueueAsync(
        IReadOnlyList<QueueTaskRequest> tasks,
        CancellationToken cancellationToken = default)
    {
        LastQueueRequests.Clear();
        LastQueueRequests.AddRange(tasks);
        return QueueRunHandler?.Invoke(tasks, cancellationToken)
            ?? Task.FromResult(new QueueRunResult(true, tasks.Count, 0, 0, tasks.Count, ConnectionErrorCode.Success, string.Empty));
    }

    public Task<QueueRunSnapshot> GetRunSnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(RunSnapshot);

    public Task<GameTaskRunResult> RunGameTaskAsync(
        string taskId,
        string settingsJson,
        Action<GameTaskProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LastGameTaskId = taskId;
        LastGameTaskSettingsJson = settingsJson;
        return GameTaskRunHandler?.Invoke(taskId, settingsJson, progress, cancellationToken)
            ?? Task.FromResult(new GameTaskRunResult(taskId, true, 1, 1, true, ConnectionErrorCode.Success, "done"));
    }

    public Task<DeviceOperationEvent> PackageOperationAsync(
        string packageId,
        string? activity,
        PackageOperationKind operation,
        CancellationToken cancellationToken = default)
    {
        LastPackageOperationId = packageId;
        return Task.FromResult<DeviceOperationEvent>(new PackageOperationCompletedEvent(
            1,
            1,
            operation,
            new PackageCommandResult(true, 0, false, false, ConnectionErrorCode.Success, string.Empty, string.Empty, string.Empty),
            true));
    }

    public void Emit(ConnectionEvent connectionEvent) => ConnectionEventReceived?.Invoke(connectionEvent);

    public Task CancelOperationAsync(ulong operationId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class UmaAutomationServiceTests
{
    [Fact]
    public async Task ConnectAsyncSetsConnectedStateOnSuccess()
    {
        var logger = new LogService();
        var connState = new ConnectionStateService();
        var fakeService = new FakeUmaService();
        var auto = new UmaAutomationService(logger, connState, fakeService);

        bool result = await auto.ConnectAsync("/bin/adb", "127.0.0.1:5555");

        Assert.True(result);
        Assert.True(auto.IsConnected);
        Assert.NotNull(auto.CurrentDevice);
        Assert.Equal("127.0.0.1:5555", auto.CurrentDevice.Serial);
        Assert.Equal(720, auto.CurrentDevice.Width);
        Assert.Equal(ConnectionState.Connected, connState.State);
    }

    [Fact]
    public async Task ConnectAsyncSetsDisconnectedStateOnFailure()
    {
        var logger = new LogService();
        var connState = new ConnectionStateService();
        var fakeService = new FakeUmaService
        {
            ConnectResult = new ConnectionFailedEvent(1, ConnectionErrorCode.DeviceOffline, "Handshake", "Device offline", 1, 1)
        };
        var auto = new UmaAutomationService(logger, connState, fakeService);

        bool result = await auto.ConnectAsync("/bin/adb", "127.0.0.1:5555");

        Assert.False(result);
        Assert.False(auto.IsConnected);
        Assert.Null(auto.CurrentDevice);
        Assert.Equal(ConnectionState.Disconnected, connState.State);
    }

    [Fact]
    public async Task DisconnectAsyncResetsConnectionState()
    {
        var logger = new LogService();
        var connState = new ConnectionStateService();
        var fakeService = new FakeUmaService();
        var auto = new UmaAutomationService(logger, connState, fakeService);

        await auto.ConnectAsync("/bin/adb", "127.0.0.1:5555");
        Assert.True(auto.IsConnected);

        await auto.DisconnectAsync();
        Assert.False(auto.IsConnected);
        Assert.Null(auto.CurrentDevice);
        Assert.Equal(ConnectionState.Disconnected, connState.State);
    }

    [Fact]
    public async Task CaptureScreenshotAsyncRequiresConnection()
    {
        var logger = new LogService();
        var connState = new ConnectionStateService();
        var fakeService = new FakeUmaService();
        var auto = new UmaAutomationService(logger, connState, fakeService);

        var result = await auto.CaptureScreenshotAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task CaptureScreenshotAsyncReturnsCapturedScreenshotWhenConnected()
    {
        var logger = new LogService();
        var connState = new ConnectionStateService();
        var fakeService = new FakeUmaService();
        var auto = new UmaAutomationService(logger, connState, fakeService);

        await auto.ConnectAsync("/bin/adb", "127.0.0.1:5555");
        var screenshot = await auto.CaptureScreenshotAsync();

        Assert.NotNull(screenshot);
        Assert.Equal(720, screenshot.Width);
        Assert.Equal(1280, screenshot.Height);
        Assert.NotEmpty(screenshot.PngBytes);
    }

    [Fact]
    public async Task RecognizeOcrAsyncInvokesOcrAndReturnsResult()
    {
        var logger = new LogService();
        var connState = new ConnectionStateService();
        var fakeService = new FakeUmaService();
        var auto = new UmaAutomationService(logger, connState, fakeService);

        var screenshot = new CapturedScreenshot(Array.Empty<byte>(), new byte[10 * 10 * 4], 10, 10, DateTime.Now);
        var ocr = await auto.RecognizeOcrAsync(screenshot, "eng");

        Assert.NotNull(ocr);
        Assert.Single(ocr.Detections);
        Assert.Equal("Start", ocr.Detections[0].Text);
    }

    [Fact]
    public async Task RunTaskQueueSubmitsWpfTaskIdsAndMirrorsCoreProgress()
    {
        var logger = new LogService();
        var connectionState = new ConnectionStateService();
        var fake = new FakeUmaService();
        fake.QueueRunHandler = (tasks, _) =>
        {
            fake.Emit(new TaskStartedEvent(44, 1, 900, "mail-collection", 0, 2));
            fake.Emit(new GameTaskProgressEvent(44, 2, "mail-collection", GameTaskEventType.TaskEntering,
                "home", "mail", 0, 1, 2, true, 0.9, 10, 20, "Opening mailbox", 10, 900));
            fake.Emit(new TaskCompletedEvent(44, 3, 900, "mail-collection", 1, 1, "done"));
            fake.Emit(new TaskStartedEvent(44, 4, 901, "daily-race", 1, 2));
            fake.Emit(new TaskCompletedEvent(44, 5, 901, "daily-race", 1, 1, "done"));
            return Task.FromResult(new QueueRunResult(true, 2, 0, 0, tasks.Count,
                ConnectionErrorCode.Success, string.Empty));
        };
        fake.RunSnapshot = new QueueRunSnapshot(44, QueueRunState.Completed, null, string.Empty, 0, 0, 2, 0, 0,
        [
            new QueueTaskSnapshot(900, "mail-collection", QueueTaskStatus.Succeeded, 0, "done", 1, 1),
            new QueueTaskSnapshot(901, "daily-race", QueueTaskStatus.Succeeded, 0, "done", 1, 1),
        ]);
        var settings = new ConnectionSettings
        {
            TargetActivityName = "game.Activity",
        };
        settings.TargetPackageIds.Add("custom.game");
        settings.Hachimi.Shop.BuyAlarmClock = true;
        var automation = new UmaAutomationService(logger, connectionState, fake, new MemorySettingsService(settings));
        var updates = new List<UmaTaskQueueEvent>();

        var result = await automation.RunTaskQueueAsync(
        [
            new UmaTaskRequest("mail-collection", "{\"definitionPath\":\"resource/hachimi/pipelines/mail_collection.json\"}"),
            new UmaTaskRequest("daily-race"),
        ], updates.Add);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.SucceededTasks);
        Assert.Collection(fake.LastQueueRequests,
            request => Assert.Equal("mail-collection", request.TaskId),
            request => Assert.Equal("daily-race", request.TaskId));
        Assert.Contains(updates, update => update.Kind == UmaTaskQueueEventKind.Started && update.TaskIndex == 0);
        Assert.Contains(updates, update => update.Kind == UmaTaskQueueEventKind.Progress
            && update.Message == "Opening mailbox" && update.TaskId == "mail-collection");
        Assert.Contains(updates, update => update.Kind == UmaTaskQueueEventKind.Completed && update.TaskId == "daily-race");

        var mailSettings = UmaTaskSettingsSerializer.Deserialize(fake.LastQueueRequests[0].SettingsJson);
        Assert.True(Path.IsPathFullyQualified(mailSettings["definitionPath"]!.GetValue<string>()));
        Assert.Equal("custom.game", mailSettings["packageId"]!.GetValue<string>());
        Assert.Equal("game.Activity", mailSettings["activityName"]!.GetValue<string>());
        Assert.True(mailSettings["shop"]!["buyAlarmClock"]!.GetValue<bool>());
    }

    [Fact]
    public async Task DefaultQueueRunsAllSevenWpfRoutesWithRuntimeSettings()
    {
        var ids = new[]
        {
            "start-game", "team-race", "daily-race", "career-training",
            "mail-collection", "mission-collection", "shop-purchase",
        };
        Assert.Equal(ids, UmaTaskCatalog.All.Select(task => task.Id));

        var fake = new FakeUmaService();
        fake.QueueRunHandler = (tasks, _) =>
        {
            for (var index = 0; index < tasks.Count; index++)
            {
                var taskId = (ulong)(100 + index);
                fake.Emit(new TaskStartedEvent(120, (ulong)(index * 2 + 1), taskId,
                    tasks[index].TaskId, index, tasks.Count));
                fake.Emit(new TaskCompletedEvent(120, (ulong)(index * 2 + 2), taskId,
                    tasks[index].TaskId, 1, 1, "done"));
            }
            return Task.FromResult(new QueueRunResult(true, tasks.Count, 0, 0, tasks.Count,
                ConnectionErrorCode.Success, string.Empty));
        };

        var settings = new ConnectionSettings { TargetActivityName = "game.MainActivity" };
        settings.TargetPackageIds.Add("example.game");
        settings.Hachimi.Shop.BuyAlarmClock = true;
        var queue = UmaTaskCatalog.CreateDefaultQueue(settings);
        var automation = new UmaAutomationService(new LogService(), new ConnectionStateService(), fake,
            new MemorySettingsService(settings));

        var result = await automation.RunTaskQueueAsync(queue.Select(item =>
            new UmaTaskRequest(item.TaskId, item.Settings)).ToArray());

        Assert.True(result.Succeeded);
        Assert.Equal(7, result.SucceededTasks);
        Assert.Equal(ids, fake.LastQueueRequests.Select(request => request.TaskId));
        Assert.All(fake.LastQueueRequests, request =>
        {
            var payload = UmaTaskSettingsSerializer.Deserialize(request.SettingsJson);
            Assert.Equal("example.game", payload["packageId"]!.GetValue<string>());
            Assert.Equal("game.MainActivity", payload["activityName"]!.GetValue<string>());
        });

        var teamRace = UmaTaskSettingsSerializer.Deserialize(fake.LastQueueRequests[1].SettingsJson);
        Assert.Equal(3, teamRace["raceCount"]!.GetValue<int>());
        Assert.True(teamRace["stopWhenTicketsEmpty"]!.GetValue<bool>());
        Assert.True(teamRace["shop"]!["buyAlarmClock"]!.GetValue<bool>());
        var dailyRace = UmaTaskSettingsSerializer.Deserialize(fake.LastQueueRequests[2].SettingsJson);
        Assert.Equal("monies", dailyRace["mode"]!.GetValue<string>());
        var career = UmaTaskSettingsSerializer.Deserialize(fake.LastQueueRequests[3].SettingsJson);
        Assert.Equal(100601, career["traineeId"]!.GetValue<int>());
        Assert.True(Path.IsPathFullyQualified(career["manifestPath"]!.GetValue<string>()));
        Assert.True(Path.IsPathFullyQualified(career["checkpointDirectory"]!.GetValue<string>()));
        Assert.Null(career["shop"]);
        Assert.True(Path.IsPathFullyQualified(
            UmaTaskSettingsSerializer.Deserialize(fake.LastQueueRequests[4].SettingsJson)["definitionPath"]!.GetValue<string>()));
        Assert.True(Path.IsPathFullyQualified(
            UmaTaskSettingsSerializer.Deserialize(fake.LastQueueRequests[5].SettingsJson)["definitionPath"]!.GetValue<string>()));
        Assert.True(UmaTaskSettingsSerializer.Deserialize(fake.LastQueueRequests[6].SettingsJson)["shop"]!["buyAlarmClock"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RunTaskQueueReportsTaskFailureWhileContinuingOrderedQueue()
    {
        var fake = new FakeUmaService();
        fake.QueueRunHandler = (tasks, _) =>
        {
            fake.Emit(new TaskStartedEvent(50, 1, 10, "mail-collection", 0, 2));
            fake.Emit(new TaskFailedEvent(50, 2, 10, "mail-collection", ConnectionErrorCode.RecognitionFailed, "mail UI not found"));
            fake.Emit(new TaskStartedEvent(50, 3, 11, "mission-collection", 1, 2));
            fake.Emit(new TaskCompletedEvent(50, 4, 11, "mission-collection", 1, 1, "done"));
            return Task.FromResult(new QueueRunResult(true, 1, 1, 0, tasks.Count,
                ConnectionErrorCode.Success, string.Empty));
        };
        fake.RunSnapshot = new QueueRunSnapshot(50, QueueRunState.Completed, null, string.Empty, 0, 0, 1, 1, 0,
        [
            new QueueTaskSnapshot(10, "mail-collection", QueueTaskStatus.Failed, (int)ConnectionErrorCode.RecognitionFailed, "mail UI not found", 0, 1),
            new QueueTaskSnapshot(11, "mission-collection", QueueTaskStatus.Succeeded, 0, "done", 1, 1),
        ]);
        var automation = new UmaAutomationService(new LogService(), new ConnectionStateService(), fake);
        var updates = new List<UmaTaskQueueEvent>();

        var result = await automation.RunTaskQueueAsync(
        [new("mail-collection"), new("mission-collection")], updates.Add);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.FailedTasks);
        Assert.Equal(1, result.SucceededTasks);
        Assert.Contains(updates, update => update.TaskId == "mail-collection" && update.Kind == UmaTaskQueueEventKind.Failed);
        Assert.Contains(updates, update => update.TaskId == "mission-collection" && update.Kind == UmaTaskQueueEventKind.Completed);
        Assert.False(automation.IsBusy);
    }

    [Fact]
    public async Task RunTaskQueueCancellationMarksCurrentAndSkipsRemainingTasks()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeUmaService();
        fake.QueueRunHandler = async (_, cancellationToken) =>
        {
            fake.Emit(new TaskStartedEvent(61, 1, 21, "mail-collection", 0, 2));
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        };
        fake.RunSnapshot = new QueueRunSnapshot(61, QueueRunState.Canceled, 21, "mail-collection", 0, 1, 0, 0, 1,
        [
            new QueueTaskSnapshot(21, "mail-collection", QueueTaskStatus.Running, 0, "canceling", 0, 1),
            new QueueTaskSnapshot(22, "mission-collection", QueueTaskStatus.Skipped, (int)ConnectionErrorCode.Canceled, "canceled", 0, 1),
        ]);
        var automation = new UmaAutomationService(new LogService(), new ConnectionStateService(), fake);
        var updates = new List<UmaTaskQueueEvent>();
        using var cancellation = new CancellationTokenSource();
        var run = automation.RunTaskQueueAsync(
            [new("mail-collection"), new("mission-collection")],
            updates.Add,
            cancellation.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await run;

        Assert.True(result.Canceled);
        Assert.False(result.Succeeded);
        Assert.Contains(updates, update => update.TaskIndex == 0 && update.Kind == UmaTaskQueueEventKind.Canceled);
        Assert.Contains(updates, update => update.TaskIndex == 1 && update.Kind == UmaTaskQueueEventKind.Skipped);
        Assert.False(automation.IsBusy);
    }

    [Fact]
    public async Task RunTaskQueueRejectsConcurrentOperationsAndReleasesBusyState()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeUmaService();
        fake.QueueRunHandler = async (tasks, cancellationToken) =>
        {
            fake.Emit(new TaskStartedEvent(72, 1, 30, "mail-collection", 0, 1));
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            fake.Emit(new TaskCompletedEvent(72, 2, 30, "mail-collection", 1, 1, "done"));
            return new QueueRunResult(true, 1, 0, 0, tasks.Count, ConnectionErrorCode.Success, string.Empty);
        };
        fake.RunSnapshot = new QueueRunSnapshot(72, QueueRunState.Completed, null, string.Empty, 0, 0, 1, 0, 0,
        [new QueueTaskSnapshot(30, "mail-collection", QueueTaskStatus.Succeeded, 0, "done", 1, 1)]);
        var automation = new UmaAutomationService(new LogService(), new ConnectionStateService(), fake);
        var busyEvents = new List<bool>();
        automation.BusyChanged += busyEvents.Add;

        var firstRun = automation.RunTaskQueueAsync([new("mail-collection")]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(automation.IsBusy);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => automation.RunTaskQueueAsync([new("daily-race")]));
        release.TrySetResult();
        var firstResult = await firstRun;

        Assert.True(firstResult.Succeeded);
        Assert.False(automation.IsBusy);
        Assert.Collection(busyEvents,
            isBusy => Assert.True(isBusy),
            isBusy => Assert.False(isBusy));
    }

    [Fact]
    public async Task StartGameUsesConfiguredPackageAndActivity()
    {
        var fake = new FakeUmaService();
        var settings = new ConnectionSettings
        {
            TargetActivityName = "example.game.MainActivity",
        };
        settings.TargetPackageIds.Add("example.game");
        var automation = new UmaAutomationService(
            new LogService(),
            new ConnectionStateService(),
            fake,
            new MemorySettingsService(settings));
        Assert.True(await automation.ConnectAsync("adb", "127.0.0.1:5555"));

        var result = await automation.StartGameAndRecognizeHomeAsync();

        Assert.True(result);
        Assert.Equal("example.game", fake.LastPackageOperationId);
        Assert.Equal("start-game", fake.LastGameTaskId);
        var taskSettings = UmaTaskSettingsSerializer.Deserialize(fake.LastGameTaskSettingsJson);
        Assert.Equal("example.game", taskSettings["packageId"]!.GetValue<string>());
        Assert.Equal("example.game.MainActivity", taskSettings["activityName"]!.GetValue<string>());
        Assert.False(automation.IsBusy);
    }

    [Fact]
    public async Task ScreenshotOperationCannotOverlapTaskQueue()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeUmaService
        {
            RunSnapshot = new QueueRunSnapshot(82, QueueRunState.Completed, null, string.Empty, 0, 0, 1, 0, 0,
            [new QueueTaskSnapshot(40, "mail-collection", QueueTaskStatus.Succeeded, 0, "done", 1, 1)]),
        };
        fake.QueueRunHandler = async (tasks, cancellationToken) =>
        {
            fake.Emit(new TaskStartedEvent(82, 1, 40, "mail-collection", 0, 1));
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            fake.Emit(new TaskCompletedEvent(82, 2, 40, "mail-collection", 1, 1, "done"));
            return new QueueRunResult(true, 1, 0, 0, tasks.Count, ConnectionErrorCode.Success, string.Empty);
        };
        var automation = new UmaAutomationService(new LogService(), new ConnectionStateService(), fake);
        Assert.True(await automation.ConnectAsync("adb", "127.0.0.1:5555"));
        var run = automation.RunTaskQueueAsync([new("mail-collection")]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => automation.CaptureScreenshotAsync());

        release.TrySetResult();
        Assert.True((await run).Succeeded);
    }

    [Fact]
    public async Task RunExclusiveAsyncUsesSharedBusyGateAndPropagatesCancellation()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeUmaService();
        var automation = new UmaAutomationService(new LogService(), new ConnectionStateService(), fake);
        using var cancellation = new CancellationTokenSource();

        var operation = automation.RunExclusiveAsync(async token =>
        {
            started.TrySetResult(token);
            await Task.Delay(Timeout.Infinite, token);
            return true;
        }, cancellation.Token);
        var linkedToken = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(automation.IsBusy);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            automation.RunTaskQueueAsync([new("mail-collection")]));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(linkedToken.IsCancellationRequested);
        Assert.False(automation.IsBusy);
    }

    [Fact]
    public async Task ReconnectAdvancesSessionAndStaleDisconnectDoesNotClearNewSession()
    {
        var fake = new FakeUmaService();
        var connectionState = new ConnectionStateService();
        var automation = new UmaAutomationService(new LogService(), connectionState, fake);
        Assert.True(await automation.ConnectAsync("adb", "127.0.0.1:5555"));
        Assert.Equal((ulong)1, automation.ActiveDeviceIdentity?.SessionGeneration);

        fake.Emit(new DeviceReconnectedEvent(
            1, 2, 1, "127.0.0.1:5555", 900, 1600, 1000, 1800, DisplaySizeSource.Physical));

        Assert.Equal((ulong)2, automation.ActiveDeviceIdentity?.SessionGeneration);
        Assert.True(automation.IsConnected);
        Assert.Equal(900, automation.CurrentDevice?.Width);
        Assert.Equal(1600, automation.CurrentDevice?.Height);

        fake.Emit(new DeviceDisconnectedEvent(1, 2, ConnectionErrorCode.DeviceDisconnected));

        Assert.True(automation.IsConnected);
        Assert.Equal((ulong)2, automation.ActiveDeviceIdentity?.SessionGeneration);

        fake.Emit(new DeviceDisconnectedEvent(2, 3, ConnectionErrorCode.DeviceDisconnected));

        Assert.False(automation.IsConnected);
        Assert.Null(automation.ActiveDeviceIdentity);
        Assert.Null(automation.CurrentDevice);
        Assert.Equal(ConnectionState.Disconnected, connectionState.State);
    }

    [Fact]
    public async Task MatchingDisconnectCancelsActiveTaskQueue()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeUmaService();
        fake.QueueRunHandler = async (_, cancellationToken) =>
        {
            fake.Emit(new TaskStartedEvent(91, 1, 60, "mail-collection", 0, 1));
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        };
        var connectionState = new ConnectionStateService();
        var automation = new UmaAutomationService(new LogService(), connectionState, fake);
        Assert.True(await automation.ConnectAsync("adb", "127.0.0.1:5555"));
        var run = automation.RunTaskQueueAsync([new("mail-collection")]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fake.Emit(new DeviceDisconnectedEvent(1, 2, ConnectionErrorCode.DeviceDisconnected));
        var result = await run;

        Assert.True(result.Canceled);
        Assert.False(result.Succeeded);
        Assert.False(automation.IsConnected);
        Assert.Equal(ConnectionState.Disconnected, connectionState.State);
        Assert.False(automation.IsBusy);
    }
}

internal sealed class MemorySettingsService(ConnectionSettings settings) : ISettingsService
{
    public ConnectionSettings Load() => settings;

    public void Save(ConnectionSettings value) => settings = value;
}
