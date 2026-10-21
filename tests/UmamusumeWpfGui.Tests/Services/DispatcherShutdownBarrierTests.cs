using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class DispatcherShutdownBarrierTests
{
    [Fact]
    public void Wait_PumpsDispatcherUntilShutdownContinuationCompletes()
    {
        RunOnSta(dispatcher =>
        {
            var callbackCompleted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task shutdownTask = Task.Run(async () =>
            {
                await Task.Delay(20).ConfigureAwait(false);
                _ = dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new Action(() => callbackCompleted.TrySetResult(true)));
                await callbackCompleted.Task.ConfigureAwait(false);
            });

            Assert.True(DispatcherShutdownBarrier.Wait(
                shutdownTask,
                dispatcher,
                TimeSpan.FromSeconds(2)));
            Assert.True(callbackCompleted.Task.IsCompletedSuccessfully);
            Assert.True(shutdownTask.IsCompletedSuccessfully);
        });
    }

    [Fact]
    public void Wait_ObservesAndPropagatesShutdownFailure()
    {
        RunOnSta(dispatcher =>
        {
            var callbackCompleted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task shutdownTask = Task.Run(async () =>
            {
                _ = dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new Action(() => callbackCompleted.TrySetException(
                        new InvalidOperationException("shutdown failed"))));
                await callbackCompleted.Task.ConfigureAwait(false);
            });

            var exception = Assert.Throws<InvalidOperationException>(() =>
                DispatcherShutdownBarrier.Wait(
                    shutdownTask,
                    dispatcher,
                    TimeSpan.FromSeconds(2)));

            Assert.Equal("shutdown failed", exception.Message);
            Assert.True(shutdownTask.IsFaulted);
        });
    }

    [Fact]
    public void Wait_ReturnsAtBudgetAndObservesLaterFaultWithoutJoiningTask()
    {
        RunOnSta(dispatcher =>
        {
            using var lateFaultObserved = new ManualResetEventSlim();
            var pending = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            bool completed = DispatcherShutdownBarrier.Wait(
                pending.Task,
                dispatcher,
                TimeSpan.FromMilliseconds(100),
                _ => lateFaultObserved.Set());

            Assert.False(completed);
            Assert.False(pending.Task.IsCompleted);

            pending.SetException(new InvalidOperationException("late shutdown failure"));
            Assert.True(lateFaultObserved.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(pending.Task.IsFaulted);
        });
    }

    private static void RunOnSta(Action<Dispatcher> action)
    {
        Exception? failure = null;
        var finished = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                action(Dispatcher.CurrentDispatcher);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                finished.Set();
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!finished.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The STA dispatcher test did not complete within five seconds.");

        thread.Join(TimeSpan.FromSeconds(1));
        finished.Dispose();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
