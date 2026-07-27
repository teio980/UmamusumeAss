using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// Shared singleton that tracks connection operation state, the immutable
/// last-verified snapshot, and the current S2 control-session display snapshot.
/// </summary>
public interface IConnectionStateService
{
    /// <summary>Current connection operation state.</summary>
    ConnectionState State { get; }

    /// <summary>Immutable last-verified connection record, or null if none.</summary>
    LastVerifiedConnection? LastVerifiedConnection { get; }

    /// <summary>Display-only S2 control session snapshot.</summary>
    ControlSessionSnapshot? ControlSession { get; }

    /// <summary>Raised when <see cref="State"/> changes to a different value.</summary>
    event EventHandler? StateChanged;

    /// <summary>Transitions the operation state and raises <see cref="StateChanged"/>.</summary>
    void SetState(ConnectionState newState);

    /// <summary>Stores a new immutable last-verified connection record.</summary>
    void UpdateLastVerified(LastVerifiedConnection record);

    /// <summary>Clears the last-verified connection record.</summary>
    void ClearLastVerified();
}
