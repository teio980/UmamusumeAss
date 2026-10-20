using System.Collections.Generic;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Services;
using Xunit;

namespace Umamusume.AppCore.Tests;

public sealed class LogServiceTests
{
    [Fact]
    public void LoggingAppendsEntriesAndRaisesEvent()
    {
        var service = new LogService();
        var received = new List<LogEntry>();
        service.LogReceived += entry => received.Add(entry);

        service.Log("Message 1", LogEntryKind.Normal, "CategoryA");
        service.Log("Message 2", LogEntryKind.Warning, "CategoryB");

        Assert.Equal(2, service.Entries.Count);
        Assert.Equal(2, received.Count);
        Assert.Equal("Message 1", service.Entries[0].Message);
        Assert.Equal(LogEntryKind.Normal, service.Entries[0].Kind);
        Assert.Equal("CategoryA", service.Entries[0].Category);
        Assert.Equal("Message 2", service.Entries[1].Message);
        Assert.Equal(LogEntryKind.Warning, service.Entries[1].Kind);
        Assert.Equal("CategoryB", service.Entries[1].Category);
    }

    [Fact]
    public void ClearRemovesAllEntries()
    {
        var service = new LogService();
        service.Log("Message 1");
        service.Log("Message 2");
        Assert.Equal(2, service.Entries.Count);

        service.Clear();
        Assert.Empty(service.Entries);
    }
}
