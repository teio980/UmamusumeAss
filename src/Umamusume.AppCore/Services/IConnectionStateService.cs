using System;
using Umamusume.AppCore.Models;

namespace Umamusume.AppCore.Services;

public interface IConnectionStateService
{
    ConnectionState State { get; }
    LastVerifiedConnection? LastVerifiedConnection { get; }
    ControlSessionSnapshot? ControlSession { get; }
    event EventHandler? StateChanged;

    void SetState(ConnectionState newState);
    void SetConnected(string serial, int width, int height);
    void SetDisconnected();
}

public sealed class ConnectionStateService : IConnectionStateService
{
    private readonly object _gate = new();
    private ConnectionState _state = ConnectionState.Disconnected;
    private LastVerifiedConnection? _lastVerified;
    private ControlSessionSnapshot? _controlSession;

    public ConnectionState State
    {
        get { lock (_gate) return _state; }
    }

    public LastVerifiedConnection? LastVerifiedConnection
    {
        get { lock (_gate) return _lastVerified; }
    }

    public ControlSessionSnapshot? ControlSession
    {
        get { lock (_gate) return _controlSession; }
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
            if (_state == newState) return;
            _state = newState;
            if (_controlSession is { } session && session.State != newState)
            {
                _controlSession = session with { State = newState };
            }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetConnected(string serial, int width, int height)
    {
        lock (_gate)
        {
            _state = ConnectionState.Connected;
            _lastVerified = new LastVerifiedConnection(
                Serial: serial,
                Width: width,
                Height: height,
                PhysicalWidth: width,
                PhysicalHeight: height,
                VerifiedAt: DateTime.UtcNow);
            _controlSession = new ControlSessionSnapshot(
                Serial: serial,
                TargetPackageId: null,
                GeometryGeneration: 1,
                FrameWidth: width,
                FrameHeight: height,
                CapturedAt: DateTime.UtcNow,
                State: ConnectionState.Connected);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetDisconnected()
    {
        lock (_gate)
        {
            _state = ConnectionState.Disconnected;
            _controlSession = new ControlSessionSnapshot(
                Serial: "",
                TargetPackageId: null,
                GeometryGeneration: 0,
                FrameWidth: null,
                FrameHeight: null,
                CapturedAt: null,
                State: ConnectionState.Disconnected);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
