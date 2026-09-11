using System.Collections.Concurrent;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

public interface IActivityRegistry
{
    IDisposable Acquire(ActivityKind kind);
    ActivitySnapshot Snapshot { get; }
    Task WaitForIdleAsync(CancellationToken cancellationToken = default);
}

public interface IIdleCoordinator
{
    Task WaitForIdleAsync(CancellationToken cancellationToken = default);
}

public sealed class ActivityRegistry : IActivityRegistry, IIdleCoordinator
{
    private readonly object _sync = new();
    private readonly Dictionary<ActivityKind, int> _counts = [];
    private TaskCompletionSource<bool> _idle = CompletedSource();

    public ActivitySnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return new ActivitySnapshot(
                    _counts.Values.Sum(),
                    new Dictionary<ActivityKind, int>(_counts));
            }
        }
    }

    public IDisposable Acquire(ActivityKind kind)
    {
        lock (_sync)
        {
            if (_counts.Values.Sum() == 0)
                _idle = NewSource();
            _counts[kind] = _counts.GetValueOrDefault(kind) + 1;
        }
        return new Lease(this, kind);
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task idle;
            lock (_sync)
            {
                if (_counts.Values.Sum() == 0)
                    return;
                idle = _idle.Task;
            }
            await idle.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void Release(ActivityKind kind)
    {
        lock (_sync)
        {
            if (_counts.TryGetValue(kind, out var count))
            {
                if (count <= 1) _counts.Remove(kind);
                else _counts[kind] = count - 1;
            }
            if (_counts.Values.Sum() == 0)
                _idle.TrySetResult(true);
        }
    }

    private sealed class Lease(ActivityRegistry owner, ActivityKind kind) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Release(kind);
        }
    }

    private static TaskCompletionSource<bool> NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<bool> CompletedSource()
    {
        var source = NewSource();
        source.SetResult(true);
        return source;
    }
}
