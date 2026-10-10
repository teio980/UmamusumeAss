using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerRaceStrategyFlowTests
{
    [Fact]
    public async Task Real_arima_runner_with_late_selected_skips_settings()
    {
        var test = await CreateAsync("late");
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var frame = GrayImageCodec.FromFile(Path.Combine(root, "testdata", "hachimi", "ura",
            "captures", "senior_arima_race_stage.png"))!;
        test.Runtime.Frames.Enqueue(frame);

        Assert.Null(await test.Handler.HandleAsync(test.Context));

        Assert.True(test.Context.State.RaceStrategyConfigured);
        Assert.Equal(1, test.Runtime.Captures);
        Assert.Empty(test.Runtime.Taps);
        Assert.Empty(test.Runtime.Delays);
        Assert.Equal(["entry.view_results"], test.Actions.Calls);
    }

    [Theory]
    [InlineData("front")]
    [InlineData("pace")]
    [InlineData("late")]
    [InlineData("end")]
    public async Task Already_selected_strategy_uses_one_frame_and_no_settings_taps(string strategy)
    {
        var test = await CreateAsync(strategy);
        test.Runtime.Frames.Enqueue(Runner(test.Pack, strategy));

        Assert.Null(await test.Handler.HandleAsync(test.Context));

        Assert.True(test.Context.State.RaceStrategyConfigured);
        Assert.Equal(1, test.Runtime.Captures);
        Assert.Empty(test.Runtime.Taps);
        Assert.Empty(test.Runtime.Delays);
        Assert.Equal(["entry.view_results"], test.Actions.Calls);
    }

    [Theory]
    [InlineData("front", "pace")]
    [InlineData("pace", "late")]
    [InlineData("late", "end")]
    [InlineData("end", "front")]
    public async Task Different_strategy_changes_once_and_verifies_saved_runner_without_fixed_delays(
        string current, string target)
    {
        var test = await CreateAsync(target);
        test.Runtime.Frames.Enqueue(Runner(test.Pack, current));
        test.Runtime.Frames.Enqueue(Dialog(test.Pack, current));
        test.Runtime.Frames.Enqueue(Dialog(test.Pack, target));
        test.Runtime.Frames.Enqueue(Runner(test.Pack, target));

        Assert.Null(await test.Handler.HandleAsync(test.Context));

        Assert.True(test.Context.State.RaceStrategyConfigured);
        Assert.Equal(4, test.Runtime.Captures);
        Assert.Equal([$"race_runner_strategy_change_{target}", $"race_runner_strategy_option_{target}",
            "race_runner_strategy_save"], test.Runtime.Taps);
        Assert.Empty(test.Runtime.Delays);
        Assert.Equal(["entry.view_results"], test.Actions.Calls);
    }

    [Fact]
    public async Task Missing_runner_marker_opens_dialog_after_bounded_retry_and_skips_already_selected_option()
    {
        var test = await CreateAsync("pace");
        for (var index = 0; index < 3; index++)
            test.Runtime.Frames.Enqueue(Runner(test.Pack));
        test.Runtime.Frames.Enqueue(Dialog(test.Pack, "pace"));
        test.Runtime.Frames.Enqueue(Runner(test.Pack, "pace"));

        Assert.Null(await test.Handler.HandleAsync(test.Context));

        Assert.True(test.Context.State.RaceStrategyConfigured);
        Assert.Equal(5, test.Runtime.Captures);
        Assert.Equal([120, 120], test.Runtime.Delays);
        Assert.Equal(["race_runner_strategy_change_pace", "race_runner_strategy_save"], test.Runtime.Taps);
    }

    [Fact]
    public async Task Ambiguous_runner_markers_do_not_treat_target_as_already_configured()
    {
        var test = await CreateAsync("pace");
        for (var index = 0; index < 3; index++)
            test.Runtime.Frames.Enqueue(Runner(test.Pack, "front", "pace"));
        test.Runtime.Frames.Enqueue(Dialog(test.Pack, "front"));
        test.Runtime.Frames.Enqueue(Dialog(test.Pack, "pace"));
        test.Runtime.Frames.Enqueue(Runner(test.Pack, "pace"));

        Assert.Null(await test.Handler.HandleAsync(test.Context));

        Assert.Equal(6, test.Runtime.Captures);
        Assert.Equal(3, test.Runtime.Taps.Count);
        Assert.Equal([120, 120], test.Runtime.Delays);
    }

    [Fact]
    public async Task Existing_strategy_dialog_is_verified_without_another_change_tap()
    {
        var test = await CreateAsync("pace");
        test.Runtime.Frames.Enqueue(Dialog(test.Pack, "pace"));
        test.Runtime.Frames.Enqueue(Runner(test.Pack, "pace"));

        Assert.Null(await test.Handler.HandleAsync(test.Context));

        Assert.Equal(["race_runner_strategy_save"], test.Runtime.Taps);
        Assert.Equal(2, test.Runtime.Captures);
        Assert.Empty(test.Runtime.Delays);
    }

    [Fact]
    public async Task Animation_is_polled_without_repeating_change_option_or_save()
    {
        var test = await CreateAsync("pace");
        test.Runtime.Frames.Enqueue(Runner(test.Pack, "front"));
        test.Runtime.Frames.Enqueue(Runner(test.Pack, "front"));
        test.Runtime.Frames.Enqueue(Dialog(test.Pack, "front"));
        test.Runtime.Frames.Enqueue(Dialog(test.Pack, "front"));
        test.Runtime.Frames.Enqueue(Dialog(test.Pack, "pace"));
        test.Runtime.Frames.Enqueue(Dialog(test.Pack, "pace"));
        test.Runtime.Frames.Enqueue(Runner(test.Pack, "pace"));

        Assert.Null(await test.Handler.HandleAsync(test.Context));

        Assert.Equal(7, test.Runtime.Captures);
        Assert.Equal([120, 120, 120], test.Runtime.Delays);
        Assert.Equal(["race_runner_strategy_change_pace", "race_runner_strategy_option_pace",
            "race_runner_strategy_save"], test.Runtime.Taps);
    }

    [Fact]
    public async Task Missing_capture_is_inconclusive_and_does_not_trigger_settings()
    {
        var test = await CreateAsync("pace");
        test.Runtime.Frames.Enqueue(null);
        test.Runtime.Frames.Enqueue(Runner(test.Pack, "pace"));

        Assert.Null(await test.Handler.HandleAsync(test.Context));

        Assert.Equal(2, test.Runtime.Captures);
        Assert.Empty(test.Runtime.Taps);
        Assert.Equal([120], test.Runtime.Delays);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_ambiguous_selection_never_sends_save(bool ambiguous)
    {
        var test = await CreateAsync("pace");
        test.Pack.ExecutionDefinition.GetTask("race_runner_strategy_option_pace_post").TimeoutMilliseconds = 1;
        test.Runtime.Frames.Enqueue(Runner(test.Pack, "front"));
        test.Runtime.Frames.Enqueue(Dialog(test.Pack, "front"));
        test.Runtime.Frames.Enqueue(ambiguous ? Dialog(test.Pack, "front", "pace") : Dialog(test.Pack));

        var result = await test.Handler.HandleAsync(test.Context);

        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.False(test.Context.State.RaceStrategyConfigured);
        Assert.Equal(["race_runner_strategy_change_pace", "race_runner_strategy_option_pace"], test.Runtime.Taps);
        Assert.Empty(test.Actions.Calls);
    }

    [Fact]
    public async Task Save_without_the_requested_runner_marker_does_not_start_race_or_repeat_confirmation()
    {
        var test = await CreateAsync("pace");
        test.Pack.ExecutionDefinition.GetTask("race_runner_strategy_save").TimeoutMilliseconds = 1;
        test.Runtime.Frames.Enqueue(Dialog(test.Pack, "pace"));
        test.Runtime.Frames.Enqueue(Runner(test.Pack, "front"));

        var result = await test.Handler.HandleAsync(test.Context);

        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.False(test.Context.State.RaceStrategyConfigured);
        Assert.Equal(["race_runner_strategy_save"], test.Runtime.Taps);
        Assert.Empty(test.Actions.Calls);
    }

    [Fact]
    public async Task Later_runner_visit_rechecks_fresh_strategy_instead_of_trusting_previous_configuration()
    {
        var test = await CreateAsync("pace");
        test.Runtime.Frames.Enqueue(Runner(test.Pack, "pace"));
        Assert.Null(await test.Handler.HandleAsync(test.Context));
        test.Context.State.RaceStrategyConfigured = false;
        test.Runtime.Frames.Enqueue(Runner(test.Pack, "front"));
        test.Runtime.Frames.Enqueue(Dialog(test.Pack, "front"));
        test.Runtime.Frames.Enqueue(Dialog(test.Pack, "pace"));
        test.Runtime.Frames.Enqueue(Runner(test.Pack, "pace"));

        Assert.Null(await test.Handler.HandleAsync(test.Context));

        Assert.Equal(5, test.Runtime.Captures);
        Assert.Equal(3, test.Runtime.Taps.Count);
        Assert.Equal(["entry.view_results", "entry.view_results"], test.Actions.Calls);
    }

    [Fact]
    public async Task Unrelated_page_is_not_clicked()
    {
        var test = await CreateAsync("pace");
        test.Runtime.Frames.Enqueue(EmptyFrame());

        var result = await test.Handler.HandleAsync(test.Context);

        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.False(test.Context.State.RaceStrategyConfigured);
        Assert.Empty(test.Runtime.Taps);
        Assert.Empty(test.Actions.Calls);
    }

    [Fact]
    public async Task Strategy_marker_without_runner_controls_does_not_mark_strategy_configured()
    {
        var test = await CreateAsync("pace");
        var frame = EmptyFrame();
        PasteTask(frame, test.Pack, "race_runner_strategy_apply_pace");
        test.Runtime.Frames.Enqueue(frame);

        var result = await test.Handler.HandleAsync(test.Context);

        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.False(test.Context.State.RaceStrategyConfigured);
        Assert.Empty(test.Runtime.Taps);
        Assert.Empty(test.Actions.Calls);
    }

    [Fact]
    public async Task Cancellation_before_observation_sends_no_input()
    {
        var test = await CreateAsync("pace");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Handler.HandleAsync(
            test.Context with { CancellationToken = cancellation.Token }));

        Assert.Empty(test.Runtime.Taps);
    }

    [Theory]
    [InlineData("next")]
    [InlineData("retry")]
    [InlineData("outcome")]
    [InlineData("count")]
    [InlineData("delay")]
    [InlineData("optional")]
    public async Task Customized_graph_uses_existing_action_runner(string customization)
    {
        var test = await CreateAsync("pace");
        var task = test.Pack.ExecutionDefinition.GetTask("race_runner_strategy_save");
        switch (customization)
        {
            case "next": task.Next.Add("custom_task"); break;
            case "retry": task.RetryTimes = 1; break;
            case "outcome": task.Outcome = "custom.outcome"; break;
            case "count": task.CountAs = "custom.count"; break;
            case "delay": task.PostDelay = 2_000; break;
            case "optional": task.Required = false; break;
            default: throw new ArgumentOutOfRangeException(nameof(customization));
        }

        Assert.Null(await test.Handler.HandleAsync(test.Context));

        Assert.Equal(["strategy.apply.pace", "entry.view_results"], test.Actions.Calls);
        Assert.Equal(0, test.Runtime.Captures);
    }

    private static async Task<TestContext> CreateAsync(string strategy)
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, StrategyRuntime>();
        var runtime = (StrategyRuntime)(object)visual;
        var actions = new RecordingActions();
        var context = new CareerFlowContext(new LastVerifiedConnection("adb", "strategy-test", "android",
                "version", 900, 1600, 900, 1600, DateTimeOffset.UnixEpoch),
            pack, true, null!, null!, strategy, new UraCareerSessionState(),
            new CareerObservation("race_runner", 1), null, CancellationToken.None);
        return new(pack, runtime, actions, context, new CareerRaceRunnerCheckpointHandler(actions, visual));
    }

    private static GrayImage EmptyFrame() => new(900, 1600,
        Enumerable.Repeat((byte)255, 900 * 1600).ToArray(),
        Enumerable.Repeat((byte)255, 900 * 1600 * 4).ToArray());

    private static GrayImage Runner(UraScenarioPack pack, params string[] selected)
    {
        var frame = EmptyFrame();
        PasteTask(frame, pack, "race_runner_strategy_change_pace");
        foreach (var type in selected)
            PasteTask(frame, pack, $"race_runner_strategy_apply_{type}");
        return frame;
    }

    private static GrayImage Dialog(UraScenarioPack pack, params string[] selected)
    {
        var frame = EmptyFrame();
        foreach (var type in new[] { "front", "pace", "late", "end" })
            PasteTask(frame, pack, $"race_runner_strategy_option_{type}");
        foreach (var type in selected)
            PasteTask(frame, pack, $"race_runner_strategy_option_{type}_post");
        PasteTask(frame, pack, "race_runner_strategy_save");
        return frame;
    }

    private static void PasteTask(GrayImage frame, UraScenarioPack pack, string name)
    {
        var task = pack.ExecutionDefinition.GetTask(name);
        var template = GrayImageCodec.FromFile(pack.VisualResources!.ResolveTaskTemplate(name))!;
        Paste(frame, template, task.Roi![0] + 2, task.Roi[1] + 2);
    }

    private static void Paste(GrayImage frame, GrayImage template, int x, int y)
    {
        for (var row = 0; row < template.Height; row++)
        {
            Array.Copy(template.Pixels, row * template.Width, frame.Pixels,
                (y + row) * frame.Width + x, template.Width);
            Array.Copy(template.RgbaPixels!, row * template.Width * 4, frame.RgbaPixels!,
                ((y + row) * frame.Width + x) * 4, template.Width * 4);
        }
    }

    private sealed record TestContext(UraScenarioPack Pack, StrategyRuntime Runtime, RecordingActions Actions,
        CareerFlowContext Context, CareerRaceRunnerCheckpointHandler Handler);

    private sealed class RecordingActions : ICareerFlowActionRunner
    {
        public List<string> Calls { get; } = [];
        public Task<CareerTrainingResult?> RunAsync(CareerFlowContext context, string screenId, string actionId,
            HachimiPipelineRunOptions? options = null)
        {
            Calls.Add(actionId);
            return Task.FromResult<CareerTrainingResult?>(null);
        }
    }

    public class StrategyRuntime : DispatchProxy
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
                    if (Frames.Count > 0)
                        _lastFrame = Frames.Dequeue();
                    return Task.FromResult(_lastFrame);
                case "LoadTemplateAsync":
                    ((CancellationToken)args![2]!).ThrowIfCancellationRequested();
                    return Task.FromResult(GrayImageCodec.FromFile((string)args[0]!));
                case "TapMatchAsync":
                    Assert.Equal(5, args!.Length);
                    Assert.Same(_lastFrame, args[1]);
                    Taps.Add((string)args[3]!);
                    return Task.CompletedTask;
                case "DelayAsync":
                    Delays.Add((int)args![0]!);
                    return Task.CompletedTask;
                default:
                    throw new InvalidOperationException($"Unexpected recapture/rematch: {targetMethod?.Name}.");
            }
        }
    }
}
