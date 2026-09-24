using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerRuntimeEventBoundaryTests
{
    [Theory]
    [InlineData("career_intro_event")]
    [InlineData("training_event")]
    [InlineData("event_choice")]
    [InlineData("scenario_event")]
    public async Task Turn_flow_does_not_automatically_act_on_runtime_event_screens(
        string screenId)
    {
        var actions = new RecordingCareerActions();
        var flow = new CareerTurnFlow(actions);
        var context = new CareerFlowContext(
            null!,
            null!,
            PauseOnUnknownOutcome: true,
            null!,
            null!,
            string.Empty,
            new UraCareerSessionState(),
            new CareerObservation(screenId, 1),
            null,
            CancellationToken.None);

        var result = await flow.HandleAsync(context);

        Assert.Null(result);
        Assert.Equal(0, actions.CallCount);
    }

    [Fact]
    public async Task Event_handler_runs_for_observed_event_screens()
    {
        var visualRuntime = UnexpectedCallProxy.Create<IVisualPipelineRuntime>();
        var runner = new HachimiJsonPipelineRunner(
            UnexpectedCallProxy.Create<IAdbRuntime>(),
            visualRuntime,
            new UnusedSettingsService());
        var eventHandler = new RecordingEventHandler();
        var dispatcher = new CareerFlowDispatcher(visualRuntime, runner, eventHandler);
        var connection = new LastVerifiedConnection(
            "adb", "serial", "android", "version", 900, 1600, 900, 1600,
            DateTimeOffset.UnixEpoch);
        var successPack = CreatePack("DoNothing");
        var context = new CareerFlowContext(
            connection,
            successPack,
            PauseOnUnknownOutcome: true,
            null!,
            null!,
            string.Empty,
            new UraCareerSessionState(),
            new CareerObservation("training_selection", 1),
            null,
            CancellationToken.None);

        Assert.Null(await dispatcher.RunAsync(context, "training_selection", "advance"));
        Assert.Equal(0, eventHandler.CallCount);

        Assert.Null(await dispatcher.RunScreenActionAsync(
            connection,
            successPack,
            "training_selection",
            "advance",
            null,
            CancellationToken.None));
        Assert.Equal(0, eventHandler.CallCount);

        Assert.Null(await dispatcher.RunAsync(
            context with { Observation = new CareerObservation("training_result", 1) },
            "training_result",
            "advance"));
        Assert.Equal(0, eventHandler.CallCount);

        foreach (var eventScreen in new[]
        {
            "career_intro_event", "training_event", "event_choice", "scenario_event",
        })
        {
            Assert.Null(await dispatcher.DispatchAsync(
                connection,
                successPack,
                true,
                null!,
                null!,
                string.Empty,
                context.State,
                new CareerObservation(eventScreen, 1),
                null,
                CareerEventHandlingModes.Default,
                CancellationToken.None));
            Assert.Equal(eventScreen, eventHandler.Context?.Observation.ScreenId);
        }
        Assert.Equal(4, eventHandler.CallCount);

        Assert.Null(await dispatcher.TryHandleEventAsync(
            context with { Observation = new CareerObservation("rest_confirmation", 1) }));
        Assert.Equal("rest_confirmation", eventHandler.Context?.Observation.ScreenId);
        Assert.Equal(5, eventHandler.CallCount);

        var failedContext = context with { Pack = CreatePack("NotAnAction") };
        var failure = await dispatcher.RunAsync(
            failedContext,
            "training_selection",
            "advance");
        Assert.NotNull(failure);
        Assert.False(failure.Succeeded);
        Assert.Equal(5, eventHandler.CallCount);
    }

    [Fact]
    public async Task Intro_screen_remains_recognizable_during_startup_only()
    {
        var frame = new GrayImage(
            8,
            8,
            [0, 255, 0, 255, 0, 255, 0, 255,
             255, 0, 255, 0, 255, 0, 255, 0,
             0, 255, 0, 255, 0, 255, 0, 255,
             255, 0, 255, 0, 255, 0, 255, 0,
             0, 255, 0, 255, 0, 255, 0, 255,
             255, 0, 255, 0, 255, 0, 255, 0,
             0, 255, 0, 255, 0, 255, 0, 255,
             255, 0, 255, 0, 255, 0, 255, 0]);
        var observer = new CareerScreenObserver(ObserverRuntimeProxy.Create(frame));
        var pack = CreateRecognitionPack();
        var connection = new LastVerifiedConnection(
            "adb", "serial", "android", "version", 900, 1600, 900, 1600,
            DateTimeOffset.UnixEpoch);
        var state = new UraCareerSessionState();

        var startupObservation = await observer.ObserveAsync(
            connection,
            pack,
            state,
            careerStartTransitionExpected: true,
            CancellationToken.None);
        var runtimeObservation = await observer.ObserveAsync(
            connection,
            pack,
            state,
            careerStartTransitionExpected: false,
            CancellationToken.None);

        Assert.Equal("career_intro_event", startupObservation?.ScreenId);
        Assert.NotEqual("career_intro_event", runtimeObservation?.ScreenId);
    }

    [Fact]
    public async Task Handler_can_process_consecutive_pages_of_the_same_event_kind()
    {
        var frame = new GrayImage(
            8,
            8,
            [0, 255, 0, 255, 0, 255, 0, 255,
             255, 0, 255, 0, 255, 0, 255, 0,
             0, 255, 0, 255, 0, 255, 0, 255,
             255, 0, 255, 0, 255, 0, 255, 0,
             0, 255, 0, 255, 0, 255, 0, 255,
             255, 0, 255, 0, 255, 0, 255, 0,
             0, 255, 0, 255, 0, 255, 0, 255,
             255, 0, 255, 0, 255, 0, 255, 0]);
        var actions = new RecordingCareerActions();
        var handler = new CareerEventHandler(ObserverRuntimeProxy.Create(frame), actions);
        var context = new CareerFlowContext(
            new LastVerifiedConnection(
                "adb", "serial", "android", "version", 900, 1600, 900, 1600,
                DateTimeOffset.UnixEpoch),
            CreateRecognitionPack(),
            true,
            null!,
            null!,
            string.Empty,
            new UraCareerSessionState(),
            new CareerObservation("event_choice", 1),
            null,
            CancellationToken.None);

        Assert.Null(await handler.TryRecognizeAndHandleAsync(context));
        Assert.Null(await handler.TryRecognizeAndHandleAsync(context));
        Assert.Equal(2, actions.CallCount);
    }

    private static UraScenarioPack CreatePack(string action)
    {
        var execution = new HachimiPipelineDefinition
        {
            Tasks = new Dictionary<string, HachimiPipelineTask>(StringComparer.OrdinalIgnoreCase)
            {
                ["no_op"] = new() { Action = action, Success = true },
            },
        };
        var profile = new UraScreenProfile
        {
            Screens =
            [
                new UraScreenDefinition
                {
                    ScreenId = "training_selection",
                    Actions = [new UraScreenAction { SemanticId = "advance", Task = "no_op" }],
                },
                new UraScreenDefinition
                {
                    ScreenId = "training_result",
                    Actions = [new UraScreenAction { SemanticId = "advance", Task = "no_op" }],
                },
            ],
        };

        return new UraScenarioPack(
            string.Empty,
            Path.GetTempPath(),
            string.Empty,
            string.Empty,
            null!,
            null!,
            null!,
            null!,
            null!,
            profile,
            execution);
    }

    private static UraScenarioPack CreateRecognitionPack()
    {
        var profile = new UraScreenProfile
        {
            Screens =
            [
                new UraScreenDefinition
                {
                    ScreenId = "career_intro_event",
                    Recognition = new UraScreenRecognition { Template = "intro.png" },
                },
                new UraScreenDefinition
                {
                    ScreenId = "training_event",
                    Recognition = new UraScreenRecognition { Template = "training-event.png" },
                },
                new UraScreenDefinition
                {
                    ScreenId = "event_choice",
                    Recognition = new UraScreenRecognition { Template = "choice.png" },
                },
                new UraScreenDefinition
                {
                    ScreenId = "scenario_event",
                    Recognition = new UraScreenRecognition { Template = "scenario.png" },
                },
            ],
        };
        return new UraScenarioPack(
            string.Empty,
            Path.GetTempPath(),
            string.Empty,
            string.Empty,
            null!,
            null!,
            null!,
            null!,
            null!,
            profile,
            new HachimiPipelineDefinition());
    }

    private sealed class RecordingCareerActions : ICareerFlowActionRunner
    {
        public int CallCount { get; private set; }

        public Task<CareerTrainingResult?> RunAsync(
            CareerFlowContext context,
            string screenId,
            string actionId,
            HachimiPipelineRunOptions? options = null)
        {
            CallCount++;
            return Task.FromResult<CareerTrainingResult?>(null);
        }
    }

    private sealed class RecordingEventHandler : ICareerEventHandler
    {
        public int CallCount { get; private set; }
        public CareerFlowContext? Context { get; private set; }

        public Task<CareerTrainingResult?> TryRecognizeAndHandleAsync(
            CareerFlowContext context)
        {
            CallCount++;
            Context = context;
            return Task.FromResult<CareerTrainingResult?>(null);
        }
    }

    private sealed class UnusedSettingsService : ISettingsService
    {
        public ConnectionSettings Load() => throw new NotSupportedException();

        public void Save(ConnectionSettings settings) => throw new NotSupportedException();
    }

    public class UnexpectedCallProxy : DispatchProxy
    {
        public UnexpectedCallProxy()
        {
        }

        public static T Create<T>() where T : class =>
            DispatchProxy.Create<T, UnexpectedCallProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException(
                $"Unexpected dependency call: {targetMethod?.Name ?? "unknown"}.");
    }

    public class ObserverRuntimeProxy : DispatchProxy
    {
        private GrayImage _frame = null!;

        public ObserverRuntimeProxy()
        {
        }

        public static IVisualPipelineRuntime Create(GrayImage frame)
        {
            var runtime = DispatchProxy.Create<IVisualPipelineRuntime, ObserverRuntimeProxy>();
            ((ObserverRuntimeProxy)(object)runtime)._frame = frame;
            return runtime;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "CaptureGrayAsync" or "LoadTemplateAsync" => Task.FromResult<GrayImage?>(_frame),
                "DelayAsync" => Task.CompletedTask,
                _ => throw new InvalidOperationException(
                    $"Unexpected visual runtime call: {targetMethod?.Name ?? "unknown"}."),
            };
    }
}
