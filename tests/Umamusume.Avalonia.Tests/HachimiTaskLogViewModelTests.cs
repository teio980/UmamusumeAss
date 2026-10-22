using Umamusume.AppCore.Models;
using Umamusume.AppCore.Services;
using Umamusume.Avalonia.ViewModels;
using Umamusume.CoreBridge;
using Xunit;

namespace Umamusume.Avalonia.Tests;

public sealed class HachimiTaskLogViewModelTests
{
    private static HachimiTaskLogViewModel Create(string taskId = "mail-collection", string language = "en-US")
    {
        var localization = new LocalizationService { CurrentLanguage = language };
        var vm = new HachimiTaskLogViewModel(localization);
        vm.BeginRun([new TaskQueueItemViewModel(UmaTaskCatalog.Get(taskId), true, null, localization)]);
        return vm;
    }

    private static void Progress(HachimiTaskLogViewModel vm, string name, GameTaskEventType? type,
        string message = "score=0.99 target=(10,20) retry=3", string taskId = "mail-collection") =>
        vm.OnQueueEvent(new(0, taskId, UmaTaskQueueEventKind.Progress,
            1, 3, name, message, true, EventType: type), []);

    [Theory]
    [InlineData(GameTaskEventType.RecognitionEvaluated)]
    [InlineData(GameTaskEventType.ActionExecuting)]
    [InlineData(GameTaskEventType.ActionExecuted)]
    [InlineData(GameTaskEventType.TransitionVerified)]
    [InlineData(GameTaskEventType.TaskRetrying)]
    [InlineData(GameTaskEventType.MonitorTriggered)]
    [InlineData(GameTaskEventType.PipelineStarted)]
    [InlineData(GameTaskEventType.PipelineCompleted)]
    [InlineData(GameTaskEventType.PipelineFailed)]
    [InlineData(null)]
    public void TechnicalEventsDoNotEnterExecutionLogButStillUpdateProgress(GameTaskEventType? type)
    {
        var vm = Create();
        Progress(vm, "collectAll", type);

        Assert.Empty(vm.Tasks[0].Entries);
        Assert.Equal("Running (1/3)", vm.Tasks[0].DisplayStatus);
        vm.Clear();
    }

    [Theory]
    [InlineData("start-game", "HomeLoopMonitor")]
    [InlineData("team-race", "internalProbe")]
    [InlineData("daily-race", "internalProbe")]
    [InlineData("mail-collection", "internalProbe")]
    [InlineData("mission-collection", "homeVerify")]
    [InlineData("shop-purchase", "internalProbe")]
    [InlineData("career-training", "independent_agenda_year_first_year_probe")]
    public void InternalNodesAreHiddenEvenWhenEnteringCompletingOrFailing(string taskId, string name)
    {
        var vm = Create(taskId);
        foreach (var type in new[] { GameTaskEventType.TaskEntering, GameTaskEventType.TaskCompleted, GameTaskEventType.TaskFailed })
            Progress(vm, name, type, taskId: taskId);

        Assert.Empty(vm.Tasks[0].Entries);
        vm.Clear();
    }

    [Theory]
    [InlineData("en-US", "Confirming game home screen.", "Game home screen confirmed.")]
    [InlineData("zh-CN", "正在确认游戏主界面。", "游戏主界面已确认。")]
    public void SemanticStepsUseWindowsMessagesAndLanguage(string language, string started, string completed)
    {
        var vm = Create("start-game", language);
        Progress(vm, "CheckGameHome", GameTaskEventType.TaskEntering, taskId: "start-game");
        Progress(vm, "CheckGameHome", GameTaskEventType.RecognitionEvaluated, taskId: "start-game");
        Progress(vm, "CheckGameHome", GameTaskEventType.ActionExecuted, taskId: "start-game");
        Progress(vm, "CheckGameHome", GameTaskEventType.TaskCompleted, taskId: "start-game");

        Assert.Equal([started, completed], vm.Tasks[0].Entries.Select(entry => entry.Message));
        Assert.Equal(LogEntryKind.Success, vm.Tasks[0].Entries[^1].Kind);
        Assert.All(vm.Tasks[0].Entries, entry => Assert.DoesNotContain("CheckGameHome", entry.Step));
        vm.Clear();
    }

    [Fact]
    public void CareerActionsAreOnlyConfirmedAfterTransitionVerification()
    {
        var vm = Create("career-training");
        Progress(vm, "career_main.training.select", GameTaskEventType.ActionExecuted, taskId: "career-training");
        Assert.Empty(vm.Tasks[0].Entries);

        Progress(vm, "career_main.training.select", GameTaskEventType.TransitionVerified, taskId: "career-training");
        Assert.Equal("Career training confirmed.", Assert.Single(vm.Tasks[0].Entries).Message);
        vm.Clear();
    }

    [Theory]
    [InlineData("en-US", "Smart training scan", "Choice: Speed training")]
    [InlineData("zh-CN", "智能训练扫描", "选择【速度训练】")]
    public void SmartTrainingScanIsRenderedAsOneReadableSummary(string language, string title, string choice)
    {
        var vm = Create("career-training", language);
        Progress(vm, "smart_training_scan", GameTaskEventType.ActionExecuted,
            """{"date":"Classic July","energy":80,"chosen":"speed","candidates":[{"type":"speed","speed":21,"failure_rate":0,"score":36.5}]}""",
            "career-training");

        var entry = Assert.Single(vm.Tasks[0].Entries);
        Assert.Equal(title, entry.Step);
        Assert.Contains(choice, entry.Message);
        Assert.Contains("36.50", entry.Message);
        Assert.DoesNotContain("candidates", entry.Message);
        vm.Clear();
    }

    [Fact]
    public void TaskLifecycleDoesNotExposeIdsOrDuplicateFailuresInQueue()
    {
        var vm = Create();
        vm.OnQueueEvent(new(0, "mail-collection", UmaTaskQueueEventKind.Started, TaskName: "mail-collection"), []);
        vm.OnQueueEvent(new(0, "mail-collection", UmaTaskQueueEventKind.Failed,
            Message: "Timed out waiting for JSON task 'collectAll' after 10000ms."), []);

        Assert.Equal("Task", vm.Tasks[0].Entries[0].Step);
        Assert.Equal("The game did not show the required screen or action in time.", vm.Tasks[0].Entries[^1].Message);
        Assert.True(vm.Tasks[0].IsFailed);
        Assert.Empty(vm.QueueEntries);
        vm.EndRun(new(false, false, 0, 1, 0, "failed"));
        Assert.Single(vm.QueueEntries);
        vm.Clear();
    }

    [Fact]
    public void CareerStatusAndDecisionsRemainVisible()
    {
        var vm = Create("career-training");
        Progress(vm, "career_status", GameTaskEventType.ActionExecuted, "Energy 80%", "career-training");
        vm.OnQueueEvent(new(0, "career-training", UmaTaskQueueEventKind.Decision, Message: "Rest selected"), []);
        vm.OnQueueEvent(new(0, "career-training", UmaTaskQueueEventKind.ActionConfirmed,
            TaskName: "Rest", Succeeded: true), []);

        Assert.Equal(["Energy 80%", "Rest selected", "Rest confirmed."], vm.Tasks[0].Entries.Select(entry => entry.Message));
        vm.Clear();
    }
}
