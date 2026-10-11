using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Tests.Services;
using TemplateMatchResult = UmamusumeWpfGui.Models.TemplateMatchResult;

namespace UmamusumeWpfGui.Tests.Services.Tasks;

public sealed class UmaVisualDeviceFacadeTests
{
    private static readonly byte[] TestPng = CreatePng(100, 100);

    [Fact]
    public async Task Tap_consumes_the_exact_recognition_frame_and_rejects_reuse()
    {
        var service = new FakeUmaService();
        await using var facade = new UmaVisualDeviceFacade(service);
        var connection = service.CreateConnection();
        GrayImage frame = await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException();
        FrameProvenance source = Assert.IsType<FrameProvenance>(frame.SourceFrame);

        Assert.NotNull(facade.GetRetainedImage(source));
        await facade.TapAtFrameAsync(connection, frame, 0, 0);

        Assert.Equal([source.Metadata.FrameId], service.TappedFrameIds);
        Assert.Null(facade.GetRetainedImage(source));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            facade.TapAtSourceAsync(connection, source, 0, 0));
        Assert.Single(service.TappedFrameIds);
    }

    [Fact]
    public async Task Package_query_preserves_frames_but_mutating_operation_retires_them()
    {
        var service = new FakeUmaService();
        await using var facade = new UmaVisualDeviceFacade(service);
        LastVerifiedConnection connection = service.CreateConnection();
        GrayImage queryFrame = await facade.CaptureGrayAsync(connection)
            ?? throw new InvalidOperationException();
        FrameProvenance querySource = queryFrame.SourceFrame!;

        DeviceOperationEvent query = await facade.PackageOperationAsync(
            connection.AdbPath, connection.Serial, "com.example.game", null,
            PackageOperationKind.QueryRunning);

        Assert.IsType<PackageOperationCompletedEvent>(query);
        Assert.NotNull(facade.GetRetainedImage(querySource));

        service.PackageOperationResults.Enqueue(new DeviceOperationFailedEvent(
            0, 0, ConnectionErrorCode.CommandFailed, "package_operation", "launch failed"));
        DeviceOperationEvent start = await facade.PackageOperationAsync(
            connection.AdbPath, connection.Serial, "com.example.game", null,
            PackageOperationKind.StartPackage);

        Assert.IsType<DeviceOperationFailedEvent>(start);
        Assert.Null(facade.GetRetainedImage(querySource));
        Assert.Equal(1, service.ReleasedFrameIds.Count(id => id == querySource.Metadata.FrameId));
        Assert.Equal(
            [PackageOperationKind.QueryRunning, PackageOperationKind.StartPackage],
            service.PackageOperations.Select(item => item.Operation).ToArray());
    }

    [Fact]
    public async Task Package_operations_reject_a_stale_adb_identity_before_native_dispatch()
    {
        var service = new FakeUmaService();
        await using var facade = new UmaVisualDeviceFacade(service);
        LastVerifiedConnection connection = service.CreateConnection();

        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.PackageOperationAsync(
            Path.Combine(Path.GetTempPath(), "stale-adb.exe"), connection.Serial,
            "com.example.game", null, PackageOperationKind.StartPackage));
        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.PackageOperationAsync(
            connection.AdbPath, "stale-serial", "com.example.game", null,
            PackageOperationKind.QueryRunning));

        Assert.Empty(service.PackageOperations);
    }

    [Fact]
    public async Task Held_touch_requires_preparation_and_accepts_a_ready_frame_from_same_geometry()
    {
        var service = new FakeUmaService();
        await using var facade = new UmaVisualDeviceFacade(service);
        var connection = service.CreateConnection();
        GrayImage unprepared = await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException();

        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.TouchDownAsync(connection, unprepared, 0, 0));
        Assert.Equal(0, service.PrepareTouchCount);
        Assert.Equal(1, service.TouchDownCount);
        await facade.TouchCancelAsync(connection, unprepared.SourceFrame!);

        await facade.PrepareHeldTouchAsync(connection);
        GrayImage ready = await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException();
        Assert.NotEqual(unprepared.SourceFrame!.Metadata.FrameId, ready.SourceFrame!.Metadata.FrameId);
        await facade.TouchDownAsync(connection, ready, 0, 0);

        Assert.Equal(1, service.PrepareTouchCount);
        Assert.Equal(2, service.TouchDownCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            facade.TouchUpAsync(connection, unprepared.SourceFrame!));
        await facade.TouchUpAsync(connection, ready.SourceFrame!);
        await facade.TouchUpAsync(connection, ready.SourceFrame!);
        Assert.Equal([ready.SourceFrame.Metadata.FrameId], service.TouchUpFrameIds);
    }

    [Fact]
    public async Task Failed_down_keeps_its_source_until_retrying_a_failed_cancel_succeeds()
    {
        var service = new FakeUmaService { DownFailure = true, CleanupFailuresRemaining = 1 };
        await using var facade = new UmaVisualDeviceFacade(service);
        var connection = service.CreateConnection();
        await facade.PrepareHeldTouchAsync(connection);
        GrayImage ready = await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException();
        FrameProvenance source = ready.SourceFrame!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.TouchDownAsync(connection, ready, 3, 4));
        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.TouchCancelAsync(connection, source));
        await facade.TouchCancelAsync(connection, source);

        Assert.Equal([source.Metadata.FrameId, source.Metadata.FrameId], service.TouchCancelFrameIds);
        Assert.All(service.TouchCancelTokens, canceled => Assert.False(canceled));
        GrayImage next = await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException();
        await facade.TapAtFrameAsync(connection, next, 1, 1);
        Assert.Single(service.TappedFrameIds);
    }

    [Fact]
    public async Task Thrown_down_with_failed_best_effort_cancel_keeps_source_until_caller_cancel()
    {
        var service = new FakeUmaService { DownThrows = true, CleanupFailuresRemaining = 1 };
        await using var facade = new UmaVisualDeviceFacade(service);
        var connection = service.CreateConnection();
        await facade.PrepareHeldTouchAsync(connection);
        GrayImage ready = await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException();
        FrameProvenance source = ready.SourceFrame!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.TouchDownAsync(connection, ready, 3, 4));
        Assert.NotNull(facade.GetRetainedImage(source));
        Assert.Contains(source.Metadata.FrameId, service.TouchCancelFrameIds);
        await facade.TouchCancelAsync(connection, source);

        Assert.Equal([source.Metadata.FrameId, source.Metadata.FrameId], service.TouchCancelFrameIds);
        Assert.Equal(1, service.ReleasedFrameIds.Count(id => id == source.Metadata.FrameId));
        GrayImage next = await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException();
        await facade.TapAtFrameAsync(connection, next, 1, 1);
        Assert.Single(service.TappedFrameIds);
    }

    [Fact]
    public async Task Retry_expiry_overflow_does_not_transfer_or_lose_ordinary_frame()
    {
        var service = new FakeUmaService();
        await using var facade = new UmaVisualDeviceFacade(service);
        var connection = service.CreateConnection();
        GrayImage frame = await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException();
        FrameProvenance source = frame.SourceFrame!;
        var match = new TemplateMatchResult(true, 1, 10, 10, 4, 4) { SourceFrame = source };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            facade.RetainMatchForRetry(connection, match, 2, TimeSpan.MaxValue));

        Assert.NotNull(facade.GetRetainedImage(source));
        await facade.TapMatchAsync(connection, match);
        Assert.Equal(1, service.ReleasedFrameIds.Count(id => id == source.Metadata.FrameId));
    }

    [Fact]
    public async Task Ordinary_frames_are_bounded_by_capacity_and_ttl()
    {
        var service = new FakeUmaService();
        var clock = new ManualTimeProvider();
        await using var facade = new UmaVisualDeviceFacade(service, clock);
        var connection = service.CreateConnection();
        var frames = new List<GrayImage>();

        for (var index = 0; index < 4; index++)
            frames.Add(await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException());

        Assert.Null(facade.GetRetainedImage(frames[0].SourceFrame!));
        Assert.Equal(1, service.ReleasedFrameIds.Count(id => id == frames[0].SourceFrame!.Metadata.FrameId));
        Assert.All(frames.Skip(1), frame => Assert.NotNull(facade.GetRetainedImage(frame.SourceFrame!)));

        clock.Advance(TimeSpan.FromSeconds(21));
        Assert.All(frames.Skip(1), frame => Assert.Null(facade.GetRetainedImage(frame.SourceFrame!)));
        Assert.All(frames.Skip(1), frame =>
            Assert.Equal(1, service.ReleasedFrameIds.Count(id => id == frame.SourceFrame!.Metadata.FrameId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.TapAtFrameAsync(
            connection, frames[1], 10, 10));
    }

    [Fact]
    public async Task Retry_frames_expire_without_waiting_for_the_timer()
    {
        var service = new FakeUmaService();
        var clock = new ManualTimeProvider();
        await using var facade = new UmaVisualDeviceFacade(service, clock);
        var connection = service.CreateConnection();
        GrayImage frame = await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException();
        FrameProvenance source = frame.SourceFrame!;
        var match = new TemplateMatchResult(true, 1, 10, 10, 4, 4) { SourceFrame = source };
        using FrameRetryIntent retry = facade.RetainMatchForRetry(
            connection, match, maxTapAttempts: 2, lifetime: TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Null(facade.GetRetainedImage(source));
        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.TapMatchAsync(connection, match, retry));
        Assert.Equal(1, service.ReleasedFrameIds.Count(id => id == source.Metadata.FrameId));
    }

    [Fact]
    public async Task Held_frame_survives_ordinary_expiry_and_rotation_pressure_until_cancel()
    {
        var service = new FakeUmaService();
        var clock = new ManualTimeProvider();
        await using var facade = new UmaVisualDeviceFacade(service, clock);
        var connection = service.CreateConnection();
        await facade.PrepareHeldTouchAsync(connection);
        GrayImage ready = await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException();
        FrameProvenance heldSource = ready.SourceFrame!;
        await facade.TouchDownAsync(connection, ready, 20, 21);

        clock.Advance(TimeSpan.FromSeconds(21));
        Assert.NotNull(facade.GetRetainedImage(heldSource));
        var ordinary = new List<GrayImage>();
        for (var index = 0; index < 5; index++)
            ordinary.Add(await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException());

        Assert.NotNull(facade.GetRetainedImage(heldSource));
        Assert.Equal(0, service.ReleasedFrameIds.Count(id => id == heldSource.Metadata.FrameId));
        Assert.Equal(2, service.ReleasedFrameIds.Count(id => ordinary.Any(frame =>
            frame.SourceFrame!.Metadata.FrameId == id)));

        clock.Advance(TimeSpan.FromSeconds(21));
        Assert.Null(facade.GetRetainedImage(ordinary[^1].SourceFrame!));
        Assert.NotNull(facade.GetRetainedImage(heldSource));
        await facade.TouchCancelAsync(connection, heldSource);
        Assert.Equal(1, service.TouchCancelFrameIds.Count(id => id == heldSource.Metadata.FrameId));
        Assert.Equal(1, service.ReleasedFrameIds.Count(id => id == heldSource.Metadata.FrameId));
    }

    [Fact]
    public async Task Geometry_change_retires_held_source_and_allows_a_new_prepare_cycle()
    {
        var service = new FakeUmaService();
        await using var facade = new UmaVisualDeviceFacade(service);
        var connection = service.CreateConnection();
        await facade.PrepareHeldTouchAsync(connection);
        GrayImage oldReady = await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException();
        FrameProvenance oldSource = oldReady.SourceFrame!;
        await facade.TouchDownAsync(connection, oldReady, 20, 21);

        service.ChangeGeometry(10);
        GrayImage current = await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException();
        Assert.Null(facade.GetRetainedImage(oldSource));
        Assert.Equal(1, service.ReleasedFrameIds.Count(id => id == oldSource.Metadata.FrameId));
        Assert.NotNull(facade.GetRetainedImage(current.SourceFrame!));
        await facade.TouchUpAsync(connection, oldSource);
        Assert.Empty(service.TouchUpFrameIds);

        var currentMatch = new TemplateMatchResult(true, 1, 20, 20, 4, 4)
        {
            SourceFrame = current.SourceFrame,
        };
        using FrameRetryIntent retry = facade.RetainMatchForRetry(
            connection, currentMatch, maxTapAttempts: 2, lifetime: TimeSpan.FromSeconds(30));
        service.ChangeGeometry(11);
        GrayImage afterGeometryRetry = await facade.CaptureGrayAsync(connection)
            ?? throw new InvalidOperationException();
        Assert.NotNull(facade.GetRetainedImage(afterGeometryRetry.SourceFrame!));
        Assert.Null(facade.GetRetainedImage(current.SourceFrame!));
        Assert.Null(facade.GetRetainedImage(retry.Source));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            facade.TapMatchAsync(connection, currentMatch, retry));

        await facade.PrepareHeldTouchAsync(connection);
        GrayImage newReady = await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException();
        await facade.TouchDownAsync(connection, newReady, 30, 31);
        await facade.TouchCancelAsync(connection, newReady.SourceFrame!);
        Assert.Equal(2, service.TouchDownCount);
        Assert.Equal([newReady.SourceFrame!.Metadata.FrameId], service.TouchCancelFrameIds);
        Assert.Null(facade.GetRetainedImage(current.SourceFrame!));
    }

    [Fact]
    public async Task Stale_disconnect_event_does_not_invalidate_the_new_native_session()
    {
        var service = new FakeUmaService();
        await using var facade = new UmaVisualDeviceFacade(service);
        LastVerifiedConnection oldConnection = service.CreateConnection();
        GrayImage oldFrame = await facade.CaptureGrayAsync(oldConnection)
            ?? throw new InvalidOperationException();
        ulong oldGeneration = oldFrame.SourceFrame!.Metadata.SessionGeneration;
        var oldMatch = new TemplateMatchResult(true, 1, 10, 10, 4, 4) { SourceFrame = oldFrame.SourceFrame };
        using FrameRetryIntent oldRetry = facade.RetainMatchForRetry(
            oldConnection, oldMatch, maxTapAttempts: 2, lifetime: TimeSpan.FromSeconds(30));

        LastVerifiedConnection newConnection = service.ChangeSession(
            Path.Combine(Path.GetTempPath(), "new-fake-adb.exe"), "new-serial", oldGeneration + 1);
        GrayImage newFrame = await facade.CaptureGrayAsync(newConnection) ?? throw new InvalidOperationException();
        Assert.Null(facade.GetRetainedImage(oldRetry.Source));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            facade.TapMatchAsync(newConnection, oldMatch, oldRetry));
        service.Publish(new DeviceDisconnectedEvent(oldGeneration, 100, ConnectionErrorCode.DeviceNotReady));

        Assert.NotNull(facade.GetRetainedImage(newFrame.SourceFrame!));
        await facade.TapAtFrameAsync(newConnection, newFrame, 9, 10);
        Assert.Equal([newFrame.SourceFrame!.Metadata.FrameId], service.TappedFrameIds);
        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.TapAtFrameAsync(
            FakeUmaService.CreateConnectionForPath(), oldFrame, 1, 1));
    }

    [Fact]
    public async Task Retry_intent_is_bound_to_original_point_and_finite_attempt_budget()
    {
        var service = new FakeUmaService();
        await using var facade = new UmaVisualDeviceFacade(service);
        var connection = service.CreateConnection();
        GrayImage frame = await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException();
        var match = new TemplateMatchResult(true, 1, 10, 10, 4, 4) { SourceFrame = frame.SourceFrame };
        using FrameRetryIntent retry = facade.RetainMatchForRetry(
            connection, match, maxTapAttempts: 2, lifetime: TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            facade.TapMatchAsync(connection, match with { X = 11 }, retry));
        await facade.TapMatchAsync(connection, match, retry);
        await facade.TapMatchAsync(connection, match, retry);
        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.TapMatchAsync(connection, match, retry));
        Assert.Equal(2, service.TappedFrameIds.Count);
        Assert.All(service.TappedPoints, point => Assert.Equal((12, 12), point));
    }

    [Fact]
    public async Task Key_and_text_input_use_native_frames_and_invalidate_evidence_even_on_failure()
    {
        var service = new FakeUmaService();
        await using var facade = new UmaVisualDeviceFacade(service);
        var connection = service.CreateConnection();
        GrayImage previous = await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException();
        FrameProvenance previousSource = previous.SourceFrame!;

        await facade.SendKeyEventAsync(connection, "KEYCODE_BACK");

        Assert.Null(facade.GetRetainedImage(previousSource));
        Assert.Equal("KEYCODE_BACK", Assert.Single(service.KeyEventCalls).KeyCode);
        Assert.NotEqual(previousSource.Metadata.FrameId, service.KeyEventCalls[0].FrameId);

        GrayImage beforeFailedText = await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException();
        FrameProvenance failedTextSource = beforeFailedText.SourceFrame!;
        service.TextInputFailure = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.InputTextAsync(connection, "马娘 & 1,500"));

        Assert.Equal("马娘 & 1,500", Assert.Single(service.TextInputCalls).Text);
        Assert.Null(facade.GetRetainedImage(failedTextSource));
        Assert.Contains(failedTextSource.Metadata.FrameId, service.ReleasedFrameIds);
        Assert.Contains(service.TextInputCalls[0].FrameId, service.ReleasedFrameIds);
    }

    [Fact]
    public async Task Explicit_key_and_text_input_use_the_guarded_source_frame()
    {
        var service = new FakeUmaService();
        await using var facade = new UmaVisualDeviceFacade(service);
        var connection = service.CreateConnection();
        GrayImage keyFrame = await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException();
        FrameProvenance keySource = keyFrame.SourceFrame!;

        await facade.SendKeyEventAtFrameAsync(connection, keyFrame, "KEYCODE_BACK");

        Assert.Equal(keySource.Metadata.FrameId, Assert.Single(service.KeyEventCalls).FrameId);
        Assert.Null(facade.GetRetainedImage(keySource));

        GrayImage textFrame = await facade.CaptureGrayAsync(connection) ?? throw new InvalidOperationException();
        FrameProvenance textSource = textFrame.SourceFrame!;
        await facade.InputTextAtFrameAsync(connection, textFrame, "space % \\\"quoted\\\" 马娘");

        Assert.Equal(textSource.Metadata.FrameId, Assert.Single(service.TextInputCalls).FrameId);
        Assert.Equal("space % \\\"quoted\\\" 马娘", service.TextInputCalls[0].Text);
        Assert.Null(facade.GetRetainedImage(textSource));
        Assert.Equal(1, service.ReleasedFrameIds.Count(id => id == keySource.Metadata.FrameId));
        Assert.Equal(1, service.ReleasedFrameIds.Count(id => id == textSource.Metadata.FrameId));
    }

    private sealed class FakeUmaService : IUmaService
    {
        private static readonly byte[] Png = CreatePng(100, 100);
        private ulong _nextFrameId;
        private bool _touchPrepared;
        private bool _touchActive;
        private ulong _sessionGeneration = 7;
        private ulong _geometryGeneration = 9;

        public string? CoreVersion => "test";
        public string AdbPath { get; private set; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "uma-fake-adb.exe"));
        public string Serial { get; private set; } = "fake-serial";
        public DeviceSessionIdentity? ActiveDeviceIdentity { get; private set; }
        public event Action<ConnectionEvent>? ConnectionEventReceived;
        public event Action<BridgeDiagnostic>? DiagnosticReceived { add { } remove { } }
        public List<ulong> TappedFrameIds { get; } = [];
        public List<ulong> CapturedFrameIds { get; } = [];
        public List<(int X, int Y)> TappedPoints { get; } = [];
        public List<(ulong FrameId, string KeyCode)> KeyEventCalls { get; } = [];
        public List<(ulong FrameId, string Text)> TextInputCalls { get; } = [];
        public List<ulong> ReleasedFrameIds { get; } = [];
        public List<ulong> TouchUpFrameIds { get; } = [];
        public List<ulong> TouchCancelFrameIds { get; } = [];
        public List<bool> TouchCancelTokens { get; } = [];
        public Queue<DeviceOperationEvent> PackageOperationResults { get; } = [];
        public List<(string AdbPath, string Serial, string PackageId, string? Activity, PackageOperationKind Operation)> PackageOperations { get; } = [];
        public int PrepareTouchCount { get; private set; }
        public int TouchDownCount { get; private set; }
        public bool DownFailure { get; set; }
        public bool DownThrows { get; set; }
        public int CleanupFailuresRemaining { get; set; }
        public bool TextInputFailure { get; set; }

        public LastVerifiedConnection CreateConnection()
        {
            ActiveDeviceIdentity = new DeviceSessionIdentity(AdbPath, Serial, _sessionGeneration);
            return new(AdbPath, Serial, "android", "version", 100, 100, 100, 100, DateTimeOffset.UtcNow);
        }

        public LastVerifiedConnection ChangeSession(string adbPath, string serial, ulong generation)
        {
            AdbPath = Path.GetFullPath(adbPath);
            Serial = serial;
            _sessionGeneration = generation;
            _touchPrepared = false;
            _touchActive = false;
            return CreateConnection();
        }

        public static LastVerifiedConnection CreateConnectionForPath() => new(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "uma-fake-adb.exe")),
            "fake-serial", "android", "version", 100, 100, 100, 100, DateTimeOffset.UtcNow);

        public void ChangeGeometry(ulong geometryGeneration)
        {
            _geometryGeneration = geometryGeneration;
            _touchPrepared = false;
            _touchActive = false;
        }

        public void Publish(ConnectionEvent connectionEvent) => ConnectionEventReceived?.Invoke(connectionEvent);

        public Task InitializeAsync(string appBaseDir, string appDataDir, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ConnectionTerminalEvent> ConnectAsync(
            string adbPath, string serial, string profile, CancellationToken cancellationToken = default) =>
            Task.FromResult<ConnectionTerminalEvent>(new ConnectionFailedEvent(
                0, ConnectionErrorCode.InvalidArgument, "fake", "not used", 1, 1));

        public Task<DeviceOperationEvent> CaptureAsync(CancellationToken cancellationToken = default)
        {
            var metadata = CreateMetadata(++_nextFrameId, Png);
            var frame = CreateFrame(metadata, Png);
            CapturedFrameIds.Add(metadata.FrameId);
            return Task.FromResult<DeviceOperationEvent>(new FrameCapturedEvent(0, metadata.FrameId, metadata, Png)
            {
                FrameLease = frame,
            });
        }

        public Task<DeviceOperationEvent> PackageOperationAsync(
            string packageId, string? activity, PackageOperationKind operation,
            CancellationToken cancellationToken = default)
        {
            PackageOperations.Add((AdbPath, Serial, packageId, activity, operation));
            if (PackageOperationResults.TryDequeue(out var result))
                return Task.FromResult(result);
            return Task.FromResult<DeviceOperationEvent>(new PackageOperationCompletedEvent(
                0, (ulong)PackageOperations.Count, operation,
                new PackageCommandResult(true, 0, false, false, ConnectionErrorCode.Success, "", "", ""),
                false));
        }

        public Task<DeviceOperationEvent> TapAsync(
            DeviceFrame frame, int x, int y, CancellationToken cancellationToken = default)
        {
            TappedFrameIds.Add(frame.FrameId);
            TappedPoints.Add((x, y));
            return Task.FromResult<DeviceOperationEvent>(new InputSucceededEvent(
                0, frame.FrameId, frame.Metadata, new DeviceInputPoint(x, y, x, y), null));
        }

        public Task<DeviceOperationEvent> PrepareTouchAsync(
            DeviceFrame frame, CancellationToken cancellationToken = default)
        {
            PrepareTouchCount++;
            _touchPrepared = true;
            return Task.FromResult<DeviceOperationEvent>(new InputSucceededEvent(0, frame.FrameId, frame.Metadata, null, null));
        }

        public Task<DeviceOperationEvent> TouchDownAsync(
            DeviceFrame frame, int x, int y, CancellationToken cancellationToken = default)
        {
            TouchDownCount++;
            if (DownThrows)
            {
                DownThrows = false;
                _touchActive = true;
                throw new InvalidOperationException("down dispatch outcome unknown");
            }
            if (DownFailure)
            {
                DownFailure = false;
                _touchActive = true;
                return Task.FromResult<DeviceOperationEvent>(new InputFailedEvent(
                    0, frame.FrameId, ConnectionErrorCode.CommandFailed, "touch_release", "release failed",
                    frame.Metadata, new DeviceInputPoint(x, y, x, y), null));
            }
            if (!_touchPrepared)
                return Task.FromResult<DeviceOperationEvent>(new InputFailedEvent(
                    0, frame.FrameId, ConnectionErrorCode.DeviceNotReady, "touch", "not prepared",
                    frame.Metadata, new DeviceInputPoint(x, y, x, y), null));
            _touchActive = true;
            return Task.FromResult<DeviceOperationEvent>(new InputSucceededEvent(
                0, frame.FrameId, frame.Metadata, new DeviceInputPoint(x, y, x, y), null));
        }

        public Task<DeviceOperationEvent> TouchUpAsync(
            DeviceFrame sourceFrame, CancellationToken cancellationToken = default)
        {
            TouchUpFrameIds.Add(sourceFrame.FrameId);
            _touchPrepared = false;
            _touchActive = false;
            return Task.FromResult<DeviceOperationEvent>(new InputSucceededEvent(
                0, sourceFrame.FrameId, sourceFrame.Metadata, null, null));
        }

        public Task<DeviceOperationEvent> TouchCancelAsync(
            DeviceFrame sourceFrame, CancellationToken cancellationToken = default)
        {
            TouchCancelFrameIds.Add(sourceFrame.FrameId);
            TouchCancelTokens.Add(cancellationToken.IsCancellationRequested);
            if (CleanupFailuresRemaining > 0)
            {
                CleanupFailuresRemaining--;
                return Task.FromResult<DeviceOperationEvent>(new InputFailedEvent(
                    0, sourceFrame.FrameId, ConnectionErrorCode.CommandFailed, "touch_release", "cleanup failed",
                    sourceFrame.Metadata, null, null));
            }
            if (!_touchActive)
                return Task.FromResult<DeviceOperationEvent>(new InputFailedEvent(
                    0, sourceFrame.FrameId, ConnectionErrorCode.DeviceNotReady, "touch_release", "no active press",
                    sourceFrame.Metadata, null, null));
            _touchPrepared = false;
            _touchActive = false;
            return Task.FromResult<DeviceOperationEvent>(new InputSucceededEvent(
                0, sourceFrame.FrameId, sourceFrame.Metadata, null, null));
        }

        public Task<DeviceOperationEvent> KeyEventAsync(
            DeviceFrame frame, string keyCode, CancellationToken cancellationToken = default)
        {
            KeyEventCalls.Add((frame.FrameId, keyCode));
            return Task.FromResult<DeviceOperationEvent>(new InputSucceededEvent(0, frame.FrameId, frame.Metadata, null, null));
        }

        public Task<DeviceOperationEvent> InputTextAsync(
            DeviceFrame frame, string text, CancellationToken cancellationToken = default)
        {
            TextInputCalls.Add((frame.FrameId, text));
            DeviceOperationEvent result = TextInputFailure
                ? new InputFailedEvent(0, frame.FrameId, ConnectionErrorCode.CommandFailed, "input_text",
                    "fake input failure", frame.Metadata, null, null)
                : new InputSucceededEvent(0, frame.FrameId, frame.Metadata, null, null);
            return Task.FromResult(result);
        }

        public Task CancelOperationAsync(ulong operationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private DeviceFrame CreateFrame(DeviceFrameMetadata metadata, byte[] png)
        {
            var constructor = typeof(DeviceFrame).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(object), typeof(DeviceFrameMetadata), typeof(byte[]), typeof(Action<DeviceFrame>)],
                modifiers: null) ?? throw new MissingMethodException("DeviceFrame internal constructor is unavailable.");
            return (DeviceFrame)constructor.Invoke([this, metadata, png, (Action<DeviceFrame>)(frame => ReleasedFrameIds.Add(frame.FrameId))]);
        }

        private DeviceFrameMetadata CreateMetadata(ulong frameId, byte[] png) => new(
            frameId, _sessionGeneration, _geometryGeneration, 100, 100, 0, (ulong)png.Length,
            new DeviceDisplayTransform(100, 100, 0, 100, 100, 1, 0, 0, 100, 100));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoopTimer();
    }

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static byte[] CreatePng(int width, int height)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 40;
            pixels[offset + 1] = 80;
            pixels[offset + 2] = 120;
            pixels[offset + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
