using Avalonia.Threading;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Services;
using Umamusume.Avalonia.ViewModels;
using Xunit;

namespace Umamusume.Avalonia.Tests;

public sealed class LogsViewModelTests
{
    [Fact]
    public void ExistingAndIncomingLogsStayInChronologicalOrderAfterFiltering()
    {
        var service = new LogService();
        service.Log("first warning", LogEntryKind.Warning);
        service.Log("second info");
        var vm = new LogsViewModel(service, new LocalizationService { CurrentLanguage = "en-US" });

        Assert.Equal(["first warning", "second info"], vm.FilteredLogs.Select(entry => entry.Message));

        service.Log("third warning", LogEntryKind.Warning);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("third warning", vm.FilteredLogs[^1].Message);

        vm.SelectedLevel = "Warning";
        Assert.Equal(["first warning", "third warning"], vm.FilteredLogs.Select(entry => entry.Message));
        service.Log("fourth warning", LogEntryKind.Warning);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("fourth warning", vm.FilteredLogs[^1].Message);
        Assert.Equal(4, vm.TotalCount);
        Assert.Equal(3, vm.FilteredCount);
    }

    [Fact]
    public void TrimmingRemovesOldestLogsAndKeepsNewestAtBottom()
    {
        var service = new LogService();
        var vm = new LogsViewModel(service, new LocalizationService { CurrentLanguage = "en-US" });

        for (var index = 0; index < 1600; index++) service.Log($"entry {index}");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1500, vm.TotalCount);
        Assert.Equal(1000, vm.FilteredCount);
        Assert.Equal("entry 600", vm.FilteredLogs[0].Message);
        Assert.Equal("entry 1599", vm.FilteredLogs[^1].Message);

        vm.SearchQuery = "entry 1";
        Assert.Equal("entry 100", vm.FilteredLogs[0].Message);
        Assert.Equal("entry 1599", vm.FilteredLogs[^1].Message);
    }
}
