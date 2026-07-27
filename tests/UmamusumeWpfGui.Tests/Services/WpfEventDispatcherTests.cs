using System;
using System.Threading;
using System.Windows.Threading;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class WpfEventDispatcherTests
{
    // ================================================================
    // Null guard
    // ================================================================

    [Fact]
    public void Post_NullAction_ThrowsArgumentNullException()
    {
        var adapter = new WpfEventDispatcher(Dispatcher.CurrentDispatcher);
        Assert.Throws<ArgumentNullException>(() => adapter.Post(null!));
    }

    // ================================================================
    // Asynchronous dispatcher behavior
    // ================================================================

    [Fact]
    public void Post_Action_InvokesOnDispatcherThread()
    {
        using var invokedEvent = new ManualResetEventSlim(initialState: false);
        int? callbackThreadId = null;

        var thread = new Thread(() =>
        {
            // Create a dispatcher for this thread
            var dispatcher = Dispatcher.CurrentDispatcher;

            var adapter = new WpfEventDispatcher(dispatcher);

            // Post an action to the dispatcher
            adapter.Post(() =>
            {
                callbackThreadId = Environment.CurrentManagedThreadId;
                invokedEvent.Set();
            });

            // Pump messages until the frame is told to stop
            var frame = new DispatcherFrame();
            adapter.Post(() => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // Wait with timeout
        Assert.True(invokedEvent.Wait(5000), "Action was not invoked within timeout");
        Assert.NotNull(callbackThreadId);
        Assert.NotEqual(Environment.CurrentManagedThreadId, callbackThreadId.Value);
    }

    [Fact]
    public void Post_MultipleActions_InvokesInOrder()
    {
        var results = new System.Collections.Generic.List<int>();
        using var completed = new ManualResetEventSlim(initialState: false);

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var adapter = new WpfEventDispatcher(dispatcher);

            adapter.Post(() => results.Add(1));
            adapter.Post(() => results.Add(2));
            adapter.Post(() => results.Add(3));
            adapter.Post(() =>
            {
                completed.Set();
            });

            var frame = new DispatcherFrame();
            adapter.Post(() => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(completed.Wait(5000), "Actions were not invoked within timeout");
        Assert.Equal([1, 2, 3], results);
    }

    [Fact]
    public void Post_CanBeCalledFromBackgroundThread()
    {
        using var posted = new ManualResetEventSlim(initialState: false);
        using var completed = new ManualResetEventSlim(initialState: false);

        var dispatcherThread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var adapter = new WpfEventDispatcher(dispatcher);

            var frame = new DispatcherFrame();

            // Background thread posts to the dispatcher
            var bg = new Thread(() =>
            {
                posted.Set();
                adapter.Post(() =>
                {
                    completed.Set();
                    frame.Continue = false;
                });
            });
            bg.Start();

            posted.Wait();
            Dispatcher.PushFrame(frame);
        });

        dispatcherThread.SetApartmentState(ApartmentState.STA);
        dispatcherThread.Start();

        Assert.True(completed.Wait(5000), "Action from background thread was not invoked within timeout");
    }
}
