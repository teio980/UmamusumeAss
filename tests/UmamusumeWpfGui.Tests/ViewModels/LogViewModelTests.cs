using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class LogViewModelTests
{
    [Fact]
    public void InitialEntriesAreEmpty()
    {
        using var viewModel = new LogViewModel();

        Assert.Empty(viewModel.Entries);
    }

    [Fact]
    public void AddStoresHachimiLogEntry()
    {
        using var viewModel = new LogViewModel();
        var before = DateTimeOffset.UtcNow;

        viewModel.Add("Task queue", "Task completed", LogEntryKind.Success);

        var entry = Assert.Single(viewModel.Entries);
        Assert.InRange(entry.Timestamp, before, DateTimeOffset.UtcNow);
        Assert.Equal("Task queue", entry.Type);
        Assert.Equal("Task completed", entry.Details);
        Assert.Equal(LogEntryKind.Success, entry.Kind);
    }

    [Fact]
    public void ClearRemovesAllEntries()
    {
        using var viewModel = new LogViewModel();
        viewModel.Add("Task", "Running");

        viewModel.Clear();

        Assert.Empty(viewModel.Entries);
    }

    [Fact]
    public void EntryCapDropsOldestEntry()
    {
        using var viewModel = new LogViewModel();

        for (var i = 0; i < 501; i++)
            viewModel.Add(
                "Task",
                i.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(500, viewModel.Entries.Count);
        Assert.Equal("1", viewModel.Entries[0].Details);
    }

    [Fact]
    public void DisposeStopsFurtherWrites()
    {
        using var viewModel = new LogViewModel();
        viewModel.Dispose();

        viewModel.Add("Task", "Ignored");
        viewModel.Clear();

        Assert.Empty(viewModel.Entries);
    }

    [Fact]
    public void DisposeCanBeCalledMoreThanOnce()
    {
        using var viewModel = new LogViewModel();

        var exception = Record.Exception(() =>
        {
            viewModel.Dispose();
            viewModel.Dispose();
        });

        Assert.Null(exception);
    }
}
