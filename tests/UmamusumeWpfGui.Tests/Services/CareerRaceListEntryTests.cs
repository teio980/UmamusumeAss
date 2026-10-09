using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerRaceListEntryTests
{
    [Theory]
    [InlineData("year2_nhk_race_list.png", "race_list")]
    [InlineData("year2_insufficient_fans_race_list.png", "race_list")]
    [InlineData("race_streak_warning.png", "race_streak_warning")]
    // Despite the corpus filename, this frame is Career Main before entry.
    [InlineData("ura_prelim_race_list.png", null)]
    public async Task Recorded_entry_frames_use_the_bounded_recognition(string capture, string? expectedScreen)
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        // The corpus resolver also knows cropped templates with some of these names;
        // use the complete game frame for an entry replay.
        var frame = GrayImageCodec.FromFile(Path.Combine(root, "testdata", "hachimi", "ura", "captures", capture));
        Assert.NotNull(frame);
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, FramesRuntime>();
        var runtime = (FramesRuntime)(object)visual;
        runtime.ReadFiles = true;
        runtime.Frames = [frame, frame];
        var state = new UraCareerSessionState { CareerStarted = true, LastScreenId = "race_day" };
        var transition = new CareerRaceListEntryTransition("race_day", new ManualClock());
        state.Runtime.RaceListEntryTransition = transition;

        var result = await transition.ObserveAsync(new CareerScreenObserver(visual), Connection, pack,
            state, null, CancellationToken.None);

        Assert.Equal(expectedScreen, result?.ScreenId);
        Assert.Equal(expectedScreen is null, state.Runtime.RaceListEntryTransition is not null);
        Assert.Equal(0, transition.FullRecognitionCount);
        Assert.Equal(2, runtime.CaptureCount);
    }

    [Fact]
    public async Task Stable_list_uses_two_frames_and_skips_unrelated_screens()
    {
        var fixture = new Fixture();
        fixture.Runtime.Frames = [fixture.Frame("race_list"), fixture.Frame("race_list")];
        var observation = await fixture.ObserveAsync();

        Assert.Equal("race_list", observation?.ScreenId);
        Assert.Equal(CareerScreenKind.Race, observation?.Kind);
        Assert.Null(fixture.State.Runtime.RaceListEntryTransition);
        Assert.Equal(2, fixture.Transition.CaptureCount);
        Assert.Equal(0, fixture.Transition.FullRecognitionCount);
        Assert.DoesNotContain("custom_overlay.png", fixture.Runtime.LoadedTemplates);
        Assert.DoesNotContain("race_day.png", fixture.Runtime.LoadedTemplates);
    }

    [Theory]
    [InlineData("race_recommendations")]
    [InlineData("race_list_empty")]
    [InlineData("race_streak_warning")]
    public async Task Overlay_takes_precedence_over_underlying_list(string overlay)
    {
        var fixture = new Fixture();
        var frame = fixture.Frame("race_list", overlay);
        fixture.Runtime.Frames = [frame, frame];

        Assert.Equal(overlay, (await fixture.ObserveAsync())?.ScreenId);
        Assert.Equal(overlay == "race_recommendations",
            fixture.State.Runtime.RaceListEntryTransition is not null);
    }

    [Fact]
    public async Task Different_frames_do_not_dispatch_the_list()
    {
        var fixture = new Fixture();
        fixture.Runtime.Frames = [fixture.Frame("race_list"), fixture.Frame("race_list", "race_recommendations")];

        Assert.Null(await fixture.ObserveAsync());
        Assert.Same(fixture.Transition, fixture.State.Runtime.RaceListEntryTransition);
        Assert.False(fixture.Transition.RetryRequested);
    }

    [Fact]
    public async Task Dropped_screenshot_does_not_prove_a_stable_list()
    {
        var fixture = new Fixture();
        fixture.Runtime.Frames = [null, fixture.Frame("race_list")];
        fixture.Runtime.FirstCaptureException = new IOException("dropped screenshot");

        Assert.Null(await fixture.ObserveAsync());
        Assert.Single(fixture.Observer.LastCaptureErrors);
        Assert.Equal(2, fixture.Transition.CaptureCount);
    }

    [Fact]
    public async Task Slow_loading_falls_back_to_full_recognition_for_an_unexpected_overlay()
    {
        var fixture = new Fixture();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        var frame = fixture.Frame("custom_overlay");
        fixture.Runtime.Frames = [frame, frame];

        Assert.Equal("custom_overlay", (await fixture.ObserveAsync())?.ScreenId);
        Assert.Equal(1, fixture.Transition.FullRecognitionCount);
        Assert.Null(fixture.State.Runtime.RaceListEntryTransition);
    }

    [Fact]
    public async Task Full_recognition_is_spaced_out_while_loading()
    {
        var fixture = new Fixture();
        fixture.Runtime.Frames = [fixture.Frame()];
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Null(await fixture.ObserveAsync());
        Assert.Equal(1, fixture.Transition.FullRecognitionCount);
        Assert.Null(await fixture.ObserveAsync());
        Assert.Equal(1, fixture.Transition.FullRecognitionCount);
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Null(await fixture.ObserveAsync());
        Assert.Equal(2, fixture.Transition.FullRecognitionCount);
    }

    [Theory]
    [InlineData("career_main")]
    [InlineData("race_day")]
    [InlineData("race_recommendations")]
    public async Task Unchanged_source_and_visible_button_allow_only_one_retry(string source)
    {
        var fixture = new Fixture(source);
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        var frame = fixture.Frame(source, "entry_button");
        fixture.Runtime.Frames = [frame, frame];

        Assert.Equal(source, (await fixture.ObserveAsync())?.ScreenId);
        Assert.True(fixture.Transition.RetryRequested);
        fixture.Transition.ActionCompleted(source);
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Null(await fixture.ObserveAsync());
        Assert.False(fixture.Transition.RetryRequested);
        Assert.False(fixture.Transition.RequestRetry());
    }

    [Theory]
    [InlineData("career_main", true)]
    [InlineData("career_main", false)]
    [InlineData("race_day", true)]
    [InlineData("race_day", false)]
    public async Task Changing_source_or_missing_button_never_allows_a_retry(string source, bool changing)
    {
        var fixture = new Fixture(source);
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        var first = fixture.Frame(changing ? [source, "entry_button"] : [source]);
        var second = fixture.Frame(changing ? [source, "entry_button"] : [source]);
        if (changing)
            second.Pixels[^1] ^= 255;
        fixture.Runtime.Frames = [first, second, first, second];

        Assert.Null(await fixture.ObserveAsync());
        Assert.False(fixture.Transition.RetryRequested);
    }

    [Fact]
    public async Task Recommendations_confirmation_keeps_the_original_deadline_and_suppresses_stale_popup()
    {
        var fixture = new Fixture("race_recommendations");
        fixture.Clock.Advance(TimeSpan.FromSeconds(25));
        fixture.Transition.ActionCompleted("race_recommendations");
        var frame = fixture.Frame("race_recommendations");
        fixture.Runtime.Frames = [frame, frame];

        Assert.Null(await fixture.ObserveAsync());
        Assert.Equal(0, fixture.Transition.FullRecognitionCount);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        var captures = fixture.Runtime.CaptureCount;
        Assert.Null(await fixture.ObserveAsync());
        Assert.True(fixture.Transition.Expired);
        Assert.Equal(captures, fixture.Runtime.CaptureCount);
    }

    [Fact]
    public async Task Cancellation_and_daily_reset_interrupt_the_entry_observer()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Transition.ObserveAsync(fixture.Observer, Connection, fixture.Pack,
                fixture.State, null, cancellation.Token));
        Assert.Equal(0, fixture.Runtime.CaptureCount);

        fixture.Runtime.FirstCaptureException = new DateChangedInterruptionException();
        await Assert.ThrowsAsync<DateChangedInterruptionException>(() => fixture.ObserveAsync());
    }

    [Fact]
    public async Task Race_day_action_is_guarded_and_a_verified_retry_reuses_the_same_transition()
    {
        var fixture = new Fixture();
        fixture.State.Runtime.RaceListEntryTransition = null;
        fixture.State.PendingTurnAction = UraPlannedAction.Race;
        var actions = new RecordingActions();
        var flow = new CareerRaceFlow(fixture.Visual, actions);
        var context = fixture.Context("race_day");

        Assert.Null(await flow.HandleAsync(context));
        var transition = fixture.State.Runtime.RaceListEntryTransition;
        Assert.NotNull(transition);
        Assert.Null(await flow.HandleAsync(context));
        Assert.Single(actions.ActionIds);
        Assert.True(transition.RequestRetry());
        Assert.Null(await flow.HandleAsync(context));
        Assert.Null(await flow.HandleAsync(context));
        Assert.Equal(2, actions.ActionIds.Count);
        Assert.Same(transition, fixture.State.Runtime.RaceListEntryTransition);
        Assert.False(transition.RetryRequested);
    }

    [Fact]
    public async Task Failed_entry_action_does_not_arm_a_transition()
    {
        var fixture = new Fixture();
        fixture.State.Runtime.RaceListEntryTransition = null;
        fixture.State.PendingTurnAction = UraPlannedAction.Race;
        var failure = new CareerTrainingResult(false, "tap failed", 0, "race_day");
        var flow = new CareerRaceFlow(fixture.Visual, new RecordingActions { Result = failure });

        Assert.Same(failure, await flow.HandleAsync(fixture.Context("race_day")));
        Assert.Null(fixture.State.Runtime.RaceListEntryTransition);
    }

    [Fact]
    public async Task Recommended_popup_confirmation_is_not_sent_twice()
    {
        var fixture = new Fixture();
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        var actions = new RecordingActions();
        var flow = new CareerRaceFlow(fixture.Visual, actions);

        Assert.Null(await flow.HandleAsync(fixture.Context("race_recommendations")));
        Assert.Null(await flow.HandleAsync(fixture.Context("race_recommendations")));
        Assert.Single(actions.ActionIds);
        Assert.Equal("race_recommendations", fixture.Transition.SourceScreenId);
        Assert.Equal(TimeSpan.FromSeconds(2), fixture.Transition.Elapsed);
    }

    [Fact]
    public async Task Main_race_entry_arms_fast_observation_for_a_recorded_list()
    {
        var context = await MainContextAsync(new RecordingStrategy());
        var actions = new RecordingActions();
        Assert.Null(await new CareerTurnFlow(actions).HandleAsync(context));
        var transition = context.State.Runtime.RaceListEntryTransition;
        Assert.NotNull(transition);
        Assert.Equal("career_main", transition.SourceScreenId);
        Assert.Equal(UraPlannedAction.Race, context.State.PendingTurnAction);

        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var frame = GrayImageCodec.FromFile(Path.Combine(root, "testdata", "hachimi", "ura", "captures",
            "year2_insufficient_fans_race_list.png"));
        Assert.NotNull(frame);
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, FramesRuntime>();
        var runtime = (FramesRuntime)(object)visual;
        runtime.ReadFiles = true;
        runtime.Frames = [frame, frame];
        var observer = new CareerScreenObserver(visual);

        var result = await transition.ObserveAsync(observer, Connection, context.Pack,
            context.State, null, CancellationToken.None);

        Assert.Equal("race_list", result?.ScreenId);
        Assert.Null(context.State.Runtime.RaceListEntryTransition);
        Assert.Equal(0, transition.FullRecognitionCount);
        Assert.Equal(2, runtime.CaptureCount);
        Assert.DoesNotContain("career_main", observer.LastCandidateScreenIds);
        Assert.DoesNotContain("training_selection", observer.LastCandidateScreenIds);
    }

    [Fact]
    public async Task Main_entry_wait_and_retry_never_choose_or_start_another_turn()
    {
        var strategy = new RecordingStrategy();
        var context = await MainContextAsync(strategy);
        var actions = new RecordingActions();
        var flow = new CareerTurnFlow(actions);
        Assert.Null(await flow.HandleAsync(context));
        var transition = context.State.Runtime.RaceListEntryTransition;
        Assert.NotNull(transition);
        var turnBaseline = context.State.PendingActionTurnIndex;
        strategy.Action = UraPlannedAction.Rest;

        Assert.Null(await flow.HandleAsync(context));
        Assert.Single(actions.ActionIds);
        Assert.Equal(1, strategy.ChooseCount);
        Assert.True(transition.RequestRetry());
        Assert.Null(await flow.HandleAsync(context));
        Assert.Null(await flow.HandleAsync(context));

        Assert.Equal(["action.races", "action.races"], actions.ActionIds);
        Assert.Equal(1, strategy.ChooseCount);
        Assert.Equal(turnBaseline, context.State.PendingActionTurnIndex);
        Assert.Equal(UraPlannedAction.Race, context.State.PendingTurnAction);
        Assert.Same(transition, context.State.Runtime.RaceListEntryTransition);
        Assert.False(transition.RequestRetry());
    }

    [Fact]
    public async Task Failed_main_entry_does_not_arm_or_commit_the_turn()
    {
        var context = await MainContextAsync(new RecordingStrategy());
        var failure = new CareerTrainingResult(false, "tap failed", 0, "career_main");
        var flow = new CareerTurnFlow(new RecordingActions { Result = failure });

        Assert.Same(failure, await flow.HandleAsync(context));
        Assert.Null(context.State.Runtime.RaceListEntryTransition);
        Assert.Null(context.State.PendingTurnAction);
    }

    [Theory]
    [InlineData(UraPlannedAction.Rest)]
    [InlineData(UraPlannedAction.Training)]
    public async Task Other_main_actions_do_not_arm_race_entry(UraPlannedAction action)
    {
        var context = await MainContextAsync(new RecordingStrategy { Action = action });
        Assert.Null(await new CareerTurnFlow(new RecordingActions()).HandleAsync(context));
        Assert.Null(context.State.Runtime.RaceListEntryTransition);
        Assert.Equal(action, context.State.PendingTurnAction);
    }

    private static async Task<CareerFlowContext> MainContextAsync(RecordingStrategy strategy)
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var scenario = new UraScenarioModule(pack);
        var state = scenario.CreateInitialState();
        state.CareerStarted = true;
        state.PhaseId = "career";
        state.LastScreenId = "career_main";
        state.TurnIndex = 20;
        state.ObservedGoalKind = CareerGoalTextParser.Fans;
        state.FansToGoal = 864;
        state.TurnsToGoal = 3;
        state.Energy = UraObservedValueFactory.FromObservation(100, 1);
        return new CareerFlowContext(Connection, pack, true, scenario, strategy, "pace", state,
            new CareerObservation("career_main", 1, EnergyPercent: 100), null, CancellationToken.None);
    }

    [Fact]
    public async Task Faster_entry_polls_do_not_exhaust_the_generic_recognition_retry_limit()
    {
        var runtime = new CareerRuntimeState
        {
            RaceListEntryTransition = new CareerRaceListEntryTransition("race_day"),
        };
        var observations = 0;
        var bindings = new CareerRuntimeLoopBindings(
            () => false,
            _ => Task.FromResult(new CareerRuntimeStep()),
            (_, _, _) =>
            {
                if (++observations < 4)
                    return Task.FromResult<CareerObservation?>(null);
                runtime.RaceListEntryTransition = null;
                return Task.FromResult<CareerObservation?>(new("race_list", 1));
            },
            (observation, _) => Task.FromResult(new CareerRuntimeStep(
                new CareerTrainingResult(true, "ready", 0, observation.ScreenId))),
            _ => throw new InvalidOperationException("Loading must not exhaust the poll count."),
            (_, _) => Task.CompletedTask);

        var result = await CareerRuntimeLoop.RunAsync(runtime, bindings, 0, null, false,
            CancellationToken.None, recognitionRetryLimit: 0);
        Assert.True(result.Succeeded);
        Assert.Equal(4, observations);
    }

    private static LastVerifiedConnection Connection => new(
        "adb", "race-list-test", "android", "version", 32, 32, 32, 32, DateTimeOffset.UnixEpoch);

    private sealed class Fixture
    {
        private readonly Dictionary<string, (int X, int Y)> _positions = new()
        {
            ["race_list"] = (0, 0), ["race_list_empty"] = (0, 8),
            ["race_recommendations"] = (0, 16), ["race_streak_warning"] = (0, 24),
            ["race_day"] = (16, 0), ["custom_overlay"] = (16, 8), ["entry_button"] = (16, 16),
            ["career_main"] = (16, 24), ["main_training"] = (24, 24),
        };
        public ManualClock Clock { get; } = new();
        public UraCareerSessionState State { get; } = new() { CareerStarted = true, LastScreenId = "race_day" };
        public CareerRaceListEntryTransition Transition { get; }
        public IVisualPipelineRuntime Visual { get; }
        public FramesRuntime Runtime { get; }
        public CareerScreenObserver Observer { get; }
        public UraScenarioPack Pack { get; }

        public Fixture(string source = "race_day")
        {
            Visual = DispatchProxy.Create<IVisualPipelineRuntime, FramesRuntime>();
            Runtime = (FramesRuntime)(object)Visual;
            var screens = new List<UraScreenDefinition>();
            var random = new Random(31);
            foreach (var (id, position) in _positions)
            {
                var pixels = new byte[64];
                random.NextBytes(pixels);
                Runtime.Templates[id + ".png"] = new GrayImage(8, 8, pixels);
                if (id is "entry_button" or "main_training")
                    continue;
                screens.Add(new UraScreenDefinition
                {
                    ScreenId = id, Flow = id switch
                    {
                        "custom_overlay" => "event", "career_main" => "main", _ => "race",
                    },
                    Recognition = new UraScreenRecognition
                    {
                        Template = id + ".png", Roi = [position.X, position.Y, 8, 8],
                        Priority = id switch
                        {
                            "custom_overlay" => -4, "race_streak_warning" => -2,
                            "race_recommendations" => -1, "race_list_empty" => 10,
                            "race_list" => 11, _ => 8,
                        },
                        TemplateThreshold = 0.99,
                        RequiredTemplate = id == "career_main" ? "main_training.png" : null,
                        RequiredTemplateRoi = id == "career_main" ? [24, 24, 8, 8] : null,
                        RequiredTemplateThreshold = 0.99,
                    },
                    Actions = id is "career_main" or "race_day" or "race_recommendations"
                        ? [new UraScreenAction
                        {
                            SemanticId = id switch
                            {
                                "career_main" => "action.races", "race_day" => "race.open_list",
                                _ => "race.recommendations.confirm",
                            },
                            Task = "entry",
                        }] : [],
                });
            }
            Pack = new UraScenarioPack("", AppContext.BaseDirectory, "", "", null!, null!, null!, null!,
                new UraEventDocument(), new UraScreenProfile
                {
                    ReferenceWidth = 32, ReferenceHeight = 32, Screens = screens,
                }, new HachimiPipelineDefinition
                {
                    ReferenceWidth = 32, ReferenceHeight = 32,
                    Tasks = new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["entry"] = new HachimiPipelineTask
                        {
                            Template = "entry_button.png", Roi = [16, 16, 8, 8], TemplateThreshold = 0.99,
                        },
                    },
                });
            Observer = new CareerScreenObserver(Visual);
            Transition = new CareerRaceListEntryTransition(source, Clock);
            State.Runtime.RaceListEntryTransition = Transition;
        }

        public GrayImage Frame(params string[] markers)
        {
            var pixels = Enumerable.Repeat((byte)255, 32 * 32).ToArray();
            foreach (var marker in markers.Contains("career_main") ? markers.Append("main_training") : markers)
            {
                var (x, y) = _positions[marker];
                var template = Runtime.Templates[marker + ".png"];
                for (var row = 0; row < 8; row++)
                    Array.Copy(template.Pixels, row * 8, pixels, (y + row) * 32 + x, 8);
            }
            return new GrayImage(32, 32, pixels);
        }

        public Task<CareerObservation?> ObserveAsync() =>
            Transition.ObserveAsync(Observer, Connection, Pack, State, null, CancellationToken.None);

        public CareerFlowContext Context(string screen) => new(Connection, Pack, true, null!, null!, "pace",
            State, new CareerObservation(screen, 1), null, CancellationToken.None);
    }

    public class FramesRuntime : DispatchProxy
    {
        public GrayImage?[] Frames { get; set; } = [];
        public int CaptureCount { get; private set; }
        public Exception? FirstCaptureException { get; set; }
        public bool ReadFiles { get; set; }
        public Dictionary<string, GrayImage> Templates { get; } = new();
        public List<string> LoadedTemplates { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "CaptureGrayAsync":
                    if (CaptureCount++ == 0 && FirstCaptureException is { } exception)
                        throw exception;
                    return Task.FromResult(Frames.Length == 0 ? null : Frames[Math.Min(CaptureCount - 1, Frames.Length - 1)]);
                case "LoadTemplateAsync":
                    var name = Path.GetFileName((string)args![0]!);
                    LoadedTemplates.Add(name);
                    return Task.FromResult(ReadFiles ? GrayImageCodec.FromFile((string)args[0]!)
                        : Templates.GetValueOrDefault(name));
                case "DelayAsync":
                    return Task.CompletedTask;
                case "DetectTextAsync":
                    return Task.FromResult<ScreenTextRecognitionResult?>(null);
                default:
                    throw new InvalidOperationException($"Unexpected call: {targetMethod?.Name}");
            }
        }
    }

    private sealed class RecordingActions : ICareerFlowActionRunner
    {
        public List<string> ActionIds { get; } = [];
        public CareerTrainingResult? Result { get; init; }
        public Task<CareerTrainingResult?> RunAsync(CareerFlowContext context, string screenId, string actionId,
            HachimiPipelineRunOptions? options = null)
        {
            ActionIds.Add(actionId);
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingStrategy : ICareerTrainingStrategy<UraCareerSessionState>
    {
        public UraPlannedAction Action { get; set; } = UraPlannedAction.Race;
        public int ChooseCount { get; private set; }
        public UraActionIntent Choose(CareerSessionState<UraCareerSessionState> session,
            ICareerScenarioModule<UraCareerSessionState> scenario)
        {
            ChooseCount++;
            return new UraActionIntent(Action, Action == UraPlannedAction.Training ? "speed" : null,
                "test strategy", false, []);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
}
