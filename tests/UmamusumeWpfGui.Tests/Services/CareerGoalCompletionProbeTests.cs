using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerGoalCompletionProbeTests
{
    [Theory]
    [InlineData("training", CareerGoalTextParser.Fans)]
    [InlineData("rest", CareerGoalTextParser.Race)]
    [InlineData("recreation", CareerGoalTextParser.Completed)]
    [InlineData("infirmary", CareerGoalTextParser.Unknown)]
    public async Task Non_race_action_arms_the_goal_banner_probe_without_a_countdown(
        string action,
        string goalKind)
    {
        var pack = await LoadPackAsync();
        var scenario = new UraScenarioModule(pack);
        var state = scenario.CreateInitialState();
        state.ObservedGoalKind = goalKind;
        state.TurnsToGoal = 8;
        state.FansToGoal = null;
        state.HasPendingRace = false;
        state.Energy = UraObservedValueFactory.FromObservation(
            action == "rest" ? 20 : 80, 1);
        if (action == "recreation")
            state.Mood = UraObservedValueFactory.FromObservation(CareerMood.Normal, 1);

        var actions = new RecordingActions();
        var flow = new CareerTurnFlow(actions);
        var main = new CareerFlowContext(
            null!, pack, true, scenario, new UraDefaultStrategy(), string.Empty,
            state,
            new CareerObservation("career_main", 1,
                EnergyPercent: state.Energy.Value,
                InfirmaryAvailable: action == "infirmary"),
            null, CancellationToken.None);

        Assert.Null(await flow.HandleAsync(main));
        var expectedMainAction = action switch
        {
            "training" => "action.training",
            "rest" => "action.rest",
            "recreation" => "action.recreation",
            _ => "action.infirmary",
        };
        Assert.Equal($"career_main.{expectedMainAction}", actions.Calls[0]);
        Assert.True(state.GoalCompletionProbePending);
        Assert.False(state.GoalCompletionProbeArmed);

        var nextScreen = action switch
        {
            "training" => "training_selection",
            "rest" => "rest_confirmation",
            "recreation" => "recreation_selection",
            _ => "infirmary_confirmation",
        };
        Assert.Null(await flow.HandleAsync(main with
        {
            Observation = new CareerObservation(nextScreen, 1),
        }));
        Assert.False(state.GoalCompletionProbePending);
        Assert.True(state.GoalCompletionProbeArmed);
    }

    [Theory]
    [InlineData("current_mid_year1.png", "goal_objective_complete")]
    [InlineData("year2_after_nhk_goal_update_ready.png", "goal_objective_complete")]
    [InlineData("ura_finale_entry_boundary.png", "goal_objective_complete")]
    [InlineData("ura_finale_entry.png", "goal_complete")]
    public async Task Armed_probe_recognizes_the_goal_banner_on_a_real_capture(
        string captureName,
        string expectedScreen)
    {
        var root = FindWorkspaceRoot();
        var pack = await LoadPackAsync();
        var framePath = CareerTestResourceResolver.FindUraCapture(root, captureName);
        var frame = GrayImageCodec.FromFile(framePath);
        Assert.NotNull(frame);

        var observer = new CareerScreenObserver(FrameVisualRuntime.Create(frame!));
        var state = new UraCareerSessionState
        {
            CareerStarted = true,
            GoalCompletionProbeArmed = true,
            LastAction = UraPlannedAction.Training,
            LastScreenId = "training_selection",
        };
        var connection = new LastVerifiedConnection(
            "adb", "serial", "android", "version", 900, 1600, 900, 1600,
            DateTimeOffset.UnixEpoch);

        var observation = await observer.ObserveAsync(
            connection, pack, state, false, CancellationToken.None);

        Assert.Equal(expectedScreen, observation?.ScreenId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_recovery_can_recognize_a_goal_without_previous_action_state(
        bool resumeRecovery)
    {
        var pack = await LoadPackAsync();
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            FindWorkspaceRoot(), "current_mid_year1.png"));
        Assert.NotNull(frame);
        var observer = new CareerScreenObserver(FrameVisualRuntime.Create(frame));
        var state = new UraCareerSessionState
        {
            CareerStarted = true,
            LastScreenId = "career_main",
        };
        var connection = new LastVerifiedConnection(
            "adb", "serial", "android", "version", 900, 1600, 900, 1600,
            DateTimeOffset.UnixEpoch);

        var observation = await observer.ObserveAsync(
            connection, pack, state, false, CancellationToken.None,
            resumeRecovery: resumeRecovery);

        if (resumeRecovery)
            Assert.Equal("goal_objective_complete", observation?.ScreenId);
        else
            Assert.NotEqual("goal_objective_complete", observation?.ScreenId);
    }

    [Fact]
    public async Task Finale_goal_header_is_recognized_when_background_changes()
    {
        var root = FindWorkspaceRoot();
        var pack = await LoadPackAsync();
        var template = GrayImageCodec.FromFile(
            CareerTestResourceResolver.ResolveBuiltInUraVisualResource(
                "templates/runtime_frames/ura_finale_entry.png"));
        Assert.NotNull(template);

        var pixels = (byte[])template.Pixels.Clone();
        var rgba = (byte[])template.RgbaPixels!.Clone();
        for (var y = 0; y < template.Height; y++)
        {
            for (var x = 0; x < template.Width; x++)
            {
                if (x >= 288 && x < 621 && y >= 315 && y < 429)
                    continue;

                var index = y * template.Width + x;
                pixels[index] = (byte)(255 - pixels[index]);
                rgba[index * 4] = (byte)(255 - rgba[index * 4]);
                rgba[index * 4 + 1] = (byte)(255 - rgba[index * 4 + 1]);
                rgba[index * 4 + 2] = (byte)(255 - rgba[index * 4 + 2]);
            }
        }

        var frame = template with { Pixels = pixels, RgbaPixels = rgba };
        Assert.False(TemplateMatcher.Find(frame, template, null, 0.78, 900, 1600).Found);

        var observer = new CareerScreenObserver(FrameVisualRuntime.Create(frame));
        var state = new UraCareerSessionState
        {
            CareerStarted = true,
            GoalCompletionProbeArmed = true,
            LastAction = UraPlannedAction.Training,
            LastScreenId = "training_selection",
        };
        var connection = new LastVerifiedConnection(
            "adb", "serial", "android", "version", 900, 1600, 900, 1600,
            DateTimeOffset.UnixEpoch);

        var observation = await observer.ObserveAsync(
            connection, pack, state, false, CancellationToken.None);

        Assert.Equal("goal_complete", observation?.ScreenId);
    }

    [Theory]
    [InlineData("G1")]
    [InlineData("G2")]
    [InlineData("G3")]
    public void Graded_race_count_still_arms_after_the_final_required_race(
        string grade)
    {
        var state = new UraCareerSessionState
        {
            ObservedGoalKind = CareerGoalTextParser.GradeRaceCount,
            TargetRaceGrade = grade,
            GradeRaceTimesLeft = 2,
        };

        CareerRaceFlow.MarkReplayFlowCompleted(state);
        Assert.False(state.GoalCompletionProbeArmed);

        state.GradeRaceTimesLeft = 1;
        CareerRaceFlow.MarkReplayFlowCompleted(state);
        Assert.True(state.GoalCompletionProbeArmed);
    }

    private static Task<UraScenarioPack> LoadPackAsync() =>
        CareerTestResourceResolver.LoadBuiltInUraPackAsync();

    private static string FindWorkspaceRoot() =>
        CareerTestResourceResolver.FindWorkspaceRoot();

    private sealed class RecordingActions : ICareerFlowActionRunner
    {
        public List<string> Calls { get; } = [];

        public Task<CareerTrainingResult?> RunAsync(
            CareerFlowContext context,
            string screenId,
            string actionId,
            HachimiPipelineRunOptions? options = null)
        {
            Calls.Add($"{screenId}.{actionId}");
            return Task.FromResult<CareerTrainingResult?>(null);
        }
    }

    public class FrameVisualRuntime : DispatchProxy
    {
        public GrayImage Frame { get; set; } = null!;

        public static IVisualPipelineRuntime Create(GrayImage frame)
        {
            var runtime = DispatchProxy.Create<IVisualPipelineRuntime, FrameVisualRuntime>();
            ((FrameVisualRuntime)(object)runtime).Frame = frame;
            return runtime;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "CaptureGrayAsync" => Task.FromResult<GrayImage?>(Frame),
                "LoadTemplateAsync" => Task.FromResult(GrayImageCodec.FromFile(
                    Path.Combine((string)args![1]!, (string)args[0]!))),
                "DelayAsync" => Task.CompletedTask,
                _ => throw new InvalidOperationException(
                    $"Unexpected visual runtime call: {targetMethod?.Name}."),
            };
    }
}
