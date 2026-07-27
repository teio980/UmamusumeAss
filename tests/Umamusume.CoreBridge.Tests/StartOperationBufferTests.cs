using System.Collections.Concurrent;

namespace Umamusume.CoreBridge.Tests;

public sealed class StartOperationBufferTests
{
    [Fact]
    public void BindReplaysBufferedCallbacksInArrivalOrder()
    {
        var routed = new ConcurrentQueue<RawCallback>();
        var buffer = new StartOperationBuffer(routed.Enqueue);
        RawCallback first = Callback(1);
        RawCallback second = Callback(2);

        buffer.Accept(first);
        buffer.Accept(second);
        buffer.Bind(42);

        Assert.Equal([first, second], routed);
        Assert.Equal(42UL, buffer.OperationId);
        Assert.Equal(StartOperationBufferState.Direct, buffer.State);
        Assert.Equal(0, buffer.BufferedCount);
    }

    [Fact]
    public void BindWithEmptyBufferSwitchesToDirectRouting()
    {
        var routed = new ConcurrentQueue<RawCallback>();
        var buffer = new StartOperationBuffer(routed.Enqueue);
        RawCallback callback = Callback(1);

        buffer.Bind(42);
        buffer.Accept(callback);

        Assert.Equal([callback], routed);
    }

    [Fact]
    public void BindCannotBeCalledTwice()
    {
        var buffer = new StartOperationBuffer(_ => { });
        buffer.Bind(42);

        Assert.Throws<InvalidOperationException>(() => buffer.Bind(43));
    }

    [Fact]
    public void RejectReturnsBufferedCallbacksAndRejectsLaterAcceptance()
    {
        var buffer = new StartOperationBuffer(_ => { });
        RawCallback callback = Callback(1);
        buffer.Accept(callback);

        IReadOnlyList<RawCallback> rejected = buffer.Reject();

        Assert.Equal([callback], rejected);
        Assert.Equal(StartOperationBufferState.Rejected, buffer.State);
        Assert.Throws<InvalidOperationException>(() => buffer.Accept(Callback(2)));
        Assert.Throws<InvalidOperationException>(() => buffer.Bind(42));
    }

    [Fact]
    public async Task CallbackArrivingDuringReplayCannotOvertakeBufferedCallbacks()
    {
        var routed = new ConcurrentQueue<RawCallback>();
        using var firstRoutingStarted = new ManualResetEventSlim();
        using var continueReplay = new ManualResetEventSlim();
        var buffer = new StartOperationBuffer(callback =>
        {
            routed.Enqueue(callback);
            if (callback.MessageId == 1)
            {
                firstRoutingStarted.Set();
                continueReplay.Wait();
            }
        });
        RawCallback first = Callback(1);
        RawCallback second = Callback(2);
        RawCallback duringReplay = Callback(3);
        buffer.Accept(first);
        buffer.Accept(second);

        Task bindTask = Task.Run(() => buffer.Bind(42));
        Assert.True(firstRoutingStarted.Wait(TimeSpan.FromSeconds(5)));
        buffer.Accept(duringReplay);
        continueReplay.Set();
        await bindTask;

        Assert.Equal([first, second, duringReplay], routed);
    }

    [Fact]
    public async Task ConcurrentAcceptAndBindAccountForEveryCallbackExactlyOnce()
    {
        var routed = new ConcurrentQueue<RawCallback>();
        var buffer = new StartOperationBuffer(routed.Enqueue);
        using var ready = new Barrier(2);
        RawCallback callback = Callback(1);

        Task acceptTask = Task.Run(() =>
        {
            ready.SignalAndWait();
            buffer.Accept(callback);
        });
        Task bindTask = Task.Run(() =>
        {
            ready.SignalAndWait();
            buffer.Bind(42);
        });

        await Task.WhenAll(acceptTask, bindTask);

        Assert.Equal([callback], routed);
    }

    private static RawCallback Callback(int messageId) => new(messageId, $$"""{"id":{{messageId}}}""");
}
