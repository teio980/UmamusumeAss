using System.Windows.Threading;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// Waits for shutdown work without blocking the WPF dispatcher that may be
/// needed by pending continuations. A false result means the budget elapsed;
/// callers must not synchronously join the still-running task.
/// </summary>
internal static class DispatcherShutdownBarrier
{
    public static bool Wait(
        Task shutdownTask,
        Dispatcher dispatcher,
        TimeSpan budget,
        Action<Exception>? lateFaultObserver = null)
    {
        ArgumentNullException.ThrowIfNull(shutdownTask);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, TimeSpan.Zero);

        if (shutdownTask.IsCompleted)
        {
            shutdownTask.GetAwaiter().GetResult();
            return true;
        }

        if (budget == TimeSpan.Zero)
        {
            ObserveLateFault(shutdownTask, lateFaultObserver);
            return false;
        }

        if (!dispatcher.CheckAccess())
        {
            bool completed;
            try
            {
                completed = shutdownTask.Wait(budget);
            }
            catch (AggregateException)
            {
                completed = true;
            }
            catch (OperationCanceledException)
            {
                completed = true;
            }

            if (completed)
            {
                shutdownTask.GetAwaiter().GetResult();
                return true;
            }

            ObserveLateFault(shutdownTask, lateFaultObserver);
            return false;
        }

        var frame = new DispatcherFrame();
        var deadline = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
        {
            Interval = budget,
        };
        deadline.Tick += (_, _) =>
        {
            deadline.Stop();
            frame.Continue = false;
        };
        deadline.Start();

        _ = shutdownTask.ContinueWith(
            _ => PostFrameExit(dispatcher, frame),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            Dispatcher.PushFrame(frame);
        }
        catch
        {
            ObserveLateFault(shutdownTask, lateFaultObserver);
            throw;
        }
        finally
        {
            deadline.Stop();
        }

        if (shutdownTask.IsCompleted)
        {
            shutdownTask.GetAwaiter().GetResult();
            return true;
        }

        ObserveLateFault(shutdownTask, lateFaultObserver);
        return false;
    }

    public static void ObserveLateFault(Task task, Action<Exception>? observer)
    {
        _ = task.ContinueWith(
            completed =>
            {
                Exception? exception = completed.Exception?.GetBaseException();
                if (exception is null)
                    return;

                try
                {
                    observer?.Invoke(exception);
                }
                catch
                {
                    // A diagnostic callback must not create a second unobserved task fault.
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void PostFrameExit(Dispatcher dispatcher, DispatcherFrame frame)
    {
        try
        {
            dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() => frame.Continue = false));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher shutdown has already made it impossible to post the
            // normal completion signal. The bounded deadline remains active.
        }
    }
}
