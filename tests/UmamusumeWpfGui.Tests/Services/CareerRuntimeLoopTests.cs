using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerRuntimeLoopTests
{
    [Fact]
    public async Task Pending_confirmation_delays_without_observing_or_handling_another_screen()
    {
        var beforeCount = 0;
        var observeCount = 0;
        var handleCount = 0;
        var delayCount = 0;
        var runtime = new CareerRuntimeState { LastScreenId = "career_main" };
        var bindings = Bindings(
            before: _ => Task.FromResult(++beforeCount == 1
                ? new CareerRuntimeStep(AwaitingTransition: true)
                : new CareerRuntimeStep(new CareerTrainingResult(
                    true, "confirmation completed", 0, runtime.LastScreenId))),
            observe: (_, _, _) =>
            {
                observeCount++;
                return Task.FromResult<CareerObservation?>(new("career_main", 1));
            },
            handle: (_, _) =>
            {
                handleCount++;
                return Task.FromResult(new CareerRuntimeStep());
            },
            delay: (_, _) =>
            {
                delayCount++;
                return Task.CompletedTask;
            });

        var result = await CareerRuntimeLoop.RunAsync(
            runtime, bindings, actionsCompleted: 4, firstObservation: null,
            resumeRecoveryPending: false, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(4, result.ActionsCompleted);
        Assert.Equal(2, beforeCount);
        Assert.Equal(0, observeCount);
        Assert.Equal(0, handleCount);
        Assert.Equal(1, delayCount);
    }

    [Fact]
    public async Task Null_observations_exhaust_retry_bound_and_save_diagnostics_without_advancing_runtime()
    {
        var observeCount = 0;
        var handleCount = 0;
        var delayCount = 0;
        var saveCount = 0;
        var runtime = new CareerRuntimeState
        {
            ScenarioId = "ura",
            PhaseId = "career",
            TurnIndex = 17,
            LastScreenId = "career_main",
            LastAction = UraPlannedAction.Rest,
            CareerStarted = true,
        };
        var bindings = Bindings(
            observe: (_, _, _) =>
            {
                observeCount++;
                return Task.FromResult<CareerObservation?>(null);
            },
            handle: (_, _) =>
            {
                handleCount++;
                return Task.FromResult(new CareerRuntimeStep());
            },
            save: _ =>
            {
                saveCount++;
                return Task.CompletedTask;
            },
            delay: (_, _) =>
            {
                delayCount++;
                return Task.CompletedTask;
            });

        var result = await CareerRuntimeLoop.RunAsync(
            runtime, bindings, actionsCompleted: 2, firstObservation: null,
            resumeRecoveryPending: false, CancellationToken.None,
            recognitionRetryLimit: 1);

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.ActionsCompleted);
        Assert.Equal("career_main", result.LastScreenId);
        Assert.Contains("Could not recognize a stable Career screen", result.Message);
        Assert.Equal(2, observeCount);
        Assert.Equal(1, delayCount);
        Assert.Equal(1, saveCount);
        Assert.Equal(0, handleCount);
        Assert.Equal(17, runtime.TurnIndex);
        Assert.Equal("career", runtime.PhaseId);
        Assert.Equal("career_main", runtime.LastScreenId);
        Assert.Equal(UraPlannedAction.Rest, runtime.LastAction);
        Assert.True(runtime.CareerStarted);
    }

    [Fact]
    public async Task Resumed_first_observation_is_consumed_once_and_recovery_closes_after_regular_observation()
    {
        var handledScreens = new List<string>();
        var observedRecoveryFlags = new List<bool>();
        var observationIndex = 0;
        var delayCount = 0;
        var firstObservation = new CareerObservation("training_result", 0.99)
        {
            ClassifiedKind = CareerScreenKind.Turn,
        };
        var bindings = Bindings(
            observe: (_, resumeRecovery, _) =>
            {
                observedRecoveryFlags.Add(resumeRecovery);
                var screenId = ++observationIndex == 1 ? "career_main" : "career_result";
                return Task.FromResult<CareerObservation?>(new(screenId, 0.99));
            },
            handle: (observation, _) =>
            {
                handledScreens.Add(observation.ScreenId);
                return observation.ScreenId switch
                {
                    "training_result" => Task.FromResult(new CareerRuntimeStep(
                        ActionsCompleted: 1, AwaitingTransition: true)),
                    "career_main" => Task.FromResult(new CareerRuntimeStep(ActionsCompleted: 1)),
                    _ => Task.FromResult(new CareerRuntimeStep(
                        new CareerTrainingResult(false, "stopped", 99, observation.ScreenId))),
                };
            },
            delay: (_, _) =>
            {
                delayCount++;
                return Task.CompletedTask;
            });

        var result = await CareerRuntimeLoop.RunAsync(
            new CareerRuntimeState { LastScreenId = "training_result", TurnIndex = 3 },
            bindings, actionsCompleted: 2, firstObservation,
            resumeRecoveryPending: true, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(["training_result", "career_main", "career_result"], handledScreens);
        Assert.Equal([true, false], observedRecoveryFlags);
        Assert.Equal(2, observationIndex);
        Assert.Equal(1, delayCount);
        Assert.Equal(4, result.ActionsCompleted);
        Assert.Equal("career_result", result.LastScreenId);
    }

    [Fact]
    public async Task Cancellation_during_transition_wait_propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var observeCount = 0;
        var delayCount = 0;
        var bindings = Bindings(
            before: _ => Task.FromResult(new CareerRuntimeStep(AwaitingTransition: true)),
            observe: (_, _, _) =>
            {
                observeCount++;
                return Task.FromResult<CareerObservation?>(null);
            },
            delay: (_, _) =>
            {
                delayCount++;
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CareerRuntimeLoop.RunAsync(
                new CareerRuntimeState(), bindings, actionsCompleted: 0,
                firstObservation: null, resumeRecoveryPending: false,
                cancellation.Token));

        Assert.Equal(1, delayCount);
        Assert.Equal(0, observeCount);
    }

    [Fact]
    public async Task Terminal_action_count_includes_initial_and_performed_actions()
    {
        var runtime = new CareerRuntimeState { LastScreenId = "career_main" };
        var bindings = Bindings(
            before: _ => Task.FromResult(new CareerRuntimeStep(ActionsCompleted: 2)),
            observe: (_, _, _) => Task.FromResult<CareerObservation?>(new("career_main", 1)),
            handle: (_, _) => Task.FromResult(new CareerRuntimeStep(
                new CareerTrainingResult(true, "complete", 99, "career_main"),
                ActionsCompleted: 4)));

        var result = await CareerRuntimeLoop.RunAsync(
            runtime, bindings, actionsCompleted: 3, firstObservation: null,
            resumeRecoveryPending: false, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(9, result.ActionsCompleted);
        Assert.Equal("career_main", result.LastScreenId);
    }

    private static CareerRuntimeLoopBindings Bindings(
        Func<CancellationToken, Task<CareerRuntimeStep>>? before = null,
        Func<bool, bool, CancellationToken, Task<CareerObservation?>>? observe = null,
        Func<CareerObservation, CancellationToken, Task<CareerRuntimeStep>>? handle = null,
        Func<CancellationToken, Task>? save = null,
        Func<int, CancellationToken, Task>? delay = null) =>
        new(
            IsStartTransitionExpected: static () => false,
            BeforeObservationAsync: before ?? (_ => Task.FromResult(new CareerRuntimeStep())),
            ObserveAsync: observe ?? ((_, _, _) => Task.FromResult<CareerObservation?>(null)),
            HandleObservationAsync: handle ?? ((_, _) => Task.FromResult(new CareerRuntimeStep())),
            SaveRecognitionFailureAsync: save ?? (_ => Task.CompletedTask),
            DelayAsync: delay ?? ((_, _) => Task.CompletedTask));
}
