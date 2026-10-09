using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>Run-scoped timing and input guard for one visit to the race list.</summary>
internal sealed class CareerRaceListEntryTransition
{
    internal static readonly TimeSpan FastRecognitionWindow = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock;
    private readonly long _started;
    private long _phaseStarted;
    private long? _lastFullRecognition;
    private bool _retried;

    public CareerRaceListEntryTransition(string sourceScreenId, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _started = _phaseStarted = _clock.GetTimestamp();
        SourceScreenId = sourceScreenId;
    }

    public string SourceScreenId { get; private set; }
    public bool RetryRequested { get; private set; }
    public int FullRecognitionCount { get; private set; }
    public int ObservationCount { get; private set; }
    public int CaptureCount { get; private set; }
    public TimeSpan CaptureDuration { get; private set; }
    public TimeSpan MatchDuration { get; private set; }
    public TimeSpan Elapsed => _clock.GetElapsedTime(_started);
    public bool Expired => Elapsed >= Timeout;
    public bool ShouldUseFullRecognition =>
        _clock.GetElapsedTime(_phaseStarted) >= FastRecognitionWindow
        && (_lastFullRecognition is not { } last
            || _clock.GetElapsedTime(last) >= FastRecognitionWindow);

    private void RecordObservation(CareerScreenObserver observer)
    {
        ObservationCount++;
        CaptureCount += observer.LastCaptureCount;
        CaptureDuration += observer.LastCaptureDuration;
        MatchDuration += observer.LastMatchDuration;
    }

    private void RecordFullRecognition()
    {
        _lastFullRecognition = _clock.GetTimestamp();
        FullRecognitionCount++;
    }

    public bool RequestRetry()
    {
        if (_retried || Expired)
            return false;
        _retried = RetryRequested = true;
        return true;
    }

    public void ActionCompleted(string sourceScreenId)
    {
        SourceScreenId = sourceScreenId;
        RetryRequested = false;
        _phaseStarted = _clock.GetTimestamp();
        _lastFullRecognition = null;
    }

    internal static async Task<CareerTrainingResult?> RunActionAsync(
        ICareerFlowActionRunner actions, CareerFlowContext context, string screenId, string actionId)
    {
        var result = await actions.RunAsync(context, screenId, actionId).ConfigureAwait(false);
        if (result is null)
        {
            var transition = context.State.Runtime.RaceListEntryTransition ??=
                new CareerRaceListEntryTransition(screenId);
            transition.ActionCompleted(screenId);
            context.LogSink?.Add("Career Training",
                $"Race list entry fast recognition armed from {screenId}.{actionId}.");
        }
        return result;
    }

    public async Task<CareerObservation?> ObserveAsync(
        CareerScreenObserver observer, LastVerifiedConnection connection, UraScenarioPack pack,
        UraCareerSessionState state, IGrassTaskLogSink? logSink, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = Timeout - Elapsed;
        if (remaining <= TimeSpan.Zero)
            return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining);
        try
        {
            var observation = await observer.ObserveRaceListEntryAsync(connection, pack, state, deadline.Token)
                .ConfigureAwait(false);
            RecordObservation(observer);
            if (observation is null || observation.ScreenId == SourceScreenId)
            {
                if (Expired || !ShouldUseFullRecognition)
                    return null;
                logSink?.Add("Career Training", "Race list entry is still pending; checking other Career screens.");
                // An unchanged source main page only needs a button retry check;
                // its OCR cannot drive a new strategy decision during this entry.
                observation = await observer.ObserveAsync(connection, pack, state, false, deadline.Token,
                        includeMainDetails: SourceScreenId != "career_main")
                    .ConfigureAwait(false);
                RecordFullRecognition();
                RecordObservation(observer);
                if (observation?.ScreenId == SourceScreenId)
                {
                    if (!Expired && !_retried
                        && await observer.CanRetryRaceListEntryAsync(pack, SourceScreenId, deadline.Token)
                            .ConfigureAwait(false)
                        && RequestRetry())
                    {
                        logSink?.Add("Career Training",
                            "The race entry page and button are unchanged in two frames; allowing one verified retry.");
                        return observation;
                    }
                    return null;
                }
            }
            if (observation is null || Expired)
                return null;

            logSink?.Add("Career Training",
                $"Race list entry timing: elapsedMs={Elapsed.TotalMilliseconds:0}, screen={observation.ScreenId}, "
                + $"observations={ObservationCount}, captures={CaptureCount}, fullRecognition={FullRecognitionCount}, "
                + $"captureMs={CaptureDuration.TotalMilliseconds:0}, matchMs={MatchDuration.TotalMilliseconds:0}.");
            // The recommendations action keeps the same overall deadline, but starts
            // a new short recognition window after its confirmation is submitted.
            if (observation.ScreenId != "race_recommendations")
                state.Runtime.RaceListEntryTransition = null;
            return observation;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
            && deadline.IsCancellationRequested)
        {
            return null;
        }
    }
}
