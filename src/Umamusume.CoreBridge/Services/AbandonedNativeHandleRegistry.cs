using System.Collections.Concurrent;

namespace Umamusume.CoreBridge;

internal static class AbandonedNativeHandleRegistry
{
    private static readonly ConcurrentDictionary<IntPtr, UmaApiCallback> Abandoned = new();
    private static readonly ConcurrentDictionary<Task, UmaApiCallback> Destroying = new();

    internal static void RetainAbandoned(IntPtr handle, UmaApiCallback callback)
    {
        if (handle != IntPtr.Zero)
        {
            Abandoned.TryAdd(handle, callback);
        }
    }

    internal static void RetainUntilDestroyCompletes(Task destroyTask, UmaApiCallback callback)
    {
        Destroying.TryAdd(destroyTask, callback);
        _ = destroyTask.ContinueWith(
            static (completed, state) =>
            {
                var registry = (ConcurrentDictionary<Task, UmaApiCallback>)state!;
                registry.TryRemove(completed, out _);
            },
            Destroying,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
