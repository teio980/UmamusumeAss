using System.Windows.Threading;
using Umamusume.CoreBridge;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// WPF dispatcher adapter that posts actions to the UI thread.
/// Uses <see cref="Dispatcher.InvokeAsync"/> for asynchronous execution.
/// </summary>
public sealed class WpfEventDispatcher : IEventDispatcher
{
    private readonly Dispatcher _dispatcher;

    /// <summary>
    /// Creates the adapter using <c>Application.Current.Dispatcher</c>,
    /// falling back to <c>Dispatcher.CurrentDispatcher</c> for test environments.
    /// </summary>
    public WpfEventDispatcher()
        : this(System.Windows.Application.Current?.Dispatcher
               ?? Dispatcher.CurrentDispatcher)
    {
    }

    /// <summary>
    /// Creates the adapter with an explicit dispatcher (test injection).
    /// </summary>
    public WpfEventDispatcher(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Posts an action to the WPF dispatcher queue for asynchronous execution.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="action"/> is null.</exception>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _dispatcher.InvokeAsync(action);
    }
}
