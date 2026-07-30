using Umamusume.CoreBridge;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

public sealed class ConnectionHealthMonitor : IConnectionHealthMonitor
{
    // Keep device-loss detection within the documented 10-second window.
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly IAdbRunner _adbRunner;
    private readonly IWinAdapter _winAdapter;
    private readonly IUmaService _umaService;
    private readonly IAsyncDelay _asyncDelay;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;

    public ConnectionHealthMonitor(
        IAdbRunner adbRunner,
        IWinAdapter winAdapter,
        IUmaService umaService,
        IAsyncDelay asyncDelay)
    {
        ArgumentNullException.ThrowIfNull(adbRunner);
        ArgumentNullException.ThrowIfNull(winAdapter);
        ArgumentNullException.ThrowIfNull(umaService);
        ArgumentNullException.ThrowIfNull(asyncDelay);
        _adbRunner = adbRunner;
        _winAdapter = winAdapter;
        _umaService = umaService;
        _asyncDelay = asyncDelay;
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _worker is { IsCompleted: false };
            }
        }
    }

    public event Action<ConnectionHealthFailure>? Failed;

    public void Start(ConnectionHealthTarget target)
    {
        ValidateTarget(target);
        StopAsync().GetAwaiter().GetResult();

        lock (_gate)
        {
            _cancellation = new CancellationTokenSource();
            _worker = RunAsync(target, _cancellation.Token);
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation;
        Task? worker;
        lock (_gate)
        {
            cancellation = _cancellation;
            worker = _worker;
            _cancellation = null;
            _worker = null;
            cancellation?.Cancel();
        }

        if (worker is not null)
        {
            await worker.ConfigureAwait(false);
        }

        cancellation?.Dispose();
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task RunAsync(ConnectionHealthTarget target, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await _asyncDelay.DelayAsync(ProbeInterval, cancellationToken)
                    .ConfigureAwait(false);

                // Some emulator ADB servers keep answering get-state briefly
                // after the emulator process exits. Confirm the serial is
                // still present and ready in the authoritative device list
                // before treating the session as healthy.
                var devices = await _winAdapter.GetAdbDevicesAsync(
                    target.AdbPath,
                    cancellationToken).ConfigureAwait(false);
                if (!IsTargetReady(devices, target.Serial))
                {
                    var diagnostic = devices.Diagnostics.Count == 0
                        ? $"Device '{target.Serial}' is no longer present in adb devices."
                        : string.Join(
                            " | ",
                            devices.Diagnostics.Select(item => item.Message));
                    PublishFailure(new ConnectionHealthFailure(
                        target.Serial,
                        ConnectionErrorCode.DeviceDisconnected,
                        diagnostic));
                    return;
                }

                var probe = await _adbRunner.RunAsync(
                    target.AdbPath,
                    ["-s", target.Serial, "get-state"],
                    cancellationToken).ConfigureAwait(false);
                if (IsHealthy(probe))
                {
                    continue;
                }

                var recovery = await RecoverAsync(target, cancellationToken).ConfigureAwait(false);
                if (recovery.Success)
                {
                    continue;
                }

                PublishFailure(new ConnectionHealthFailure(
                    target.Serial,
                    ConnectionErrorCode.DeviceDisconnected,
                    recovery.Diagnostic));
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            PublishFailure(new ConnectionHealthFailure(
                target.Serial,
                ConnectionErrorCode.DeviceDisconnected,
                $"Connection health monitoring failed: {exception.Message}"));
        }
    }

    private async Task<(bool Success, string Diagnostic)> RecoverAsync(
        ConnectionHealthTarget target,
        CancellationToken cancellationToken)
    {
        var resolution = await _winAdapter.ResolveEndpointsAsync(
            target.AdbPath,
            target.ProfileName,
            cancellationToken).ConfigureAwait(false);
        if (!resolution.VerifiedEndpoints.Contains(target.Serial, StringComparer.Ordinal))
        {
            var diagnostics = string.Join(
                " | ",
                resolution.Diagnostics.Select(diagnostic => diagnostic.Message));
            return (
                false,
                string.IsNullOrEmpty(diagnostics)
                    ? $"Endpoint '{target.Serial}' was not verified during health recovery."
                    : $"Endpoint '{target.Serial}' was not verified during health recovery: {diagnostics}");
        }

        var result = await _umaService.ConnectAsync(
            target.AdbPath,
            target.Serial,
            target.ProfileName,
            cancellationToken).ConfigureAwait(false);
        return result switch
        {
            ConnectionSucceededEvent success when success.Serial == target.Serial =>
                (true, string.Empty),
            ConnectionFailedEvent failure =>
                (false, $"Health recovery failed: {failure.Message}"),
            _ => (false, "Health recovery returned an unexpected terminal event."),
        };
    }

    private void PublishFailure(ConnectionHealthFailure failure) => Failed?.Invoke(failure);

    private static bool IsHealthy(AdbCommandResult result) =>
        result.Error is null
        && !result.TimedOut
        && result.ExitCode == 0
        && string.Equals(result.Stdout.Trim(), "device", StringComparison.Ordinal);

    private static bool IsTargetReady(
        AdbDevicesResult devices,
        string serial) =>
        devices.Records.Any(record =>
            string.Equals(record.Serial, serial, StringComparison.Ordinal)
            && string.Equals(record.State, "device", StringComparison.OrdinalIgnoreCase));

    private static void ValidateTarget(ConnectionHealthTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target.AdbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.Serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.ProfileName);
    }
}
