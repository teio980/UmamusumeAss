using Microsoft.Win32.SafeHandles;

namespace Umamusume.CoreBridge;

internal sealed class SafeUmaHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private Action<IntPtr>? _destroy;

    internal SafeUmaHandle(IntPtr handle, Action<IntPtr> destroy)
        : base(ownsHandle: true)
    {
        ArgumentNullException.ThrowIfNull(destroy);
        _destroy = destroy;
        SetHandle(handle);
    }

    internal IntPtr Abandon()
    {
        IntPtr rawHandle = DangerousGetHandle();
        Interlocked.Exchange(ref _destroy, null);
        SetHandleAsInvalid();
        return rawHandle;
    }

    protected override bool ReleaseHandle()
    {
        Action<IntPtr>? destroy = Interlocked.Exchange(ref _destroy, null);
        if (destroy is null)
        {
            return true;
        }

        try
        {
            destroy(handle);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
