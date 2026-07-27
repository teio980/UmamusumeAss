using System.Runtime.InteropServices;
using System.Text;

namespace Umamusume.CoreBridge;

internal sealed unsafe class UTF8Pinned : IDisposable
{
    private GCHandle _pin;
    private bool _disposed;

    public UTF8Pinned(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Bytes = Encoding.UTF8.GetBytes(value + '\0');
        _pin = GCHandle.Alloc(Bytes, GCHandleType.Pinned);
    }

    internal byte[] Bytes { get; }

    internal byte* Pointer => _disposed
        ? throw new ObjectDisposedException(nameof(UTF8Pinned))
        : (byte*)_pin.AddrOfPinnedObject();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _pin.Free();
        _disposed = true;
    }
}
