using System.Windows.Threading;
using Umamusume.CoreBridge;

namespace UmamusumeWpfGui.Services;





public sealed class WpfEventDispatcher : IEventDispatcher
{
    private readonly Dispatcher _dispatcher;





    public WpfEventDispatcher()
        : this(System.Windows.Application.Current?.Dispatcher
               ?? Dispatcher.CurrentDispatcher)
    {
    }




    public WpfEventDispatcher(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }





    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _dispatcher.InvokeAsync(action);
    }
}
