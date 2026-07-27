using System;
using System.Linq;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class ConnectionStateServiceTests
{
    // ================================================================
    // Initial state defaults
    // ================================================================

    [Fact]
    public void InitialState_IsDisconnected()
    {
        var svc = new ConnectionStateService();
        Assert.Equal(ConnectionState.Disconnected, svc.State);
    }

    [Fact]
    public void InitialState_LastVerifiedConnectionIsNull()
    {
        var svc = new ConnectionStateService();
        Assert.Null(svc.LastVerifiedConnection);
    }

    [Fact]
    public void InitialState_ControlSessionIsDisconnected()
    {
        var svc = new ConnectionStateService();
        Assert.NotNull(svc.ControlSession);
        Assert.Equal(ConnectionState.Disconnected, svc.ControlSession.State);
    }

    // ================================================================
    // State changes and notification
    // ================================================================

    [Fact]
    public void SetState_RaisesStateChanged()
    {
        var svc = new ConnectionStateService();
        ConnectionState? captured = null;
        svc.StateChanged += (_, _) => captured = svc.State;

        svc.SetState(ConnectionState.Connecting);

        Assert.Equal(ConnectionState.Connecting, captured);
    }

    [Fact]
    public void SetState_SameState_DoesNotRaiseStateChanged()
    {
        var svc = new ConnectionStateService();
        int callCount = 0;
        svc.StateChanged += (_, _) => callCount++;

        svc.SetState(ConnectionState.Disconnected); // same as default

        Assert.Equal(0, callCount);
    }

    [Fact]
    public void SetState_TransitionsInOrder_RaisesEach()
    {
        var svc = new ConnectionStateService();
        var transitions = new System.Collections.Generic.List<ConnectionState>();
        svc.StateChanged += (_, _) => transitions.Add(svc.State);

        svc.SetState(ConnectionState.Detecting);
        svc.SetState(ConnectionState.Connecting);
        svc.SetState(ConnectionState.Connected);

        Assert.Equal(3, transitions.Count);
        Assert.Equal([ConnectionState.Detecting, ConnectionState.Connecting, ConnectionState.Connected],
            transitions);
    }

    [Fact]
    public void SetState_FailedTransition_RaisesStateChanged()
    {
        var svc = new ConnectionStateService();
        ConnectionState? captured = null;
        svc.StateChanged += (_, _) => captured = svc.State;

        svc.SetState(ConnectionState.Failed);

        Assert.Equal(ConnectionState.Failed, captured);
    }

    [Fact]
    public void SetState_CancelingTransition_RaisesStateChanged()
    {
        var svc = new ConnectionStateService();
        svc.SetState(ConnectionState.Connecting);

        ConnectionState? captured = null;
        svc.StateChanged += (_, _) => captured = svc.State;

        svc.SetState(ConnectionState.Canceling);
        Assert.Equal(ConnectionState.Canceling, captured);
    }

    // ================================================================
    // LastVerifiedConnection — immutable snapshot
    // ================================================================

    [Fact]
    public void UpdateLastVerified_SetsProperty()
    {
        var svc = new ConnectionStateService();
        var now = DateTimeOffset.UtcNow;
        var record = new LastVerifiedConnection(
            @"C:\adb\adb.exe", "s1", "id1", "12", 100, 200, 100, 200, now);

        svc.UpdateLastVerified(record);

        Assert.NotNull(svc.LastVerifiedConnection);
        Assert.Equal(record, svc.LastVerifiedConnection);
    }

    [Fact]
    public void UpdateLastVerified_ReturnsImmutableSnapshot()
    {
        var svc = new ConnectionStateService();
        var now = DateTimeOffset.UtcNow;
        var record = new LastVerifiedConnection(
            "adb", "s1", "id1", "12", 100, 200, 100, 200, now);

        svc.UpdateLastVerified(record);

        // Verify the returned reference is the same record (records are immutable)
        var stored = svc.LastVerifiedConnection;
        Assert.Equal(record.AdbPath, stored!.AdbPath);
        Assert.Equal(record.Serial, stored.Serial);
        Assert.Equal(record.AndroidId, stored.AndroidId);
        Assert.Equal(record.VerifiedAt, stored.VerifiedAt);
    }

    // ================================================================
    // ControlSession — display state defaults and updates
    // ================================================================

    [Fact]
    public void UpdateControlSession_ReturnsNewSnapshot()
    {
        var svc = new ConnectionStateService();
        var snapshot = new ControlSessionSnapshot(
            "s1", null, 5, 1080, 1920, DateTimeOffset.UtcNow, ConnectionState.Connected);

        svc.UpdateControlSession(snapshot);

        Assert.NotNull(svc.ControlSession);
        Assert.Equal("s1", svc.ControlSession.Serial);
        Assert.Equal(ConnectionState.Connected, svc.ControlSession.State);
        Assert.Equal(1080, svc.ControlSession.FrameWidth);
    }

    [Fact]
    public void UpdateControlSession_DefaultIsDisconnected()
    {
        var svc = new ConnectionStateService();

        // Initial value
        Assert.NotNull(svc.ControlSession);
        Assert.Equal(ConnectionState.Disconnected, svc.ControlSession.State);
    }
}
