using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerDirectRestFlowTests
{
    [Theory]
    [InlineData("action.rest", "rest_before_low_energy.png", "rest_confirmation.png")]
    [InlineData("action.rest", "rest_before_low_energy.png", "rest_confirmation_live_1538.png")]
    [InlineData("action.summer_rest", "summer_rest_current.png", "summer_rest_dialog.png")]
    public async Task Rest_clicks_button_then_ok_using_two_frames_without_full_observation(
        string actionId, string mainName, string dialogName)
    {
        var (_, visual, runtime, context) = await CreateAsync(mainName, dialogName);
        Assert.Null(await Dispatcher(visual).RunAsync(context, "career_main", actionId));

        string[] expectedTasks = [actionId == "action.rest"
            ? "career_main_action_rest" : "career_main_action_summer_rest", "rest_confirmation_rest_confirm"];
        Assert.Equal(expectedTasks, runtime.Taps);
        Assert.Equal(2, runtime.Captures);
        Assert.Empty(runtime.Delays);
        Assert.True(context.State.AwaitingRestConfirmationGone);
        Assert.True(context.State.GoalCompletionProbeArmed);
        Assert.False(context.State.GoalCompletionProbePending);
        Assert.Equal(actionId == "action.rest" ? "rest_confirmation" : "summer_rest_confirmation",
            context.State.LastScreenId);

        // A still-visible dialog after submission must never receive a second tap.
        Assert.Null(await Dispatcher(visual).RunAsync(context, context.State.LastScreenId, "rest.confirm"));
        Assert.Equal(2, runtime.Captures);
        Assert.Equal(2, runtime.Taps.Count);
    }

    [Fact]
    public async Task Dialog_animation_is_polled_and_does_not_repeat_the_rest_tap()
    {
        var (_, visual, runtime, context) = await CreateAsync(
            "rest_before_low_energy.png", "rest_before_low_energy.png", "rest_confirmation.png");

        Assert.Null(await new CareerRestFlow(visual).RunAsync(context, "career_main", "action.rest"));

        Assert.Equal(3, runtime.Captures);
        Assert.Equal([100], runtime.Delays);
        Assert.Equal(["career_main_action_rest", "rest_confirmation_rest_confirm"], runtime.Taps);
    }

    [Theory]
    [InlineData("recreation_confirmation.png")]
    [InlineData("infirmary_confirm.png")]
    [InlineData("rest_result.png")]
    public async Task Unrelated_dialog_or_result_does_not_receive_a_rest_ok_tap(string frameName)
    {
        var (pack, visual, runtime, context) = await CreateAsync("rest_before_low_energy.png", frameName);
        pack.ExecutionDefinition.GetTask("rest_confirmation_rest_confirm").TimeoutMilliseconds = 1;

        var result = await new CareerRestFlow(visual).RunAsync(context, "career_main", "action.rest");

        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Equal(["career_main_action_rest"], runtime.Taps);
        Assert.False(context.State.AwaitingRestConfirmationGone);
    }

    [Fact]
    public async Task Resumed_rest_dialog_sends_only_ok_and_uses_its_verified_frame()
    {
        var (_, visual, runtime, context) = await CreateAsync("rest_confirmation.png");

        Assert.Null(await Dispatcher(visual).RunAsync(context, "rest_confirmation", "rest.confirm"));

        Assert.Equal(["rest_confirmation_rest_confirm"], runtime.Taps);
        Assert.Equal(1, runtime.Captures);
        Assert.Empty(runtime.Delays);
    }

    [Fact]
    public async Task Missing_screenshot_is_inconclusive_and_never_clicks_ok()
    {
        var (pack, visual, runtime, context) = await CreateAsync("rest_before_low_energy.png");
        runtime.Frames.Enqueue(null);
        pack.ExecutionDefinition.GetTask("rest_confirmation_rest_confirm").TimeoutMilliseconds = 1;

        var result = await new CareerRestFlow(visual).RunAsync(context, "career_main", "action.rest");

        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Single(runtime.Taps);
    }

    private static async Task<(UraScenarioPack Pack, IVisualPipelineRuntime Visual,
        RestRuntime Runtime, CareerFlowContext Context)> CreateAsync(params string[] frames)
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, RestRuntime>();
        var runtime = (RestRuntime)(object)visual;
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        foreach (var name in frames)
            runtime.Frames.Enqueue(GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(root, name))
                ?? throw new InvalidDataException(name));
        var context = new CareerFlowContext(new LastVerifiedConnection("adb", "rest-test", "android", "version",
                900, 1600, 900, 1600, DateTimeOffset.UnixEpoch),
            pack, true, null!, null!, string.Empty,
            new UraCareerSessionState { GoalCompletionProbePending = true },
            new CareerObservation("career_main", 1), null, CancellationToken.None);
        return (pack, visual, runtime, context);
    }

    private static CareerFlowDispatcher Dispatcher(IVisualPipelineRuntime visual) => new(visual,
        new HachimiJsonPipelineRunner(
            DispatchProxy.Create<IAdbRuntime, CareerTrainingEntryTransitionTests.UnexpectedAdbRuntime>(),
            visual, new JsonSettingsService(Path.Combine(Path.GetTempPath(), $"rest-{Guid.NewGuid():N}.json"))));

    public class RestRuntime : DispatchProxy
    {
        public Queue<GrayImage?> Frames { get; } = new();
        public List<string> Taps { get; } = [];
        public List<int> Delays { get; } = [];
        public int Captures { get; private set; }
        private GrayImage? _lastFrame;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "CaptureGrayAsync":
                    Captures++;
                    _lastFrame = Frames.Count > 0 ? Frames.Dequeue() : _lastFrame;
                    return Task.FromResult(_lastFrame);
                case "LoadTemplateAsync":
                    return Task.FromResult(GrayImageCodec.FromFile((string)args![0]!));
                case "TapMatchAsync":
                    Assert.Equal(5, args!.Length);
                    Assert.Same(_lastFrame, args[1]);
                    Taps.Add((string)args[3]!);
                    return Task.CompletedTask;
                case "DelayAsync":
                    Delays.Add((int)args![0]!);
                    return Task.CompletedTask;
                default:
                    throw new InvalidOperationException($"Unexpected full observation/rematch: {targetMethod?.Name}.");
            }
        }
    }
}
