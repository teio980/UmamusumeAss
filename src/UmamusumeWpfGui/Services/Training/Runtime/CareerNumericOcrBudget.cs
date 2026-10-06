using System.Diagnostics;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>One candidate's fallback time, shared across fields and both frames.</summary>
internal sealed class CareerNumericOcrBudget
{
    private TimeSpan _remaining;

    public CareerNumericOcrBudget(TimeSpan? maximum = null) =>
        _remaining = maximum ?? TimeSpan.FromMilliseconds(2500);

    public async Task<int?> RunAsync(Func<CancellationToken, Task<int?>> read,
        CancellationToken cancellationToken, CancellationToken externalBudgetToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_remaining <= TimeSpan.Zero || externalBudgetToken.IsCancellationRequested)
            return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, externalBudgetToken);
        timeout.CancelAfter(_remaining);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await read(timeout.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return timeout.IsCancellationRequested ? null : result;
        }
        finally
        {
            _remaining = timeout.IsCancellationRequested ? TimeSpan.Zero
                : _remaining - Stopwatch.GetElapsedTime(started);
        }
    }
}
