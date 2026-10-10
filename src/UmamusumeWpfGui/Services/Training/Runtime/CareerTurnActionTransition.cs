using System.Diagnostics;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>Observes a submitted break/treatment until a fresh turn destination is stable.</summary>
internal sealed class CareerTurnActionTransition(string confirmationScreenId, string label,
    TimeProvider? clock = null)
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly long _started = (clock ?? TimeProvider.System).GetTimestamp();
    private long _waitStarted = (clock ?? TimeProvider.System).GetTimestamp();
    private long? _lastFullRecognition;
    private GrayImage? _confirmationFrame;
    private long _confirmationCapturedAt;

    public string ConfirmationScreenId { get; } = confirmationScreenId;
    public string ConfirmationActionId => ConfirmationScreenId switch
    {
        "recreation_confirmation" => "recreation.confirm",
        "infirmary_confirmation" => "infirmary.confirm",
        _ => "rest.confirm",
    };
    public string Label { get; } = label;
    public bool AwaitingConfirmation { get; private set; } = true;
    public TimeSpan Elapsed => _clock.GetElapsedTime(_started);
    public bool Expired => _clock.GetElapsedTime(_waitStarted) >= Timeout;
    public int CaptureCount { get; private set; }
    public int ReusedFrames { get; private set; }
    public int FullRecognitionCount { get; private set; }
    private TimeSpan _captureDuration;
    private TimeSpan _matchDuration;

    public void ConfirmationDisappeared(GrayImage frame)
    {
        AwaitingConfirmation = false;
        _confirmationFrame = frame;
        _confirmationCapturedAt = Stopwatch.GetTimestamp();
        _waitStarted = _clock.GetTimestamp();
    }

    public void ActionCompleted(string screenId)
    {
        // A chain of successfully handled events gets a fresh waiting window.
        // Reobserving the submitted confirmation must never extend its deadline.
        if (!AwaitingConfirmation && screenId != ConfirmationScreenId)
            _waitStarted = _clock.GetTimestamp();
    }

    public async Task<CareerObservation?> ObserveAsync(CareerScreenObserver observer,
        LastVerifiedConnection connection, UraScenarioPack pack, UraCareerSessionState state,
        IGrassTaskLogSink? logSink, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (AwaitingConfirmation || Expired)
            return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout - _clock.GetElapsedTime(_waitStarted));
        var seed = _confirmationFrame;
        _confirmationFrame = null;
        if (seed is not null && Stopwatch.GetElapsedTime(_confirmationCapturedAt) > TimeSpan.FromSeconds(1))
            seed = null;
        if (seed is not null)
            ReusedFrames++;
        try
        {
            CareerObservation? observation;
            try
            {
                observation = await observer.ObserveTurnActionReturnAsync(connection, pack, state,
                    deadline.Token, seed).ConfigureAwait(false);
            }
            finally { Record(observer); }
            // Recover unexpected screens occasionally without broad scanning on every poll.
            if (observation is null && Elapsed >= TimeSpan.FromSeconds(5)
                && (_lastFullRecognition is not { } last
                    || _clock.GetElapsedTime(last) >= TimeSpan.FromSeconds(10)))
            {
                FullRecognitionCount++;
                try
                {
                    observation = await observer.ObserveAsync(connection, pack, state, false,
                        deadline.Token).ConfigureAwait(false);
                }
                finally
                {
                    _lastFullRecognition = _clock.GetTimestamp();
                    Record(observer);
                }
            }
            if (observation is null || Expired)
                return null;
            logSink?.Add("Career Training",
                $"{Label} return timing: elapsedMs={Elapsed.TotalMilliseconds:0}, screen={observation.ScreenId}, "
                + $"captures={CaptureCount}, reusedFrames={ReusedFrames}, fullRecognition={FullRecognitionCount}, "
                + $"captureMs={_captureDuration.TotalMilliseconds:0}, matchMs={_matchDuration.TotalMilliseconds:0}.");
            if (observation.ScreenId is "career_main" or "race_day" or "career_races_ready"
                or "claw_machine" or "claw_machine_result"
                || observation.Kind == CareerScreenKind.Settlement)
                state.Runtime.TurnActionTransition = null;
            return observation;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
            && deadline.IsCancellationRequested)
        {
            return null;
        }
    }

    private void Record(CareerScreenObserver observer)
    {
        CaptureCount += observer.LastCaptureCount;
        _captureDuration += observer.LastCaptureDuration;
        _matchDuration += observer.LastMatchDuration;
    }
}
