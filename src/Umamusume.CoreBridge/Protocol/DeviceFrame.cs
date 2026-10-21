namespace Umamusume.CoreBridge;

/// <summary>A copied PNG frame that also retains its native frame lease for later input.</summary>
public sealed class DeviceFrame : IDisposable
{
    private readonly object _sync = new();
    private readonly object _owner;
    private Action<DeviceFrame>? _release;
    private int _activeUses;
    private bool _disposeRequested;

    internal DeviceFrame(
        object owner,
        DeviceFrameMetadata metadata,
        byte[] pngBytes,
        Action<DeviceFrame> release)
    {
        _owner = owner;
        Metadata = metadata;
        PngBytes = pngBytes;
        _release = release;
    }

    public DeviceFrameMetadata Metadata { get; }
    public ReadOnlyMemory<byte> PngBytes { get; }
    public ulong FrameId => Metadata.FrameId;

    public bool IsDisposed
    {
        get
        {
            lock (_sync)
            {
                return _disposeRequested;
            }
        }
    }

    public void Dispose()
    {
        Action<DeviceFrame>? release = null;
        lock (_sync)
        {
            if (_disposeRequested)
            {
                return;
            }

            _disposeRequested = true;
            if (_activeUses == 0)
            {
                release = _release;
                _release = null;
            }
        }

        release?.Invoke(this);
    }

    internal FrameUse Acquire(object owner)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(owner, _owner))
            {
                throw new ArgumentException("The device frame belongs to another service.", nameof(owner));
            }

            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            _activeUses++;
            return new FrameUse(this, FrameId);
        }
    }

    internal ulong GetFrameIdForTouchRelease(object owner)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(owner, _owner))
            {
                throw new ArgumentException("The device frame belongs to another service.", nameof(owner));
            }

            // UP/CANCEL must be tied to the press that created them. The lease
            // may already be disposed or detached, but its ID remains a safe
            // stale-press guard in the native session.
            return FrameId;
        }
    }

    internal void DetachFromOwner()
    {
        lock (_sync)
        {
            _disposeRequested = true;
            _release = null;
        }
    }

    private void ReleaseUse()
    {
        Action<DeviceFrame>? release = null;
        lock (_sync)
        {
            _activeUses--;
            if (_activeUses == 0 && _disposeRequested)
            {
                release = _release;
                _release = null;
            }
        }

        release?.Invoke(this);
    }

    internal sealed class FrameUse : IDisposable
    {
        private DeviceFrame? _frame;

        internal FrameUse(DeviceFrame frame, ulong frameId)
        {
            _frame = frame;
            FrameId = frameId;
        }

        internal ulong FrameId { get; }

        public void Dispose() => Interlocked.Exchange(ref _frame, null)?.ReleaseUse();
    }
}
