using System;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Services;
using Xunit;

namespace Umamusume.AppCore.Tests;

public sealed class ConnectionStateServiceTests
{
    [Fact]
    public void DefaultStateIsDisconnected()
    {
        var service = new ConnectionStateService();
        Assert.Equal(ConnectionState.Disconnected, service.State);
        Assert.Null(service.LastVerifiedConnection);
        Assert.NotNull(service.ControlSession);
        Assert.Equal(ConnectionState.Disconnected, service.ControlSession.State);
    }

    [Fact]
    public void SetConnectedUpdatesStateAndSession()
    {
        var service = new ConnectionStateService();
        bool eventFired = false;
        service.StateChanged += (s, e) => eventFired = true;

        service.SetConnected("127.0.0.1:5555", 1080, 1920);

        Assert.True(eventFired);
        Assert.Equal(ConnectionState.Connected, service.State);
        Assert.NotNull(service.LastVerifiedConnection);
        Assert.Equal("127.0.0.1:5555", service.LastVerifiedConnection.Serial);
        Assert.Equal(1080, service.LastVerifiedConnection.Width);
        Assert.Equal(1920, service.LastVerifiedConnection.Height);

        Assert.NotNull(service.ControlSession);
        Assert.Equal("127.0.0.1:5555", service.ControlSession.Serial);
        Assert.Equal(1080, service.ControlSession.FrameWidth);
        Assert.Equal(1920, service.ControlSession.FrameHeight);
    }

    [Fact]
    public void SetDisconnectedResetsState()
    {
        var service = new ConnectionStateService();
        service.SetConnected("127.0.0.1:5555", 1080, 1920);
        Assert.Equal(ConnectionState.Connected, service.State);

        service.SetDisconnected();
        Assert.Equal(ConnectionState.Disconnected, service.State);
        Assert.NotNull(service.ControlSession);
        Assert.Equal(ConnectionState.Disconnected, service.ControlSession.State);
    }
}
