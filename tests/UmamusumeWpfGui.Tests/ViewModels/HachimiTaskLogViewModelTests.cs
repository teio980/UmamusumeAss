using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class HachimiTaskLogViewModelTests
{
    [Fact]
    public void BeginRunCreatesOrderedGroupsAndClearsThePreviousRun()
    {
        var log = new HachimiTaskLogViewModel();
        log.BeginRun(
            [
                ("0:team-race", "Team Race"),
                ("1:team-race", "Team Race copy"),
            ],
            "Pending",
            "Running",
            "Completed",
            "Failed",
            "Canceled",
            "Skipped",
            "Queue started");
        log.AddTaskStep("0:team-race", "Action", "Clicked Team Race", HachimiTaskLogEventKind.Action);
        log.SetTaskStatus("0:team-race", HachimiTaskLogStatus.Completed);

        Assert.Equal(2, log.Tasks.Count);
        Assert.Equal(1, log.Tasks[0].Order);
        Assert.Equal(2, log.Tasks[1].Order);
        Assert.Single(log.Tasks[0].Entries);
        Assert.Equal(HachimiTaskLogStatus.Completed, log.Tasks[0].Status);
        Assert.Equal(HachimiTaskLogStatus.Pending, log.Tasks[1].Status);

        log.BeginRun(
            [("0:mail-collection", "Mail collection")],
            "Pending",
            "Running",
            "Completed",
            "Failed",
            "Canceled",
            "Skipped",
            "Queue started");

        Assert.Single(log.Tasks);
        Assert.Empty(log.Tasks[0].Entries);
        Assert.Equal("mail-collection", log.Tasks[0].TaskId.Split(':')[1]);
    }

    [Fact]
    public void GroupSinkWritesOnlyToItsOwnTaskGroup()
    {
        var log = new HachimiTaskLogViewModel();
        log.BeginRun(
            [
                ("0:start-game", "Start Game"),
                ("1:daily-race", "Daily Race"),
            ],
            "Pending",
            "Running",
            "Completed",
            "Failed",
            "Canceled",
            "Skipped",
            "Queue started");

        log.ForTask("1:daily-race").Add(
            "Selection",
            "Selected the configured runner",
            HachimiTaskLogEventKind.Success);

        Assert.Empty(log.Tasks[0].Entries);
        Assert.Single(log.Tasks[1].Entries);
        Assert.Equal("Selection", log.Tasks[1].Entries[0].Step);
    }
}

