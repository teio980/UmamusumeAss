using Umamusume.CoreBridge;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// Adapts native device-session disconnect events to the connection-health contract used by
/// the Settings view model. Device probing and session recovery belong to the native core.
/// </summary>
public sealed class ConnectionHealthMonitor : IConnectionHealthMonitor
{
    private readonly object _gate = new();
    private readonly IUmaService _umaService;
    private ConnectionHealthTarget? _target;

    public ConnectionHealthMonitor(IUmaService umaService)
    {
        ArgumentNullException.ThrowIfNull(umaService);
        _umaService = umaService;
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _target is not null;
            }
        }
    }

    public event Action<ConnectionHealthFailure>? Failed;

    public void Start(ConnectionHealthTarget target)
    {
        ValidateTarget(target);

        lock (_gate)
        {
            _umaService.ConnectionEventReceived -= OnConnectionEvent;
            _target = target;
            _umaService.ConnectionEventReceived += OnConnectionEvent;
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            _umaService.ConnectionEventReceived -= OnConnectionEvent;
            _target = null;
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private void OnConnectionEvent(ConnectionEvent connectionEvent)
    {
        if (connectionEvent is DeviceReconnectedEvent reconnected)
        {
            lock (_gate)
            {
                ConnectionHealthTarget? current = _target;
                if (current is not null
                    && string.Equals(current.Serial, reconnected.Serial, StringComparison.Ordinal)
                    && (current.SessionGeneration is null
                        || current.SessionGeneration == reconnected.PreviousSessionGeneration))
                {
                    _target = current with { SessionGeneration = reconnected.SessionGeneration };
                }
            }
            return;
        }

        if (connectionEvent is not DeviceDisconnectedEvent disconnected)
            return;

        ConnectionHealthTarget? target;
        lock (_gate)
        {
            target = _target;
            if (target is null
                || (target.SessionGeneration is ulong expectedGeneration
                    && disconnected.SessionGeneration != expectedGeneration))
            {
                return;
            }

            // Clear the active session before notifying listeners. A synchronous handler may
            // immediately start a replacement connection or stop this monitor.
            _target = null;
            _umaService.ConnectionEventReceived -= OnConnectionEvent;
        }

        Failed?.Invoke(new ConnectionHealthFailure(
            target.Serial,
            disconnected.ErrorCode,
            $"Native device session {disconnected.SessionGeneration} disconnected."));
    }

    private static void ValidateTarget(ConnectionHealthTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.AdbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.Serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.ProfileName);
    }
}
