namespace Umamusume.CoreBridge;

internal enum StartOperationBufferState
{
    Starting,
    Replaying,
    Direct,
    Rejected,
}

internal sealed class StartOperationBuffer
{
    private readonly object _sync = new();
    private readonly List<RawCallback> _buffered = [];
    private readonly Action<RawCallback> _route;
    private ulong? _operationId;
    private StartOperationBufferState _state = StartOperationBufferState.Starting;

    internal StartOperationBuffer(Action<RawCallback> route)
    {
        ArgumentNullException.ThrowIfNull(route);
        _route = route;
    }

    internal ulong? OperationId
    {
        get
        {
            lock (_sync)
            {
                return _operationId;
            }
        }
    }

    internal int BufferedCount
    {
        get
        {
            lock (_sync)
            {
                return _buffered.Count;
            }
        }
    }

    internal StartOperationBufferState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    internal void Accept(RawCallback callback)
    {
        Action<RawCallback>? directRoute = null;
        lock (_sync)
        {
            switch (_state)
            {
                case StartOperationBufferState.Starting:
                case StartOperationBufferState.Replaying:
                    _buffered.Add(callback);
                    break;
                case StartOperationBufferState.Direct:
                    directRoute = _route;
                    break;
                case StartOperationBufferState.Rejected:
                    throw new InvalidOperationException("The native start was rejected.");
                default:
                    throw new InvalidOperationException("Unknown start buffer state.");
            }
        }

        directRoute?.Invoke(callback);
    }

    internal void Bind(ulong operationId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(operationId);

        lock (_sync)
        {
            if (_state != StartOperationBufferState.Starting)
            {
                throw new InvalidOperationException("The start buffer has already been resolved.");
            }

            _operationId = operationId;
            _state = StartOperationBufferState.Replaying;
        }

        while (true)
        {
            RawCallback[] batch;
            lock (_sync)
            {
                if (_buffered.Count == 0)
                {
                    _state = StartOperationBufferState.Direct;
                    return;
                }

                batch = [.. _buffered];
                _buffered.Clear();
            }

            foreach (RawCallback callback in batch)
            {
                _route(callback);
            }
        }
    }

    internal IReadOnlyList<RawCallback> Reject()
    {
        lock (_sync)
        {
            if (_state != StartOperationBufferState.Starting)
            {
                throw new InvalidOperationException("The start buffer has already been resolved.");
            }

            RawCallback[] rejected = [.. _buffered];
            _buffered.Clear();
            _state = StartOperationBufferState.Rejected;
            return rejected;
        }
    }
}
