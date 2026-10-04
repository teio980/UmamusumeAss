using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerRaceStreakWarningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Captured_warning_is_recognized_before_empty_races_or_underlying_race_day(bool resume)
    {
        var pack = await LoadPackAsync();
        var observer = new CareerScreenObserver(FrameRuntime.Create(WarningFrame()));
        var state = new UraCareerSessionState
        {
            CareerStarted = !resume,
            LastScreenId = resume ? "unknown" : "race_day",
        };

        var observation = await observer.ObserveAsync(
            Connection(), pack, state, false, CancellationToken.None, careerOnly: resume);

        Assert.Equal("race_streak_warning", observation?.ScreenId);
        Assert.Equal(CareerScreenKind.Race, observation?.Kind);
        Assert.True(CareerScreenObserver.IsInitialResumeCandidate("race_streak_warning"));
    }

    [Theory]
    [InlineData("race_streak_warning.png")]
    [InlineData("ura_finish_confirmed.png")]
    public async Task Empty_race_text_rejects_the_warning_and_finish_dialog(string capture)
    {
        var pack = await LoadPackAsync();
        var recognition = pack.ScreenProfile.Find("race_list_empty")!.Recognition;
        var template = Load(pack.VisualResources!.ResolveScreenTemplate(
            pack.ScreenProfile.Find("race_list_empty")!, recognition.Template!));
        var frame = Load(CareerTestResourceResolver.FindUraCapture(
            CareerTestResourceResolver.FindWorkspaceRoot(), capture));

        var match = TemplateMatcher.FindColor(frame, template, recognition.Roi,
            recognition.TemplateThreshold, 900, 1600, requireTextContrast: true);

        Assert.False(match.Found, $"Empty races matched {capture}: score={match.Score:0.000}.");
    }

    [Fact]
    public async Task Genuine_empty_race_text_remains_recognizable()
    {
        var pack = await LoadPackAsync();
        var screen = pack.ScreenProfile.Find("race_list_empty")!;
        var template = Load(pack.VisualResources!.ResolveScreenTemplate(
            screen, screen.Recognition.Template!));
        var frame = new GrayImage(900, 1600,
            Enumerable.Repeat((byte)255, 900 * 1600).ToArray(),
            Enumerable.Repeat((byte)255, 900 * 1600 * 4).ToArray());
        Stamp(frame, template, 225, 1000);
        pack = pack with { ScreenProfile = new UraScreenProfile { Screens = [screen] } };

        var observation = await new CareerScreenObserver(FrameRuntime.Create(frame)).ObserveAsync(
            Connection(), pack, new UraCareerSessionState { CareerStarted = true },
            false, CancellationToken.None);

        Assert.Equal("race_list_empty", observation?.ScreenId);
    }

    [Theory]
    [InlineData(CareerGoalTextParser.Race, "career", null, true)]
    [InlineData(CareerGoalTextParser.Unknown, "finale_underway", null, true)]
    [InlineData(CareerGoalTextParser.Fans, "career", 1, true)]
    [InlineData(CareerGoalTextParser.GradeRaceCount, "career", 1, true)]
    [InlineData(CareerGoalTextParser.Fans, "career", 9, false)]
    [InlineData(CareerGoalTextParser.Unknown, "career", null, false)]
    public async Task Warning_confirms_required_races_once_and_preserves_optional_race_protection(
        string goalKind, string phase, int? turnsToGoal, bool confirm)
    {
        var scenario = new UraScenarioModule(await LoadPackAsync());
        var state = scenario.CreateInitialState();
        state.ObservedGoalKind = goalKind;
        state.PhaseId = phase;
        state.TurnsToGoal = turnsToGoal;
        state.FansToGoal = 1739;
        var actions = new RecordingActions();
        var flow = new CareerRaceFlow(FrameRuntime.Create(WarningFrame()), actions);
        var context = new CareerFlowContext(null!, null!, true, scenario,
            new UraDefaultStrategy(), string.Empty, state,
            new CareerObservation("race_streak_warning", 1), null, CancellationToken.None);

        var result = await flow.HandleAsync(context);

        Assert.True(state.HasPendingRace);
        Assert.Equal(2, state.ConsecutiveRaceTurns);
        Assert.Null(state.RaceUnavailableTurnIndex);
        Assert.Equal(1739, state.FansToGoal);
        if (confirm)
        {
            Assert.Null(result);
            Assert.Null(await flow.HandleAsync(context));
            Assert.Equal(["race_streak_warning.race.streak.confirm"], actions.Calls);
            Assert.Equal(state.IsFinale ? UraPlannedAction.FinaleRace : UraPlannedAction.Race,
                state.PendingTurnAction);
        }
        else
        {
            Assert.Null(result);
            Assert.Null(await flow.HandleAsync(context));
            Assert.Equal(["race_streak_warning.race.streak.cancel"], actions.Calls);
            Assert.Null(state.PendingTurnAction);
        }
    }

    [Fact]
    public async Task Resume_on_warning_recovers_the_required_goal_from_the_underlying_banner()
    {
        var pack = await LoadPackAsync();
        var observer = new CareerScreenObserver(FrameRuntime.Create(
            WarningFrame(), "Place top 3 in Arima Kinen"));
        var state = new UraCareerSessionState();
        CareerRaceStreakPolicy.InitializeForRun(state, continueExistingCareer: true);
        var observation = await observer.ObserveAsync(Connection(), pack, state,
            false, CancellationToken.None, careerOnly: true);
        Assert.Equal("race_streak_warning", observation?.ScreenId);
        var scenario = new UraScenarioModule(pack, useTraineeObjectives: false);

        scenario.ObserveScreen(state, observation!.ScreenId, observation.Score,
            goalText: observation.GoalText);

        Assert.Equal(CareerGoalTextParser.Race, state.ObservedGoalKind);
        Assert.True(state.HasPendingRace);
        Assert.True(CareerRaceStreakPolicy.MustRaceNow(scenario, state));
    }

    [Fact]
    public async Task Warning_action_rechecks_the_message_and_clicks_a_tight_opaque_ok_template()
    {
        var pack = await LoadPackAsync();
        var warning = pack.ScreenProfile.Find("race_streak_warning")!;
        var probeId = warning.Actions
            .Single(action => action.SemanticId == "race.streak.confirm").Task;
        var probe = pack.ExecutionDefinition.GetTask(probeId);
        var confirm = pack.ExecutionDefinition.GetTask(probe.Next.Single());
        Assert.Equal("MatchTemplateColorText", probe.Algorithm);
        Assert.Equal("JustReturn", probe.Action);
        Assert.Equal("MatchTemplateColorText", confirm.Algorithm);
        Assert.Equal("ClickSelf", confirm.Action);
        foreach (var taskId in new[] { probeId, probe.Next.Single() })
        {
            var task = pack.ExecutionDefinition.GetTask(taskId);
            var template = Load(pack.VisualResources!.ResolveTaskTemplate(taskId));
            Assert.InRange(template.Width, 1, 300);
            Assert.InRange(template.Height, 1, 45);
            Assert.All(template.RgbaPixels!.Where((_, index) => index % 4 == 3),
                alpha => Assert.Equal((byte)255, alpha));
            var match = TemplateMatcher.FindColor(WarningFrame(), template, task.Roi,
                task.TemplateThreshold, 900, 1600, requireTextContrast: true);
            Assert.True(match.Found, $"{task.Template}: score={match.Score:0.000}.");
        }
    }

    private static void Stamp(GrayImage frame, GrayImage template, int x, int y)
    {
        for (var row = 0; row < template.Height; row++)
        {
            Array.Copy(template.Pixels, row * template.Width,
                frame.Pixels, (y + row) * frame.Width + x, template.Width);
            Array.Copy(template.RgbaPixels!, row * template.Width * 4,
                frame.RgbaPixels!, ((y + row) * frame.Width + x) * 4, template.Width * 4);
        }
    }

    private static LastVerifiedConnection Connection() => new(
        "adb", "serial", "android", "version", 900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);
    private static GrayImage WarningFrame() => Load(CareerTestResourceResolver.FindUraCapture(
        CareerTestResourceResolver.FindWorkspaceRoot(), "race_streak_warning.png"));
    private static GrayImage Load(string path) => GrayImageCodec.FromFile(path)
        ?? throw new FileNotFoundException("Missing test image.", path);
    private static Task<UraScenarioPack> LoadPackAsync() =>
        CareerTestResourceResolver.LoadBuiltInUraPackAsync();

    private sealed class RecordingActions : ICareerFlowActionRunner
    {
        public List<string> Calls { get; } = [];
        public Task<CareerTrainingResult?> RunAsync(CareerFlowContext context,
            string screenId, string actionId, HachimiPipelineRunOptions? options = null)
        {
            Calls.Add($"{screenId}.{actionId}");
            return Task.FromResult<CareerTrainingResult?>(null);
        }
    }

    public class FrameRuntime : DispatchProxy
    {
        private GrayImage _frame = null!;
        private string? _goal;
        public static IVisualPipelineRuntime Create(GrayImage frame, string? goal = null)
        {
            var runtime = Create<IVisualPipelineRuntime, FrameRuntime>();
            var proxy = (FrameRuntime)(object)runtime;
            proxy._frame = frame;
            proxy._goal = goal;
            return runtime;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "CaptureGrayAsync" => Task.FromResult<GrayImage?>(_frame),
                "LoadTemplateAsync" => Task.FromResult(GrayImageCodec.FromFile((string)args![0]!)),
                "DelayAsync" => Task.CompletedTask,
                "DetectTextAsync" => Task.FromResult<ScreenTextRecognitionResult?>(_goal is null
                    ? null
                    : new ScreenTextRecognitionResult(
                        [new ScreenTextDetection(_goal, new ScreenTextRect(315, 90, 540, 75), 1)],
                        "en-US", 900, 1600)),
                _ => throw new InvalidOperationException($"Unexpected call: {targetMethod?.Name}"),
            };
    }
}
