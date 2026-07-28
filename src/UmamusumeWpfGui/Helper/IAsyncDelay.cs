namespace UmamusumeWpfGui.Helper;

public interface IAsyncDelay
{
    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default);
}

public sealed class AsyncDelay : IAsyncDelay
{
    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default) =>
        Task.Delay(duration, cancellationToken);
}
