using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerRaceAlarmRetryTests
{
    [Fact]
    public void Alarm_clock_setting_defaults_off_and_round_trips()
    {
        var settings = new CareerTrainingTaskSettingsViewModel();
        Assert.False(settings.RetryFailedRaceWithAlarmClock);

        settings.RetryFailedRaceWithAlarmClock = true;
        var exported = CareerTaskSettingsSerializer.Export(settings);
        Assert.True(exported["retryFailedRaceWithAlarmClock"]!.GetValue<bool>());

        CareerTaskSettingsSerializer.Import(settings, new JsonObject());
        Assert.False(settings.RetryFailedRaceWithAlarmClock);
        CareerTaskSettingsSerializer.Import(settings, exported);
        Assert.True(settings.RetryFailedRaceWithAlarmClock);
        settings.TraineeId = 1;
        Assert.True(CareerTaskSettingsMapper.ToNormalSettings(settings)
            .RetryFailedRaceWithAlarmClock);
    }

    [Fact]
    public async Task Captured_alarm_dialog_is_a_resume_checkpoint_and_not_a_runner_page()
    {
        var root = FindSolutionRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        var capture = GrayImageCodec.FromFile(Path.Combine(
            root, "testdata", "hachimi", "ura", "captures", "race_retry_alarm_dialog.png"));
        Assert.NotNull(capture);
        var observer = new CareerScreenObserver(FrameRuntime.Create(capture!));
        var connection = new LastVerifiedConnection(
            "adb", "serial", "android", "version", 900, 1600, 900, 1600,
            DateTimeOffset.UnixEpoch);

        var runtimeObservation = await observer.ObserveAsync(
            connection, pack, new UraCareerSessionState { CareerStarted = true },
            careerStartTransitionExpected: false, CancellationToken.None);
        var resumedObservation = await observer.ObserveAsync(
            connection, pack, new UraCareerSessionState(),
            careerStartTransitionExpected: false, CancellationToken.None,
            careerOnly: true);

        Assert.Equal("race_retry_dialog", runtimeObservation?.ScreenId);
        Assert.Equal("race_retry_dialog", resumedObservation?.ScreenId);
        Assert.True(CareerScreenObserver.IsInitialResumeCandidate("race_retry_dialog"));
    }

    [Fact]
    public async Task Alarm_dialog_template_does_not_match_an_ordinary_runner_or_result()
    {
        var root = FindSolutionRoot();
        var pack = await UraScenarioPackLoader.LoadAsync(Path.Combine(
            root, "resource", "hachimi", "ura", "manifest.json"));
        var screen = pack.ScreenProfile.Find("race_retry_dialog");
        Assert.NotNull(screen);
        var template = GrayImageCodec.FromFile(Path.Combine(
            root, "resource", "hachimi", "ura", "screens", "templates",
            "career", "race", "race_retry_dialog_title.png"));
        Assert.NotNull(template);

        foreach (var name in new[]
        {
            "senior_arima_race_stage.png",
            "debut_race_results_screen.png",
        })
        {
            var frame = GrayImageCodec.FromFile(Path.Combine(
                root, "testdata", "hachimi", "ura", "captures", name));
            Assert.NotNull(frame);
            var match = TemplateMatcher.Find(
                frame!, template!, screen!.Recognition.Roi,
                screen.Recognition.TemplateThreshold,
                pack.ScreenProfile.ReferenceWidth,
                pack.ScreenProfile.ReferenceHeight);
            Assert.False(match.Found, $"Retry title falsely matched {name}: {match.Score:0.000}");
        }
    }

    [Theory]
    [InlineData(false, "cancel")]
    [InlineData(true, "retry_with_alarm_clock")]
    public async Task Retry_dialog_uses_the_saved_choice_once(
        bool useClock, string expectedAction)
    {
        var actions = new RecordingActions();
        var flow = new CareerRaceFlow(FrameRuntime.Create(null), actions);
        var state = new UraCareerSessionState
        {
            RaceStrategyConfigured = true,
            RaceReplayFlowCompleted = true,
        };
        var context = new CareerFlowContext(
            null!, null!, true, null!, null!, "pace", state,
            new CareerObservation("race_retry_dialog", 1), null,
            CancellationToken.None,
            RetryFailedRaceWithAlarmClock: useClock);

        Assert.Null(await flow.HandleAsync(context));
        Assert.Null(await flow.HandleAsync(context));
        Assert.Equal(useClock
            ? [expectedAction]
            : [expectedAction, "result.next"], actions.Calls);
        Assert.Equal(!useClock, state.RaceRetryDeclined);
        Assert.Equal(!useClock, state.RaceReplayFlowCompleted);
        Assert.Equal(!useClock, state.RaceStrategyConfigured);
    }

    [Fact]
    public async Task Retry_reenters_the_existing_runner_flow_after_each_failure()
    {
        var actions = new RecordingActions();
        var state = new UraCareerSessionState
        {
            RaceStrategyConfigured = true,
            RaceReplayFlowCompleted = true,
        };
        var flow = new CareerRaceFlow(FrameRuntime.Create(null), actions);
        var dialog = new CareerFlowContext(
            null!, null!, true, null!, null!, "pace", state,
            new CareerObservation("race_retry_dialog", 1), null,
            CancellationToken.None,
            RetryFailedRaceWithAlarmClock: true);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Null(await flow.HandleAsync(dialog));
            state.RaceRetryDialogActionIssued = false; // the runner page replaced the dialog
            Assert.Null(await new CareerRaceRunnerCheckpointHandler(actions)
                .HandleAsync(dialog with
                {
                    Observation = new CareerObservation("race_runner", 1),
                }));
        }

        Assert.Equal(
            [
                "retry_with_alarm_clock", "strategy.apply.pace", "entry.view_results",
                "retry_with_alarm_clock", "strategy.apply.pace", "entry.view_results",
            ],
            actions.Calls);
    }

    [Fact]
    public async Task Declined_retry_advances_to_the_existing_settlement_flow()
    {
        var actions = new RecordingActions();
        var state = new UraCareerSessionState();
        var context = new CareerFlowContext(
            null!, null!, true, null!, null!, "pace", state,
            new CareerObservation("race_retry_dialog", 1), null,
            CancellationToken.None);

        Assert.Null(await new CareerRaceFlow(FrameRuntime.Create(null), actions)
            .HandleAsync(context));
        Assert.True(state.RaceRetryDeclined);
        Assert.True(state.RaceReplayFlowCompleted);
        Assert.Null(await new CareerSettlementFlow(actions)
            .HandleAsync(context with
            {
                Observation = new CareerObservation("complete_career", 1),
            }));
        Assert.Equal(["cancel", "result.next", "finish"], actions.Calls);
    }

    [Fact]
    public async Task Declined_retry_does_not_complete_replay_if_final_next_fails()
    {
        var actions = new RecordingActions { FailOnAction = "result.next" };
        var state = new UraCareerSessionState();
        var context = new CareerFlowContext(
            null!, null!, true, null!, null!, "pace", state,
            new CareerObservation("race_retry_dialog", 1), null,
            CancellationToken.None);

        var result = await new CareerRaceFlow(FrameRuntime.Create(null), actions)
            .HandleAsync(context);

        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.True(state.RaceRetryDeclined);
        Assert.False(state.RaceReplayFlowCompleted);
        Assert.Equal(["cancel", "result.next"], actions.Calls);
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "CMakePresets.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the solution root.");
    }

    private sealed class RecordingActions : ICareerFlowActionRunner
    {
        public List<string> Calls { get; } = [];
        public string? FailOnAction { get; init; }

        public Task<CareerTrainingResult?> RunAsync(
            CareerFlowContext context,
            string screenId,
            string actionId,
            HachimiPipelineRunOptions? options = null)
        {
            Calls.Add(actionId);
            return Task.FromResult<CareerTrainingResult?>(actionId == FailOnAction
                ? CareerRuntimeResults.Failure("Next flow failed", screenId)
                : null);
        }
    }

    public class FrameRuntime : DispatchProxy
    {
        private GrayImage? _frame;

        public static IVisualPipelineRuntime Create(GrayImage? frame)
        {
            var runtime = DispatchProxy.Create<IVisualPipelineRuntime, FrameRuntime>();
            ((FrameRuntime)(object)runtime)._frame = frame;
            return runtime;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "CaptureGrayAsync" => Task.FromResult(_frame),
                "LoadTemplateAsync" => Task.FromResult(LoadTemplate(args)),
                "DelayAsync" => Task.CompletedTask,
                "DetectTextAsync" => Task.FromResult<ScreenTextRecognitionResult?>(null),
                _ => throw new InvalidOperationException(
                    $"Unexpected visual runtime call: {targetMethod?.Name ?? "unknown"}"),
            };

        private static GrayImage? LoadTemplate(object?[]? args)
        {
            var templatePath = (string?)args?[0];
            var baseDirectory = (string?)args?[1];
            if (string.IsNullOrWhiteSpace(templatePath))
                return null;
            var path = Path.IsPathRooted(templatePath)
                ? templatePath
                : Path.Combine(baseDirectory ?? string.Empty, templatePath);
            return GrayImageCodec.FromFile(path);
        }
    }
}
