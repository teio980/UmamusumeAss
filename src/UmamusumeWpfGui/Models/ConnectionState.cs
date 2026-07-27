namespace UmamusumeWpfGui.Models;

/// <summary>
/// Connection operation states for the state machine.
/// </summary>
public enum ConnectionState
{
    Idle,
    Detecting,
    Connecting,
    Connected,
    Disconnected,
    Failed,
    Canceling,
}
