using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using Xunit;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class ConnectionHealthMonitorTests
{
    private static readonly ConnectionHealthTarget Target = new(
        @"C:\MuMu\nx_main\adb.exe",
        "127.0.0.1:16384",
        "MuMuEmulator12");

    [Fact]
    public async Task Start_HealthyProbe_RemainsRunningUntilStopped()
    {
        var runner = new FakeAdbRunner([
            new AdbCommandResult("device", "", 0, false, null),
        ]);
        var delay = new ControlledDelay();
        var monitor = CreateMonitor(runner, delay);

        monitor.Start(Target);
        await delay.WaitForCallAsync();
        delay.ReleaseCurrent();
        await runner.WaitForCallAsync();

        Assert.True(monitor.IsRunning);
        Assert.Equal(["-s", Target.Serial, "get-state"], runner.Commands[0]);

        await monitor.StopAsync();

        Assert.False(monitor.IsRunning);
    }

    [Fact]
    public async Task FailedProbe_WithVerifiedEndpoint_ReconnectsOnce()
    {
        var runner = new FakeAdbRunner([
            new AdbCommandResult("", "offline", 1, false, null),
        ]);
        var delay = new ControlledDelay();
        var uma = new FakeUmaService();
        var monitor = CreateMonitor(runner, delay, uma);

        monitor.Start(Target);
        await delay.WaitForCallAsync();
        delay.ReleaseCurrent();
        await uma.WaitForConnectAsync();

        Assert.Equal(1, uma.ConnectCallCount);
        Assert.Equal(Target.Serial, uma.LastConnectCall?.Serial);

        await monitor.StopAsync();
    }

    [Fact]
    public async Task MissingTargetFromAdbDevices_RaisesDeviceDisconnected()
    {
        var runner = new FakeAdbRunner([]);
        var delay = new ControlledDelay();
        var winAdapter = new FakeWinAdapter
        {
            NextDevices = new AdbDevicesResult([], []),
        };
        var failureSource = new TaskCompletionSource<ConnectionHealthFailure>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = CreateMonitor(runner, delay, winAdapter: winAdapter);
        monitor.Failed += failure => failureSource.TrySetResult(failure);

        monitor.Start(Target);
        await delay.WaitForCallAsync();
        delay.ReleaseCurrent();
        var failure = await failureSource.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(ConnectionErrorCode.DeviceDisconnected, failure.ErrorCode);
        Assert.Contains("no longer present", failure.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);

        await monitor.StopAsync();
    }

    [Fact]
    public async Task FailedProbe_WhenRecoveryFails_RaisesFailure()
    {
        var runner = new FakeAdbRunner([
            new AdbCommandResult("", "offline", 1, false, null),
        ]);
        var delay = new ControlledDelay();
        var winAdapter = new FakeWinAdapter
        {
            NextResolution = new EndpointResolutionResult([], []),
        };
        var failureSource = new TaskCompletionSource<ConnectionHealthFailure>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = CreateMonitor(runner, delay, winAdapter: winAdapter);
        monitor.Failed += failure => failureSource.TrySetResult(failure);

        monitor.Start(Target);
        await delay.WaitForCallAsync();
        delay.ReleaseCurrent();
        var failure = await failureSource.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(Target.Serial, failure.Serial);
        Assert.Equal(ConnectionErrorCode.DeviceDisconnected, failure.ErrorCode);
        Assert.Contains("verified", failure.Diagnostic, StringComparison.OrdinalIgnoreCase);

        await monitor.StopAsync();
    }

    [Fact]
    public async Task StopAsync_CancelsPendingProbeWithoutRunningAdb()
    {
        var runner = new FakeAdbRunner([]);
        var delay = new ControlledDelay();
        var monitor = CreateMonitor(runner, delay);

        monitor.Start(Target);
        await delay.WaitForCallAsync();
        await monitor.StopAsync();

        Assert.Empty(runner.Commands);
        Assert.False(monitor.IsRunning);
    }

    [Fact]
    public async Task Start_ReplacesExistingMonitor()
    {
        var runner = new FakeAdbRunner([]);
        var delay = new ControlledDelay();
        var monitor = CreateMonitor(runner, delay);

        monitor.Start(Target);
        await delay.WaitForCallAsync();

        var replacement = Target with { Serial = "127.0.0.1:16416" };
        monitor.Start(replacement);
        await delay.WaitForCallAsync();

        Assert.True(monitor.IsRunning);
        await monitor.StopAsync();
    }

    [Fact]
    public async Task Reconnect_IsSerializedToOneActiveCall()
    {
        var runner = new FakeAdbRunner([
            new AdbCommandResult("", "offline", 1, false, null),
        ]);
        var delay = new ControlledDelay();
        var uma = new FakeUmaService
        {
            ConnectGate = new TaskCompletionSource<ConnectionTerminalEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var monitor = CreateMonitor(runner, delay, uma);

        monitor.Start(Target);
        await delay.WaitForCallAsync();
        delay.ReleaseCurrent();
        await uma.WaitForConnectAsync();

        Assert.Equal(1, uma.ConnectCallCount);
        uma.ConnectGate.TrySetResult(new ConnectionSucceededEvent(
            1, Target.Serial, "id", "14", 1080, 1920, 1080, 1920, DisplaySizeSource.Physical));
        await monitor.StopAsync();
    }

    private static ConnectionHealthMonitor CreateMonitor(
        FakeAdbRunner runner,
        ControlledDelay delay,
        FakeUmaService? uma = null,
        FakeWinAdapter? winAdapter = null) =>
        new(
            runner,
            winAdapter ?? new FakeWinAdapter(),
            uma ?? new FakeUmaService(),
            delay);

    private sealed class FakeAdbRunner : IAdbRunner
    {
        private readonly Queue<AdbCommandResult> _results;
        private readonly TaskCompletionSource<bool> _called = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeAdbRunner(IEnumerable<AdbCommandResult> results) => _results = new(results);

        public List<IReadOnlyList<string>> Commands { get; } = [];

        public Task<bool> WaitForCallAsync() => _called.Task.WaitAsync(TimeSpan.FromSeconds(1));

        public AdbCommandResult Run(string adbPath, IReadOnlyList<string> arguments)
        {
            Commands.Add(arguments);
            return _results.Dequeue();
        }

        public Task<AdbCommandResult> RunAsync(
            string adbPath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = Run(adbPath, arguments);
            _called.TrySetResult(true);
            return Task.FromResult(result);
        }

        public (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error) RunDevices(string adbPath) =>
            ("", "", 0, false, null);
    }

    private sealed class FakeWinAdapter : IWinAdapter
    {
        public EndpointResolutionResult NextResolution { get; set; } = new([Target.Serial], []);
        public AdbDevicesResult NextDevices { get; set; } = new(
            [new AdbDeviceRecord(Target.Serial, "device")], []);

        public DiscoveryResult RefreshEmulatorsInfo() => new([], []);

        public AdbDevicesResult GetAdbDevices(string adbPath) => NextDevices;

        public EndpointResolutionResult ResolveEndpoints(
            string adbPath,
            string profileName,
            CancellationToken cancellationToken) => NextResolution;

        public Task<EndpointResolutionResult> ResolveEndpointsAsync(
            string adbPath,
            string profileName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(NextResolution);
        }
    }

    private sealed class FakeUmaService : IUmaService
    {
        private readonly TaskCompletionSource<bool> _connectCalled = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ConnectionTerminalEvent>? ConnectGate { get; set; }
        public int ConnectCallCount { get; private set; }
        public (string AdbPath, string Serial, string Profile)? LastConnectCall { get; private set; }
        public string? CoreVersion => "test";
        public string? ResourcePath => null;
        #pragma warning disable CS0067
        public event Action<ConnectionEvent>? ConnectionEventReceived;
        public event Action<BridgeDiagnostic>? DiagnosticReceived;
        #pragma warning restore CS0067

        public Task<bool> WaitForConnectAsync() => _connectCalled.Task.WaitAsync(TimeSpan.FromSeconds(1));

        public Task InitializeAsync(string appBaseDir, string appDataDir, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task<ConnectionTerminalEvent> ConnectAsync(
            string adbPath,
            string serial,
            string profile,
            CancellationToken cancellationToken = default)
        {
            ConnectCallCount++;
            LastConnectCall = (adbPath, serial, profile);
            _connectCalled.TrySetResult(true);
            if (ConnectGate is not null)
                return await ConnectGate.Task.WaitAsync(cancellationToken);
            return new ConnectionSucceededEvent(
                1, serial, "id", "14", 1080, 1920, 1080, 1920, DisplaySizeSource.Physical);
        }

        public Task CancelOperationAsync(ulong operationId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControlledDelay : IAsyncDelay
    {
        private readonly object _gate = new();
        private TaskCompletionSource<object?> _callStarted = CreateSource();
        private TaskCompletionSource<bool>? _current;

        public async Task<object?> WaitForCallAsync()
        {
            TaskCompletionSource<object?> observed;
            lock (_gate)
            {
                observed = _callStarted;
            }

            var result = await observed.Task.WaitAsync(TimeSpan.FromSeconds(1));
            lock (_gate)
            {
                if (ReferenceEquals(_callStarted, observed))
                    _callStarted = CreateSource();
            }
            return result;
        }

        public void ReleaseCurrent()
        {
            lock (_gate)
            {
                _current?.TrySetResult(true);
            }
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _current = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _callStarted.TrySetResult(null);
                return _current.Task.WaitAsync(cancellationToken);
            }
        }

        private static TaskCompletionSource<object?> CreateSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
