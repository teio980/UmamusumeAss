using Microsoft.Win32.SafeHandles;

namespace Umamusume.CoreBridge;

internal sealed class SafeDecodedImageHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeDecodedImageHandle(IntPtr handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        try
        {
            UmaCoreBridgeNative.ReleaseDecodedImageNative(handle);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
