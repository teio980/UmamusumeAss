using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// Default implementation of <see cref="IConnectionStateService"/>.
/// Shared singleton across ViewModels. Tracks operation state, immutable
/// last-verified connection data, and the current S2 control-session snapshot.
/// </summary>
public sealed class ConnectionStateService : IConnectionStateService
{
    private ConnectionState _state = ConnectionState.Disconnected;
    private LastVerifiedConnection? _lastVerified;
    private ControlSessionSnapshot? _controlSession;

    public ConnectionState State => _state;

    public LastVerifiedConnection? LastVerifiedConnection => _lastVerified;

    public ControlSessionSnapshot? ControlSession => _controlSession;

    public event EventHandler? StateChanged;

    /// <summary>
    /// Initializes with a default disconnected control session snapshot.
    /// </summary>
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

    /// <summary>
    /// Transitions the state. Does not raise <see cref="StateChanged"/>
    /// if the new value equals the current state.
    /// </summary>
    public void SetState(ConnectionState newState)
    {
        if (_state == newState)
            return;

        _state = newState;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Stores a new immutable last-verified connection record.
    /// </summary>
    public void UpdateLastVerified(LastVerifiedConnection record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _lastVerified = record;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Updates the current S2 control-session display snapshot.
    /// </summary>
    public void UpdateControlSession(ControlSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _controlSession = snapshot;
    }

    /// <summary>
    /// Clears the last-verified connection record.
    /// </summary>
    public void ClearLastVerified()
    {
        _lastVerified = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
