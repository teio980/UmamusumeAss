using System;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Tests.Models;

public sealed class LogEntryTests
{




    [Fact]
    public void Constructor_SetsTimestampNearUtcNow()
    {
        var before = DateTimeOffset.UtcNow;
        var entry = new LogEntry(
            DateTimeOffset.UtcNow, "ConnectionStarted", "details", LogEntryKind.Info);
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(entry.Timestamp, before, after);
    }

    [Fact]
    public void Constructor_SetsType()
    {
        var entry = new LogEntry(
            DateTimeOffset.UtcNow, "ConnectionStarted", "details", LogEntryKind.Info);

        Assert.Equal("ConnectionStarted", entry.Type);
    }

    [Fact]
    public void Constructor_SetsDetails()
    {
        var entry = new LogEntry(
            DateTimeOffset.UtcNow, "ConnectionStarted", "Some details here", LogEntryKind.Info);

        Assert.Equal("Some details here", entry.Details);
    }

    [Fact]
    public void Constructor_SetsKind()
    {
        var entry = new LogEntry(
            DateTimeOffset.UtcNow, "ConnectionSucceeded", "details", LogEntryKind.Success);

        Assert.Equal(LogEntryKind.Success, entry.Kind);
    }





    [Fact]
    public void Kind_DefaultsToInfo()
    {
        var entry = new LogEntry(
            DateTimeOffset.UtcNow, "ConnectionStarted", "details", LogEntryKind.Info);

        Assert.Equal(LogEntryKind.Info, entry.Kind);
    }





    [Fact]
    public void LogEntry_IsRecordWithValueEquality()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new LogEntry(now, "Type", "Details", LogEntryKind.Info);
        var b = new LogEntry(now, "Type", "Details", LogEntryKind.Info);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void LogEntry_DifferentType_NotEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new LogEntry(now, "TypeA", "Details", LogEntryKind.Info);
        var b = new LogEntry(now, "TypeB", "Details", LogEntryKind.Info);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void LogEntry_DifferentKind_NotEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new LogEntry(now, "Type", "Details", LogEntryKind.Info);
        var b = new LogEntry(now, "Type", "Details", LogEntryKind.Failure);

        Assert.NotEqual(a, b);
    }
}
