using System.Threading;
using System.Threading.Tasks;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Services;
using Xunit;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class ConnectionHealthMonitorTests
{
    private static readonly ConnectionHealthTarget Target = new(
        @"C:\MuMu\nx_main\adb.exe",
        "127.0.0.1:16384",
        "MuMuEmulator12",
        SessionGeneration: 42);

    [Fact]
    public async Task Start_TracksNativeSessionWithoutRunningAnotherProbe()
    {
        var service = new FakeUmaService();
        var monitor = new ConnectionHealthMonitor(service);

        monitor.Start(Target);

        Assert.True(monitor.IsRunning);
        service.Publish(new ConnectionStartedEvent(42));
        service.Publish(new ConnectionProgressEvent(42, ConnectionPhase.AdbDevices));
        Assert.True(monitor.IsRunning);

        await monitor.StopAsync();
        Assert.False(monitor.IsRunning);
    }

    [Fact]
    public async Task DisconnectFromStaleSession_IsIgnored()
    {
        var service = new FakeUmaService();
        var monitor = new ConnectionHealthMonitor(service);
        var failureCount = 0;
        monitor.Failed += _ => failureCount++;

        monitor.Start(Target);
        service.Publish(new DeviceDisconnectedEvent(
            SessionGeneration: 41,
            Sequence: 1,
            ErrorCode: ConnectionErrorCode.DeviceDisconnected));

        Assert.True(monitor.IsRunning);
        Assert.Equal(0, failureCount);

        await monitor.StopAsync();
    }

    [Fact]
    public void DisconnectForCurrentSession_RaisesFailureOnceAndStopsMonitoring()
    {
        var service = new FakeUmaService();
        var monitor = new ConnectionHealthMonitor(service);
        ConnectionHealthFailure? failure = null;
        var failureCount = 0;
        monitor.Failed += current =>
        {
            failure = current;
            failureCount++;
            Assert.False(monitor.IsRunning);
        };

        monitor.Start(Target);
        service.Publish(new DeviceDisconnectedEvent(
            SessionGeneration: 42,
            Sequence: 3,
            ErrorCode: ConnectionErrorCode.DeviceDisconnected));
        service.Publish(new DeviceDisconnectedEvent(
            SessionGeneration: 42,
            Sequence: 4,
            ErrorCode: ConnectionErrorCode.DeviceDisconnected));

        Assert.Equal(1, failureCount);
        Assert.NotNull(failure);
        Assert.Equal(Target.Serial, failure.Serial);
        Assert.Equal(ConnectionErrorCode.DeviceDisconnected, failure.ErrorCode);
        Assert.Contains("session 42", failure.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.False(monitor.IsRunning);
    }

    [Fact]
    public void Start_ReplacesSessionAndAcceptsOnlyReplacementGeneration()
    {
        var service = new FakeUmaService();
        var monitor = new ConnectionHealthMonitor(service);
        ConnectionHealthFailure? failure = null;
        monitor.Failed += current => failure = current;
        var replacement = Target with
        {
            Serial = "127.0.0.1:16416",
            SessionGeneration = 99,
        };

        monitor.Start(Target);
        monitor.Start(replacement);
        service.Publish(new DeviceDisconnectedEvent(
            SessionGeneration: 42,
            Sequence: 1,
            ErrorCode: ConnectionErrorCode.DeviceDisconnected));

        Assert.True(monitor.IsRunning);
        Assert.Null(failure);

        service.Publish(new DeviceDisconnectedEvent(
            SessionGeneration: 99,
            Sequence: 2,
            ErrorCode: ConnectionErrorCode.DeviceDisconnected));

        Assert.False(monitor.IsRunning);
        Assert.NotNull(failure);
        Assert.Equal(replacement.Serial, failure.Serial);
    }

    private sealed class FakeUmaService : IUmaService
    {
        public string? CoreVersion => "test";
        public event Action<ConnectionEvent>? ConnectionEventReceived;
        #pragma warning disable CS0067
        public event Action<BridgeDiagnostic>? DiagnosticReceived;
        #pragma warning restore CS0067

        public void Publish(ConnectionEvent connectionEvent) =>
            ConnectionEventReceived?.Invoke(connectionEvent);

        public Task InitializeAsync(
            string appBaseDir,
            string appDataDir,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ConnectionTerminalEvent> ConnectAsync(
            string adbPath,
            string serial,
            string profile,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ConnectionTerminalEvent>(
                new NotSupportedException("ConnectionHealthMonitor must not reconnect."));

        public Task CancelOperationAsync(
            ulong operationId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
