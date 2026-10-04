namespace UmamusumeWpfGui.Services.Training;

internal sealed record CareerRuntimeStep(
    CareerTrainingResult? Terminal = null,
    int ActionsCompleted = 0,
    bool AwaitingTransition = false)
{
    public static implicit operator CareerRuntimeStep(CareerTrainingResult terminal) => new(terminal);
}

/// <summary>Scenario/mode adapters supply behavior; the loop owns no scenario-specific state.</summary>
internal sealed record CareerRuntimeLoopBindings(
    Func<bool> IsStartTransitionExpected,
    Func<CancellationToken, Task<CareerRuntimeStep>> BeforeObservationAsync,
    Func<bool, bool, CancellationToken, Task<CareerObservation?>> ObserveAsync,
    Func<CareerObservation, CancellationToken, Task<CareerRuntimeStep>> HandleObservationAsync,
    Func<CancellationToken, Task> SaveRecognitionFailureAsync,
    Func<int, CancellationToken, Task> DelayAsync);

internal static class CareerRuntimeLoop
{
    public static async Task<CareerTrainingResult> RunAsync(
        CareerRuntimeState runtime,
        CareerRuntimeLoopBindings bindings,
        int actionsCompleted,
        CareerObservation? firstObservation,
        bool resumeRecoveryPending,
        CancellationToken cancellationToken,
        int recognitionRetryLimit = 30,
        int startRetryLimit = 40)
    {
        var recognitionRetries = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = await bindings.BeforeObservationAsync(cancellationToken).ConfigureAwait(false);
            actionsCompleted += before.ActionsCompleted;
            if (before.Terminal is { } beforeTerminal)
                return beforeTerminal with { ActionsCompleted = actionsCompleted };
            if (before.AwaitingTransition)
            {
                await bindings.DelayAsync(250, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var startExpected = bindings.IsStartTransitionExpected();
            var observation = firstObservation;
            firstObservation = null;
            observation ??= await bindings.ObserveAsync(startExpected, resumeRecoveryPending, cancellationToken)
                .ConfigureAwait(false);
            if (observation is null)
            {
                var limit = startExpected ? startRetryLimit : recognitionRetryLimit;
                if (recognitionRetries++ < limit)
                {
                    await bindings.DelayAsync(250, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await bindings.SaveRecognitionFailureAsync(cancellationToken).ConfigureAwait(false);
                return CareerRuntimeResults.Failure(
                    "Could not recognize a stable Career screen; automation paused safely.",
                    runtime.LastScreenId, actionsCompleted);
            }

            recognitionRetries = 0;
            var step = await bindings.HandleObservationAsync(observation, cancellationToken).ConfigureAwait(false);
            actionsCompleted += step.ActionsCompleted;
            if (step.Terminal is { } terminal)
                return terminal with { ActionsCompleted = actionsCompleted };
            if (step.AwaitingTransition)
                await bindings.DelayAsync(250, cancellationToken).ConfigureAwait(false);
            else
                resumeRecoveryPending = false;
        }
    }
}
