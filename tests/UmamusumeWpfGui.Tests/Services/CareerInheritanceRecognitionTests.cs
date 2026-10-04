using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerInheritanceRecognitionTests
{
    [Fact]
    public async Task Initial_resume_recognizes_GO_before_career_state_is_known()
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var observer = new CareerScreenObserver(InheritanceReplay.Create(out _));
        var state = new UraCareerSessionState();

        var observation = await observer.ObserveAsync(Connection(), pack, state,
            false, CancellationToken.None, careerOnly: true);

        Assert.Equal("inheritance_event", observation?.ScreenId);
        Assert.Equal(["inheritance_event", "inheritance_event"], observer.LastFrameScreenIds);
    }

    [Theory]
    [InlineData("goal_update", false)]
    [InlineData("race_runner_result", false)]
    [InlineData("career_main", false)]
    [InlineData("training_selection", false)]
    [InlineData("training_selection", true)]
    public async Task Visible_inheritance_GO_is_recognized_without_a_calendar_prediction(
        string previousScreen, bool pending)
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var state = State(previousScreen, pending);
        var runtime = InheritanceReplay.Create(out _);
        var observer = new CareerScreenObserver(runtime);

        var observation = await observer.ObserveAsync(Connection(), pack, state,
            false, CancellationToken.None);

        Assert.NotNull(observation);
        Assert.Equal("inheritance_event", observation.ScreenId);
        Assert.Contains("inheritance_event", observer.LastCandidateScreenIds);
        Assert.Equal(["inheritance_event", "inheritance_event"], observer.LastFrameScreenIds);
        Assert.Equal(CareerScreenKind.Event, observation.Kind);
        Console.WriteLine($"Inheritance after {previousScreen}, pending={pending}: score={observation.Score:0.000}.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recognized_GO_runs_the_json_action_and_releases_the_main_page(bool pending)
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var state = State("goal_update", pending);
        var runtime = InheritanceReplay.Create(out var replay);
        var observer = new CareerScreenObserver(runtime);
        var observation = await observer.ObserveAsync(Connection(), pack, state,
            false, CancellationToken.None);
        Assert.NotNull(observation);
        Assert.Equal("inheritance_event", observation.ScreenId);
        var runner = new HachimiJsonPipelineRunner(
            DispatchProxy.Create<IAdbRuntime, CareerGoalResumeTests.UnexpectedAdbRuntime>(),
            runtime, new JsonSettingsService(Path.Combine(Path.GetTempPath(),
                $"inheritance-replay-{Guid.NewGuid():N}.json")));

        var result = await new CareerFlowDispatcher(runtime, runner).DispatchAsync(
            Connection(), pack, true, new UraScenarioModule(pack), new UraDefaultStrategy(),
            "pace", state, observation, null, CareerEventHandlingModes.Default, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(["inheritance_event_go"], replay.TappedTasks);
        Assert.False(state.InheritanceEventPending);
        state.LastScreenId = "inheritance_event";
        var next = await observer.ObserveAsync(Connection(), pack, state,
            false, CancellationToken.None);
        Assert.Equal("career_main", next?.ScreenId);
    }

    [Theory]
    [InlineData("career_main_after_rest.png", "career_main")]
    [InlineData("training_selection_lv1_active-green_900x1600.png", "training_selection")]
    [InlineData("claw_machine_ready.png", "claw_machine")]
    [InlineData("claw_machine_result.png", "claw_machine_result")]
    [InlineData("career_skill_race_day_662_sample.png", "race_day")]
    [InlineData("current_mid_year1.png", "goal_objective_complete")]
    public async Task Normal_pages_do_not_match_the_inheritance_GO(string capture, string expectedScreen)
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var screen = pack.ScreenProfile.Find("inheritance_event")!;
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(root, capture));
        var template = GrayImageCodec.FromFile(pack.VisualResources!
            .ResolveScreenTemplate(screen, screen.Recognition.Template!));
        Assert.NotNull(frame);
        Assert.NotNull(template);

        var match = TemplateMatcher.FindColor(frame, template, screen.Recognition.Roi,
            screen.Recognition.TemplateThreshold, 900, 1600);

        Assert.False(match.Found, $"{capture} falsely matched inheritance GO at {match.Score:0.000}.");
        var observer = new CareerScreenObserver(InheritanceReplay.Create(frame));
        var state = State("race_runner_result", false);
        state.GoalCompletionProbeArmed = true;
        var observation = await observer.ObserveAsync(Connection(), pack, state,
            false, CancellationToken.None);
        Assert.Equal(expectedScreen, observation?.ScreenId);
        Assert.Contains("inheritance_event", observer.LastCandidateScreenIds);
    }

    [Fact]
    public async Task Pending_inheritance_still_blocks_the_underlying_main_page()
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var state = State("training_selection", true);
        Assert.False(CareerObservationPolicy.IsCandidate(pack.ScreenProfile.Find("career_main")!,
            pack.ScreenProfile, state, false, false, false, false, false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Asahi_Hai_goal_completion_does_not_recognize_or_click_inheritance_GO(bool initialResume)
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var screen = pack.ScreenProfile.Find("inheritance_event")!;
        var frame = GrayImageCodec.FromFile(Path.Combine(root, "tests", "UmamusumeWpfGui.Tests",
            "Fixtures", "Career", "goal-complete-asahi-hai.png"));
        var template = GrayImageCodec.FromFile(pack.VisualResources!
            .ResolveScreenTemplate(screen, screen.Recognition.Template!));
        Assert.NotNull(frame);
        Assert.NotNull(template);

        // The captured background + Next button reproduced the reported false hit.
        var oldMatch = TemplateMatcher.FindColor(frame, template, screen.Recognition.Roi,
            0.76, 900, 1600);
        Assert.True(oldMatch.Found);
        Assert.InRange(oldMatch.Score, 0.764, 0.765);
        Assert.Equal((328, 1194), (oldMatch.X, oldMatch.Y));

        var recognitionMatch = TemplateMatcher.FindColor(frame, template, screen.Recognition.Roi,
            screen.Recognition.TemplateThreshold, 900, 1600);
        Assert.False(recognitionMatch.Found);
        var action = pack.ExecutionDefinition.Tasks["inheritance_event_go"];
        var clickMatch = TemplateMatcher.FindColor(frame, template, action.Roi,
            action.TemplateThreshold, 900, 1600);
        Assert.False(clickMatch.Found);

        var observer = new CareerScreenObserver(InheritanceReplay.Create(frame));
        var state = initialResume ? new UraCareerSessionState() : State("race_runner_result", false);
        state.GoalCompletionProbeArmed = true;
        var observation = await observer.ObserveAsync(Connection(), pack, state,
            false, CancellationToken.None, careerOnly: initialResume);
        Assert.Equal("goal_objective_complete", observation?.ScreenId);
        Assert.Equal(["goal_objective_complete", "goal_objective_complete"], observer.LastFrameScreenIds);
    }

    private static UraCareerSessionState State(string previousScreen, bool pending) => new()
    {
        CareerStarted = true,
        LastScreenId = previousScreen,
        LastAction = UraPlannedAction.Race,
        TurnIndex = 29,
        TurnIndexSource = UraStateSource.Unknown,
        RaceReplayFlowCompleted = true,
        InheritanceEventPending = pending,
    };

    private static LastVerifiedConnection Connection() => new("adb", "serial", "android", "version",
        900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);

    public class InheritanceReplay : DispatchProxy
    {
        private GrayImage[] _frames = [];
        private GrayImage _after = null!;
        private int _captures;
        public List<string> TappedTasks { get; } = [];

        public static IVisualPipelineRuntime Create(GrayImage frame)
        {
            var runtime = Create(out var replay);
            replay._frames = [frame, frame];
            return runtime;
        }

        public static IVisualPipelineRuntime Create(out InheritanceReplay replay)
        {
            var runtime = Create<IVisualPipelineRuntime, InheritanceReplay>();
            replay = (InheritanceReplay)(object)runtime;
            var root = CareerTestResourceResolver.FindWorkspaceRoot();
            replay._frames = [Load("inheritance-go-bourbon.png"), Load("inheritance-go-bourbon-glow.png")];
            replay._after = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(root,
                "career_main_after_rest.png")) ?? throw new FileNotFoundException("Career Main fixture");
            return runtime;

            GrayImage Load(string file) => GrayImageCodec.FromFile(Path.Combine(root, "tests",
                "UmamusumeWpfGui.Tests", "Fixtures", "Career", file))
                ?? throw new FileNotFoundException(file);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "CaptureGrayAsync":
                    return Task.FromResult<GrayImage?>(TappedTasks.Count == 0
                        ? _frames[_captures++ % _frames.Length] : _after);
                case "LoadTemplateAsync":
                    return Task.FromResult(GrayImageCodec.FromFile(Path.Combine(
                        (string)args![1]!, (string)args[0]!)));
                case "DelayAsync": return Task.CompletedTask;
                case "DetectTextAsync": return Task.FromResult<ScreenTextRecognitionResult?>(null);
                case "WaitForColorMatchAsync":
                    Assert.Equal("inheritance_event_go", (string)args![8]!);
                    var template = GrayImageCodec.FromFile(Path.Combine((string)args[9]!, (string)args[1]!));
                    Assert.NotNull(template);
                    var match = TemplateMatcher.FindColor(_frames[0], template, (int[]?)args[2],
                        (double)args[3]!, (int)args[4]!, (int)args[5]!);
                    Assert.True(match.Found, $"Inheritance GO action score={match.Score:0.000}.");
                    return Task.FromResult<TemplateMatchResult?>(match);
                case "TapMatchAsync":
                    Assert.Equal("inheritance_event_go", (string)args![2]!);
                    TappedTasks.Add((string)args[2]!);
                    return Task.CompletedTask;
                default: throw new InvalidOperationException($"Unexpected inheritance call: {targetMethod?.Name}");
            }
        }
    }
}
