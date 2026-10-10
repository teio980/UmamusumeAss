using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerTurnActionTransitionTests
{
    [Theory]
    [InlineData("career_main")]
    [InlineData("race_day")]
    public async Task Confirmation_disappearance_frame_saves_one_capture_and_keeps_stability(string destination)
    {
        var fixture = new Fixture();
        var frame = fixture.Frame(destination);
        fixture.Transition.ConfirmationDisappeared(frame);
        fixture.Runtime.Frames = [frame];

        Assert.Equal(destination, (await fixture.ObserveAsync())?.ScreenId);
        Assert.Null(fixture.State.Runtime.TurnActionTransition);
        Assert.Equal(1, fixture.Runtime.CaptureCount);
        Assert.Equal(1, fixture.Transition.ReusedFrames);
        Assert.Equal(0, fixture.Transition.FullRecognitionCount);
        Assert.DoesNotContain("race_list", fixture.Observer.LastCandidateScreenIds);
        Assert.DoesNotContain("training_selection", fixture.Observer.LastCandidateScreenIds);
    }

    [Fact]
    public async Task Changing_or_missing_followup_frame_cannot_start_another_turn()
    {
        foreach (var next in new[] { "event_choice", "missing" })
        {
            var fixture = new Fixture();
            fixture.Transition.ConfirmationDisappeared(fixture.Frame("career_main"));
            fixture.Runtime.Frames = [next == "missing" ? null : fixture.Frame(next)];

            Assert.Null(await fixture.ObserveAsync());
            Assert.Same(fixture.Transition, fixture.State.Runtime.TurnActionTransition);
        }
    }

    [Fact]
    public async Task Event_overlay_wins_over_main_and_retains_the_return_observer()
    {
        var fixture = new Fixture();
        var frame = fixture.Frame("career_main", "event_choice");
        fixture.Transition.ConfirmationDisappeared(frame);
        fixture.Runtime.Frames = [frame];

        var result = await fixture.ObserveAsync();
        Assert.Equal("event_choice", result?.ScreenId);
        Assert.NotNull(result);
        Assert.True(result.StableEventCapturedAt.HasValue);
        Assert.Same(fixture.Transition, fixture.State.Runtime.TurnActionTransition);
    }

    [Fact]
    public async Task Broad_recovery_recognizes_an_unexpected_overlay_after_repeated_misses()
    {
        var fixture = new Fixture();
        fixture.Transition.ConfirmationDisappeared(fixture.Frame());
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        fixture.Runtime.Frames = [fixture.Frame(),
            fixture.Frame("custom_overlay"), fixture.Frame("custom_overlay")];

        Assert.Equal("custom_overlay", (await fixture.ObserveAsync())?.ScreenId);
        Assert.Equal(1, fixture.Transition.FullRecognitionCount);
        Assert.Equal(3, fixture.Runtime.CaptureCount);
    }

    [Fact]
    public async Task Successful_event_chain_extends_waiting_but_submitted_confirmation_does_not()
    {
        var fixture = new Fixture();
        fixture.Transition.ConfirmationDisappeared(fixture.Frame());
        fixture.Clock.Advance(TimeSpan.FromSeconds(40));
        fixture.Transition.ActionCompleted("event_choice");
        fixture.Clock.Advance(TimeSpan.FromSeconds(40));
        Assert.False(fixture.Transition.Expired);
        fixture.Transition.ActionCompleted("rest_confirmation");
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Transition.Expired);
        Assert.Null(await fixture.ObserveAsync());
        Assert.Equal(0, fixture.Runtime.CaptureCount);
    }

    [Fact]
    public async Task Cancellation_and_daily_reset_propagate_from_return_observation()
    {
        var fixture = new Fixture();
        fixture.Transition.ConfirmationDisappeared(fixture.Frame());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Transition.ObserveAsync(fixture.Observer, Connection, fixture.Pack, fixture.State,
                null, cancellation.Token));
        Assert.Equal(0, fixture.Runtime.CaptureCount);
        fixture.Runtime.FirstCaptureException = new DateChangedInterruptionException();
        await Assert.ThrowsAsync<DateChangedInterruptionException>(() => fixture.ObserveAsync());
    }

    [Fact]
    public async Task Faster_return_polling_does_not_exhaust_the_general_recognition_retry_limit()
    {
        var runtime = new CareerRuntimeState { TurnActionTransition = new("rest_confirmation", "Rest") };
        var observations = 0;
        var bindings = new CareerRuntimeLoopBindings(() => false,
            _ => Task.FromResult(new CareerRuntimeStep()),
            (_, _, _) => Task.FromResult<CareerObservation?>(++observations == 4
                ? new CareerObservation("career_main", 1) : null),
            (_, _) => Task.FromResult(new CareerRuntimeStep(new CareerTrainingResult(true, "done", 0, "career_main"))),
            _ => throw new InvalidOperationException("Should not exhaust recognition retries."),
            (_, _) => Task.CompletedTask);

        var result = await CareerRuntimeLoop.RunAsync(runtime, bindings, 0, null, false,
            CancellationToken.None, recognitionRetryLimit: 0);
        Assert.True(result.Succeeded);
        Assert.Equal(4, observations);
    }

    private static LastVerifiedConnection Connection => new("adb", "break-test", "android", "version",
        32, 32, 32, 32, DateTimeOffset.UnixEpoch);

    private sealed class Fixture
    {
        private readonly Dictionary<string, (int X, int Y)> _positions = new()
        {
            ["event_choice"] = (0, 0), ["career_main"] = (0, 8), ["main_training"] = (8, 8),
            ["race_day"] = (0, 16), ["custom_overlay"] = (0, 24),
            ["race_list"] = (16, 0), ["training_selection"] = (16, 8),
        };
        public ManualClock Clock { get; } = new();
        public UraCareerSessionState State { get; } = new() { CareerStarted = true, LastScreenId = "rest_confirmation" };
        public CareerTurnActionTransition Transition { get; }
        public CareerRaceListEntryTests.FramesRuntime Runtime { get; }
        public CareerScreenObserver Observer { get; }
        public UraScenarioPack Pack { get; }

        public Fixture()
        {
            var visual = DispatchProxy.Create<IVisualPipelineRuntime, CareerRaceListEntryTests.FramesRuntime>();
            Runtime = (CareerRaceListEntryTests.FramesRuntime)(object)visual;
            var screens = new List<UraScreenDefinition>();
            var random = new Random(65);
            foreach (var (id, position) in _positions)
            {
                var pixels = new byte[64];
                random.NextBytes(pixels);
                Runtime.Templates[id + ".png"] = new GrayImage(8, 8, pixels);
                if (id == "main_training") continue;
                screens.Add(new UraScreenDefinition
                {
                    ScreenId = id,
                    Flow = id switch
                    {
                        "event_choice" or "custom_overlay" => "event", "career_main" => "main",
                        "training_selection" => "turn", _ => "race",
                    },
                    Recognition = new UraScreenRecognition
                    {
                        Template = id + ".png", Roi = [position.X, position.Y, 8, 8],
                        Stable = true, TemplateThreshold = 0.99,
                        RequiredTemplate = id == "career_main" ? "main_training.png" : null,
                        RequiredTemplateRoi = id == "career_main" ? [8, 8, 8, 8] : null,
                        RequiredTemplateThreshold = 0.99,
                    },
                });
            }
            Pack = new UraScenarioPack("", AppContext.BaseDirectory, "", "", null!, null!, null!, null!,
                new UraEventDocument(), new UraScreenProfile
                {
                    ReferenceWidth = 32, ReferenceHeight = 32, Screens = screens,
                }, new HachimiPipelineDefinition());
            Observer = new CareerScreenObserver(visual);
            Transition = new("rest_confirmation", "Rest", Clock);
            State.Runtime.TurnActionTransition = Transition;
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

        public Task<CareerObservation?> ObserveAsync() => Transition.ObserveAsync(Observer, Connection, Pack,
            State, null, CancellationToken.None);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
}
