using System.IO;
using System.Reflection;
using System.Text.Json;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerTrainingEntryTransitionTests
{
    [Fact]
    public async Task Confirmed_entry_selects_training_without_observing_then_resumes_event_recognition()
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, TrainingVisualRuntime>();
        var recording = (TrainingVisualRuntime)(object)visual;
        recording.Frame = GrayImageCodec.FromFile(Path.Combine(root,
            "testdata", "hachimi", "ura", "captures", "turn3_training_selection.png"));
        Assert.NotNull(recording.Frame);
        var dispatcher = Dispatcher(visual);
        var state = TrainingState();
        state.GoalCompletionProbePending = true;
        var handled = new List<string>();
        var fullObservationCount = 0;
        var bindings = new CareerRuntimeLoopBindings(
            IsStartTransitionExpected: () => false,
            BeforeObservationAsync: _ => Task.FromResult(new CareerRuntimeStep()),
            ObserveAsync: (_, _, _) =>
            {
                var confirmed = CareerTrainingEngine.TakeConfirmedTrainingSelection(pack, state);
                if (confirmed is not null)
                    return Task.FromResult<CareerObservation?>(confirmed);

                fullObservationCount++;
                Assert.Equal("speed", state.TrainingClickIssuedType);
                return Task.FromResult<CareerObservation?>(new("training_event", .99));
            },
            HandleObservationAsync: async (observation, token) =>
            {
                state.LastScreenId = observation.ScreenId;
                handled.Add(observation.ScreenId);
                var context = Context(pack, state, observation, token);
                if (observation.ScreenId == "career_main")
                {
                    Assert.Null(await dispatcher.RunAsync(context, "career_main", "action.training"));
                    Assert.True(state.TrainingSelectionEntryConfirmed);
                }
                else if (observation.ScreenId == "training_selection")
                {
                    Assert.True(observation.ConfirmedByAction);
                    Assert.Equal(CareerScreenKind.Turn, observation.Kind);
                    Assert.Null(await new CareerTurnFlow(dispatcher).HandleAsync(context));
                    Assert.True(state.GoalCompletionProbeArmed);
                    Assert.False(state.TrainingSelectionEntryConfirmed);
                }
                else
                {
                    return new CareerRuntimeStep(new CareerTrainingResult(
                        true, "Reached the event after training.", 0, observation.ScreenId));
                }

                return new CareerRuntimeStep(ActionsCompleted: 1);
            },
            SaveRecognitionFailureAsync: _ => throw new InvalidOperationException("Unexpected recognition failure."),
            DelayAsync: (_, _) => Task.CompletedTask);

        var result = await CareerRuntimeLoop.RunAsync(state.Runtime, bindings,
            actionsCompleted: 0, firstObservation: new("career_main", .99),
            resumeRecoveryPending: false, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["career_main", "training_selection", "training_event"], handled);
        Assert.Equal(1, fullObservationCount);
        Assert.Equal(2, result.ActionsCompleted);
        Assert.Equal(
            ["career_main_action_training", "career_main_action_training_transition",
             "training_selection_speed_raised_probe", "training_selection_speed_raised_click"],
            recording.MatchRequests);
        Assert.Equal(2, recording.Taps);
        Assert.Equal(1, recording.CaptureCount);
    }

    [Theory]
    [InlineData(true, "training.selection.confirmed", true)]
    [InlineData(true, null, false)]
    [InlineData(false, "training.selection.confirmed", false)]
    public async Task Entry_requires_the_confirmed_outcome_even_when_recovery_succeeds(
        bool headerFound, string? outcome, bool expectedConfirmed)
    {
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, TrainingVisualRuntime>();
        var recording = (TrainingVisualRuntime)(object)visual;
        recording.HeaderFound = headerFound;
        var pack = EntryPack(outcome);
        var state = TrainingState();
        state.TrainingSelectionEntryConfirmed = true;

        Assert.Null(await Dispatcher(visual).RunAsync(
            Context(pack, state, new("career_main", .99)), "career_main", "action.training"));

        Assert.Equal(expectedConfirmed, state.TrainingSelectionEntryConfirmed);
        Assert.Equal(expectedConfirmed,
            CareerTrainingEngine.TakeConfirmedTrainingSelection(pack, state) is not null);
        Assert.Null(CareerTrainingEngine.TakeConfirmedTrainingSelection(pack, state));
        Assert.False(state.TrainingSelectionEntryConfirmed);
    }

    [Fact]
    public async Task Failed_entry_clears_a_previous_confirmation()
    {
        var visual = DispatchProxy.Create<IVisualPipelineRuntime, TrainingVisualRuntime>();
        ((TrainingVisualRuntime)(object)visual).ButtonFound = false;
        var pack = EntryPack("training.selection.confirmed");
        var state = TrainingState();
        state.TrainingSelectionEntryConfirmed = true;

        var result = await Dispatcher(visual).RunAsync(
            Context(pack, state, new("career_main", .99)), "career_main", "action.training");

        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.False(state.TrainingSelectionEntryConfirmed);
        Assert.Null(CareerTrainingEngine.TakeConfirmedTrainingSelection(pack, state));
    }

    [Theory]
    [InlineData("career_main", "speed", null, false)]
    [InlineData("training_selection", "speed", null, true)]
    [InlineData("career_main", "speed", "speed", true)]
    [InlineData("career_main", null, null, true)]
    public void Resume_or_already_submitted_training_does_not_bypass_recognition(
        string lastScreen, string? pendingType, string? clickedType, bool confirmed)
    {
        var state = TrainingState();
        state.LastScreenId = lastScreen;
        state.PendingTrainingType = pendingType;
        state.TrainingClickIssuedType = clickedType;
        state.TrainingSelectionEntryConfirmed = confirmed;

        Assert.Null(CareerTrainingEngine.TakeConfirmedTrainingSelection(EntryPack(null), state));
        Assert.False(state.TrainingSelectionEntryConfirmed);
    }

    [Fact]
    public void Entry_confirmation_is_not_restored_from_a_checkpoint()
    {
        var state = TrainingState();
        state.TrainingSelectionEntryConfirmed = true;
        var json = JsonSerializer.Serialize(state);

        Assert.DoesNotContain("TrainingSelectionEntryConfirmed", json, StringComparison.Ordinal);
        var restored = JsonSerializer.Deserialize<UraCareerSessionState>(json)!;
        Assert.Equal("speed", restored.PendingTrainingType);
        Assert.False(restored.TrainingSelectionEntryConfirmed);
        Assert.Null(CareerTrainingEngine.TakeConfirmedTrainingSelection(EntryPack(null), restored));
    }

    private static UraCareerSessionState TrainingState() => new()
    {
        CareerStarted = true,
        LastScreenId = "career_main",
        LastAction = UraPlannedAction.Training,
        PendingTrainingType = "speed",
    };

    private static CareerFlowContext Context(UraScenarioPack pack, UraCareerSessionState state,
        CareerObservation observation, CancellationToken token = default) => new(
        Connection, pack, true, null!, null!, string.Empty, state, observation, null, token);

    private static CareerFlowDispatcher Dispatcher(IVisualPipelineRuntime visual) => new(
        visual, new HachimiJsonPipelineRunner(
            DispatchProxy.Create<IAdbRuntime, UnexpectedAdbRuntime>(), visual,
            new JsonSettingsService(Path.Combine(Path.GetTempPath(),
                $"training-entry-{Guid.NewGuid():N}.json"))));

    private static UraScenarioPack EntryPack(string? outcome) => new(
        "manifest.json", ".", ".", ".", new UraScenarioManifest(), new UraScenarioDefinition(),
        new UraObjectiveDocument(), new UraRaceDocument(), new UraEventDocument(),
        new UraScreenProfile
        {
            Screens =
            [
                new UraScreenDefinition
                {
                    ScreenId = "career_main", Flow = "main",
                    Actions = [new UraScreenAction { SemanticId = "action.training", Task = "open" }],
                },
                new UraScreenDefinition { ScreenId = "training_selection", Flow = "turn" },
            ],
        },
        new HachimiPipelineDefinition
        {
            Tasks = new()
            {
                ["open"] = new()
                {
                    Algorithm = "MatchTemplate", Action = "ClickSelf", Template = "button.png",
                    Required = true, TimeoutMilliseconds = 100, Next = ["confirm"],
                },
                ["confirm"] = new()
                {
                    Algorithm = "MatchTemplate", Action = "JustReturn", Template = "header.png",
                    Required = true, TimeoutMilliseconds = 100, Outcome = outcome,
                    OnErrorNext = ["recover"],
                },
                ["recover"] = new() { Algorithm = "JustReturn", Action = "JustReturn" },
            },
        });

    private static LastVerifiedConnection Connection => new(
        "adb", "serial", "android", "version", 900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);

    public class TrainingVisualRuntime : DispatchProxy
    {
        public GrayImage? Frame { get; set; }
        public bool ButtonFound { get; set; } = true;
        public bool HeaderFound { get; set; } = true;
        public List<string> MatchRequests { get; } = [];
        public int Taps { get; private set; }
        public int CaptureCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name is "WaitForMatchAsync" or "WaitForColorMatchAsync")
            {
                var task = (string)args![8]!;
                MatchRequests.Add(task);
                var found = task == "confirm" ? HeaderFound : task != "open" || ButtonFound;
                return Task.FromResult<TemplateMatchResult?>(new(found, found ? .99 : 0, 50, 1200, 82, 52));
            }
            if (targetMethod?.Name == "CaptureGrayAsync")
            {
                CaptureCount++;
                return Task.FromResult(Frame);
            }
            if (targetMethod?.Name == "LoadTemplateAsync")
                return Task.FromResult(GrayImageCodec.FromFile((string)args![0]!));
            if (targetMethod?.Name == "TapMatchAsync")
            {
                Taps++;
                return Task.CompletedTask;
            }
            if (targetMethod?.Name == "DelayAsync")
                return Task.CompletedTask;
            throw new InvalidOperationException($"Unexpected visual operation: {targetMethod?.Name}.");
        }
    }

    public class UnexpectedAdbRuntime : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"Unexpected ADB operation: {targetMethod?.Name}.");
    }
}
