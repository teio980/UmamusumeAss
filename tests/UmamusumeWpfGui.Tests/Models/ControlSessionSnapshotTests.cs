using System;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Tests.Models;

public sealed class ControlSessionSnapshotTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var snapshot = new ControlSessionSnapshot(
            Serial: "emulator-5554",
            TargetPackageId: "com.example.app",
            GeometryGeneration: 42,
            FrameWidth: 1080,
            FrameHeight: 1920,
            CapturedAt: capturedAt,
            State: ConnectionState.Connected);

        Assert.Equal("emulator-5554", snapshot.Serial);
        Assert.Equal("com.example.app", snapshot.TargetPackageId);
        Assert.Equal(42, snapshot.GeometryGeneration);
        Assert.Equal(1080, snapshot.FrameWidth);
        Assert.Equal(1920, snapshot.FrameHeight);
        Assert.Equal(capturedAt, snapshot.CapturedAt);
        Assert.Equal(ConnectionState.Connected, snapshot.State);
    }

    [Fact]
    public void IsImmutableRecord_ValueSemantics()
    {
        var a = new ControlSessionSnapshot("s1", null, 0, null, null, null, ConnectionState.Disconnected);
        var b = new ControlSessionSnapshot("s1", null, 0, null, null, null, ConnectionState.Disconnected);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a == b);
    }

    [Fact]
    public void RecordsWithSameValues_AreEqual()
    {
        var a = new ControlSessionSnapshot("s1", null, 0, null, null, null, ConnectionState.Disconnected);
        var b = new ControlSessionSnapshot("s1", null, 0, null, null, null, ConnectionState.Disconnected);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void RecordsWithNullTargetPackageId_AreValid()
    {
        var snapshot = new ControlSessionSnapshot(
            "serial", null, 0, null, null, null, ConnectionState.Disconnected);
        Assert.Null(snapshot.TargetPackageId);
    }

    [Fact]
    public void RecordsWithNullFrameDimensions_AreValid()
    {
        var snapshot = new ControlSessionSnapshot(
            "serial", null, 0, null, null, null, ConnectionState.Disconnected);
        Assert.Null(snapshot.FrameWidth);
        Assert.Null(snapshot.FrameHeight);
        Assert.Null(snapshot.CapturedAt);
    }
}
