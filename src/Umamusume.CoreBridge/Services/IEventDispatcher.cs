namespace Umamusume.CoreBridge;

public interface IEventDispatcher
{
    void Post(Action action);
}
