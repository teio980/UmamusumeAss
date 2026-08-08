using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class HachimiPipelineEditorItemTests
{
    [Fact]
    public void Task_editor_round_trips_arrays_lists_and_flags()
    {
        var item = HachimiPipelineTaskEditorItem.Create("tap");
        item.Action = "ClickSelf";
        item.Template = "templates/test.png";
        item.RoiText = "10, 20, 300, 400";
        item.NextText = "wait\ncomplete";
        item.OnErrorNextText = "retry";
        item.TemplateThresholdText = "0.91";
        item.Required = false;
        item.Success = true;

        var task = item.ToTask();

        Assert.Equal([10, 20, 300, 400], task.Roi!);
        Assert.Equal(["wait", "complete"], task.Next);
        Assert.Equal(["retry"], task.OnErrorNext);
        Assert.Equal(0.91, task.TemplateThreshold, 3);
        Assert.False(task.Required);
        Assert.True(task.Success);
    }

    [Fact]
    public void Timing_editor_round_trips_every_timing_field()
    {
        var timing = new HachimiPipelineTiming
        {
            NavigationMilliseconds = 1,
            MailboxLoadMilliseconds = 2,
            CollectionSettleMilliseconds = 3,
            HomeTimeoutMilliseconds = 4,
            HomeRetryTimeoutMilliseconds = 5,
            HomeVerifyTimeoutMilliseconds = 6,
            BackAttempts = 7,
            BackSettleMilliseconds = 8,
            PollIntervalMilliseconds = 9,
            TeamDownloadMilliseconds = 10,
            NextRaceLoadMilliseconds = 11,
            PlaybackLoadMilliseconds = 12,
            SkipSettleMilliseconds = 13,
            RaceTimeoutMilliseconds = 14,
            ShopProbeMilliseconds = 15,
            BetweenRacesMilliseconds = 16,
        };

        var roundTrip = HachimiPipelineTimingEditorItem.FromTiming(timing).ToTiming();

        Assert.Equal(timing.NavigationMilliseconds, roundTrip.NavigationMilliseconds);
        Assert.Equal(timing.MailboxLoadMilliseconds, roundTrip.MailboxLoadMilliseconds);
        Assert.Equal(timing.CollectionSettleMilliseconds, roundTrip.CollectionSettleMilliseconds);
        Assert.Equal(timing.HomeTimeoutMilliseconds, roundTrip.HomeTimeoutMilliseconds);
        Assert.Equal(timing.HomeRetryTimeoutMilliseconds, roundTrip.HomeRetryTimeoutMilliseconds);
        Assert.Equal(timing.HomeVerifyTimeoutMilliseconds, roundTrip.HomeVerifyTimeoutMilliseconds);
        Assert.Equal(timing.BackAttempts, roundTrip.BackAttempts);
        Assert.Equal(timing.BackSettleMilliseconds, roundTrip.BackSettleMilliseconds);
        Assert.Equal(timing.PollIntervalMilliseconds, roundTrip.PollIntervalMilliseconds);
        Assert.Equal(timing.TeamDownloadMilliseconds, roundTrip.TeamDownloadMilliseconds);
        Assert.Equal(timing.NextRaceLoadMilliseconds, roundTrip.NextRaceLoadMilliseconds);
        Assert.Equal(timing.PlaybackLoadMilliseconds, roundTrip.PlaybackLoadMilliseconds);
        Assert.Equal(timing.SkipSettleMilliseconds, roundTrip.SkipSettleMilliseconds);
        Assert.Equal(timing.RaceTimeoutMilliseconds, roundTrip.RaceTimeoutMilliseconds);
        Assert.Equal(timing.ShopProbeMilliseconds, roundTrip.ShopProbeMilliseconds);
        Assert.Equal(timing.BetweenRacesMilliseconds, roundTrip.BetweenRacesMilliseconds);
    }

    [Fact]
    public void Timing_editor_rejects_negative_values()
    {
        var item = HachimiPipelineTimingEditorItem.CreateDefault();
        item.RaceTimeoutMs = "-1";

        Assert.Throws<FormatException>(() => item.ToTiming());
    }
}
