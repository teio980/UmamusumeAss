using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class HachimiTaskLogViewModelTests
{
    [Theory]
    [InlineData("Queue completed")]
    [InlineData("Queue failed")]
    [InlineData("Queue canceled")]
    public void TotalRuntimeTracksTheQueueAndStaysFixedAfterItEnds(string status)
    {
        var clock = new ManualTimeProvider();
        var log = new HachimiTaskLogViewModel(clock);
        Assert.Equal("00:00:00", log.TotalRunDurationText);
        log.BeginRun(
            [("0:start-game", "Start Game")],
            "Pending", "Running", "Completed", "Failed", "Canceled", "Skipped", "Queue started");

        clock.Advance(TimeSpan.FromSeconds(12));
        // Queue preparation is included before any task starts.
        Assert.Equal("00:00:12", log.TotalRunDurationText);
        log.SetTaskStatus("0:start-game", HachimiTaskLogStatus.Running);
        clock.Advance(TimeSpan.FromSeconds(18));
        log.SetRunStatus(status);
        var notifications = new List<string?>();
        log.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        log.EndRun();

        clock.Advance(TimeSpan.FromMinutes(1));
        log.EndRun();
        Assert.Equal(TimeSpan.FromSeconds(30), log.TotalRunDuration);
        Assert.Equal("00:00:30", log.TotalRunDurationText);
        Assert.Contains(nameof(log.TotalRunDurationText), notifications);
    }

    [Fact]
    public void TotalRuntimeKeepsHoursBeyondOneDayAndResetsOnTheNextRun()
    {
        var clock = new ManualTimeProvider();
        var log = new HachimiTaskLogViewModel(clock);
        log.BeginRun(
            [("0:start-game", "Start Game")],
            "Pending", "Running", "Completed", "Failed", "Canceled", "Skipped", "Queue started");
        clock.Advance(new TimeSpan(25, 2, 3));
        Assert.Equal("25:02:03", log.TotalRunDurationText);
        log.EndRun();

        log.BeginRun(
            [("0:start-game", "Start Game")],
            "Pending", "Running", "Completed", "Failed", "Canceled", "Skipped", "Queue started");
        Assert.Equal("00:00:00", log.TotalRunDurationText);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("00:00:01", log.TotalRunDurationText);
        log.EndRun();
    }

    [Fact]
    public void Run_progress_stays_visible_after_completion_and_language_changes()
    {
        var log = new HachimiTaskLogViewModel();
        log.BeginRun(
            [("0:career-training", "Career Training")],
            "Pending", "Running", "Completed", "Failed", "Canceled", "Skipped", "Queue started");
        var group = Assert.Single(log.Tasks);
        log.SetTaskStatus(group.TaskId, HachimiTaskLogStatus.Running);
        log.SetTaskProgress(group.TaskId, new GrassTaskExecutionProgress(1, 3));
        Assert.Equal("Running · 1/3", group.DisplayStatus);

        log.SetTaskProgress(group.TaskId, new GrassTaskExecutionProgress(3, 3));
        log.SetTaskStatus(group.TaskId, HachimiTaskLogStatus.Completed);
        Assert.Equal("Completed · 3/3", group.DisplayStatus);
        log.RefreshStatusText("等待", "运行中", "已完成", "失败", "取消", "跳过");
        Assert.Equal("已完成 · 3/3", group.DisplayStatus);

        log.BeginRun(
            [("0:career-training", "Career Training")],
            "Pending", "Running", "Completed", "Failed", "Canceled", "Skipped", "Queue started");
        Assert.Equal("Pending", Assert.Single(log.Tasks).DisplayStatus);
    }

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

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}

