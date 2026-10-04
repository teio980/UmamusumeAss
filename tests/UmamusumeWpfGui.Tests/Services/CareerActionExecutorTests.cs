using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerActionExecutorTests
{
    [Fact]
    public async Task Semantic_outcome_survives_an_opaque_task_name()
    {
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, VisualStub>();
        var pack = Pack(new HachimiPipelineTask
        {
            Algorithm = "JustReturn", Action = "JustReturn", Outcome = "race.retry.declined",
        });
        var result = await Executor(visual).RunAsync(Connection, pack, "test", "test.action",
            null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("race.retry.declined", result.Outcome);
        Assert.Equal(HachimiFailureKind.None, result.FailureKind);
    }

    [Fact]
    public async Task Only_explicitly_optional_recognition_timeouts_are_skipped()
    {
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, VisualStub>();
        var executor = Executor(visual);
        var pack = Pack(new HachimiPipelineTask { Template = "unused.png", TimeoutMilliseconds = 1_000 });
        var required = await executor.RunAsync(Connection, pack, "test", "test.action", null,
            CancellationToken.None);
        var optional = await executor.RunAsync(Connection, pack, "test", "test.action", null,
            CancellationToken.None, allowVisualMiss: true);

        Assert.False(required.Succeeded);
        Assert.Equal(HachimiFailureKind.RecognitionTimeout, required.FailureKind);
        Assert.True(optional.Succeeded);
        Assert.Equal(CareerActionStatus.NotApplicable, optional.Status);
        Assert.Equal(0, ((VisualStub)(object)visual).Taps);
    }

    [Fact]
    public async Task Adapter_errors_are_not_optional_visual_misses()
    {
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, VisualStub>();
        ((VisualStub)(object)visual).ThrowOnMatch = true;
        var result = await Executor(visual).RunAsync(Connection,
            Pack(new HachimiPipelineTask { Template = "unused.png", TimeoutMilliseconds = 1_000 }),
            "test", "test.action", null, CancellationToken.None, allowVisualMiss: true);

        Assert.False(result.Succeeded);
        Assert.Equal(HachimiFailureKind.RuntimeError, result.FailureKind);
        Assert.Equal(CareerActionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task A_missing_dynamic_template_is_a_definition_failure()
    {
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, VisualStub>();
        var result = await Executor(visual).RunAsync(Connection,
            Pack(new HachimiPipelineTask { TemplateCollection = "test.collection" }),
            "test", "test.action", null, CancellationToken.None, allowVisualMiss: true);

        Assert.False(result.Succeeded);
        Assert.Equal(HachimiFailureKind.InvalidDefinition, result.FailureKind);
        Assert.Equal(0, ((VisualStub)(object)visual).Taps);
    }

    [Fact]
    public async Task An_action_suffix_does_not_resolve_a_different_semantic_id()
    {
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, VisualStub>();
        var result = await Executor(visual).RunAsync(Connection,
            Pack(new HachimiPipelineTask { Algorithm = "JustReturn", Action = "JustReturn" }),
            "test", "action", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(HachimiFailureKind.InvalidDefinition, result.FailureKind);
    }

    [Fact]
    public async Task Pending_confirmation_requires_a_captured_frame_and_never_reclicks()
    {
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, VisualStub>();
        var stub = (VisualStub)(object)visual;
        var template = new GrayImage(2, 2, [10, 70, 140, 220]);
        stub.Template = template;
        stub.Frame = null;
        var pack = Pack(new HachimiPipelineTask { Template = "unused.png", Roi = [0, 0, 2, 2], TemplateThreshold = 0.99 });
        pack.ExecutionDefinition.ReferenceWidth = 2;
        pack.ExecutionDefinition.ReferenceHeight = 2;
        pack.ScreenProfile.Screens = [new UraScreenDefinition
        {
            ScreenId = "rest_confirmation", Flow = "turn",
            Actions = [new UraScreenAction { SemanticId = "rest.confirm", Task = "opaque" }],
        }];
        var state = new UraCareerSessionState { AwaitingRestConfirmationGone = true };
        var completion = new CareerActionCompletionObserver(visual, 4);

        Assert.Equal(CareerActionStatus.AwaitingConfirmation,
            (await completion.ObserveAsync(Connection, pack, state, null, CancellationToken.None))!.Status);
        Assert.True(state.AwaitingRestConfirmationGone);
        stub.Frame = template;
        Assert.Equal(CareerActionStatus.AwaitingConfirmation,
            (await completion.ObserveAsync(Connection, pack, state, null, CancellationToken.None))!.Status);
        stub.Frame = new GrayImage(2, 2, [255, 255, 255, 255]);
        Assert.Equal(CareerActionStatus.AlreadySatisfied,
            (await completion.ObserveAsync(Connection, pack, state, null, CancellationToken.None))!.Status);
        Assert.False(state.AwaitingRestConfirmationGone);
        Assert.Equal(0, stub.Taps);
    }

    private static CareerJsonActionExecutor Executor(IVisualPipelineRuntime visual) => new(
        new HachimiJsonPipelineRunner(DispatchProxy.Create<IAdbRuntime, UnexpectedAdb>(), visual,
            new JsonSettingsService(Path.Combine(Path.GetTempPath(), $"career-action-{Guid.NewGuid():N}.json"))));

    private static UraScenarioPack Pack(HachimiPipelineTask task) => new(
        "manifest.json", ".", ".", ".", new UraScenarioManifest(), new UraScenarioDefinition(),
        new UraObjectiveDocument(), new UraRaceDocument(), new UraEventDocument(),
        new UraScreenProfile
        {
            Screens = [new UraScreenDefinition
            {
                ScreenId = "test", Flow = "turn",
                Actions = [new UraScreenAction { SemanticId = "test.action", Task = "opaque" }],
            }],
        }, new HachimiPipelineDefinition { Tasks = new() { ["opaque"] = task } });

    private static LastVerifiedConnection Connection => new("adb", "serial", "android", "version",
        900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);

    public class VisualStub : DispatchProxy
    {
        public bool ThrowOnMatch { get; set; }
        public int Taps { get; private set; }
        public GrayImage? Frame { get; set; }
        public GrayImage? Template { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "WaitForMatchAsync")
                return ThrowOnMatch ? throw new IOException("Capture unavailable")
                    : Task.FromResult<TemplateMatchResult?>(new(false, 0, 0, 0, 0, 0));
            if (targetMethod?.Name == "CaptureGrayAsync")
                return Task.FromResult(Frame);
            if (targetMethod?.Name == "LoadTemplateAsync")
                return Task.FromResult(Template);
            if (targetMethod?.Name is "TapAsync" or "TapMatchAsync")
            {
                Taps++;
                return Task.CompletedTask;
            }
            if (targetMethod?.Name == "DelayAsync")
                return Task.CompletedTask;
            throw new InvalidOperationException($"Unexpected visual call: {targetMethod?.Name}");
        }
    }

    public class UnexpectedAdb : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"Unexpected ADB call: {targetMethod?.Name}");
    }
}
