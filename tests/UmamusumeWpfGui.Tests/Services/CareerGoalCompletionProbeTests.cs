using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        Assert.Equal($"career_main.{action}", actions.Calls[0]);
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
    [InlineData("current_mid_year1.png", "goal_objective_complete", false)]
    [InlineData("year2_after_nhk_goal_update_ready.png", "goal_objective_complete", false)]
    [InlineData("ura_finale_entry.png", "goal_complete", true)]
    public async Task Armed_probe_recognizes_the_goal_banner_on_a_real_capture(
        string captureName,
        string expectedScreen,
        bool finaleTemplate)
    {
        var root = FindWorkspaceRoot();
        var pack = await LoadPackAsync();
        var framePath = finaleTemplate
            ? Path.Combine(root, "resource", "hachimi", "ura", "screens",
                "templates", "runtime_frames", captureName)
            : Path.Combine(root, "testdata", "hachimi", "ura", "captures",
                captureName);
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

    private static async Task<UraScenarioPack> LoadPackAsync() =>
        await UraScenarioPackLoader.LoadAsync(Path.Combine(
            FindWorkspaceRoot(), "resource", "hachimi", "ura", "manifest.json"));

    private static string FindWorkspaceRoot([CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[]
                 {
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory(),
                     Path.GetDirectoryName(sourceFile) ?? string.Empty,
                 })
        {
            if (string.IsNullOrWhiteSpace(start))
                continue;
            for (var directory = new DirectoryInfo(start);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName,
                        "resource", "hachimi", "ura", "manifest.json"))
                    && File.Exists(Path.Combine(directory.FullName,
                        "testdata", "hachimi", "ura", "captures",
                        "year2_after_nhk_goal_update_ready.png")))
                    return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate URA resources.");
    }

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
