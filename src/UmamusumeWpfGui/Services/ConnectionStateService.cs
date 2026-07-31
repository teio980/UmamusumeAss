using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;






public sealed class ConnectionStateService : IConnectionStateService
{
    private readonly object _gate = new();
    private ConnectionState _state = ConnectionState.Disconnected;
    private LastVerifiedConnection? _lastVerified;
    private ControlSessionSnapshot? _controlSession;

    public ConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public LastVerifiedConnection? LastVerifiedConnection
    {
        get
        {
            lock (_gate)
            {
                return _lastVerified;
            }
        }
    }

    public ControlSessionSnapshot? ControlSession
    {
        get
        {
            lock (_gate)
            {
                return _controlSession;
            }
        }
    }

    public event EventHandler? StateChanged;




    public ConnectionStateService()
    {
        _controlSession = new ControlSessionSnapshot(
            Serial: "",
            TargetPackageId: null,
            GeometryGeneration: 0,
            FrameWidth: null,
            FrameHeight: null,
            CapturedAt: null,
            State: ConnectionState.Disconnected);
    }





    public void SetState(ConnectionState newState)
    {
        lock (_gate)
        {
            if (_state == newState)
                return;

            _state = newState;
            if (_controlSession is { } session && session.State != newState)
            {
                _controlSession = session with { State = newState };
            }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }




    public void UpdateLastVerified(LastVerifiedConnection record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            _lastVerified = record;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }




    public void UpdateControlSession(ControlSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            _controlSession = snapshot;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }




    public void ClearLastVerified()
    {
        lock (_gate)
        {
            _lastVerified = null;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
