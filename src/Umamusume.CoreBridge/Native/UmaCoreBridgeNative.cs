using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Umamusume.CoreBridge;

internal sealed unsafe partial class UmaCoreBridgeNative : IUmaNativeApi
{
    private const string LibraryName = "UmamusumeCore.dll";

    public string GetVersion()
    {
        string? version = Marshal.PtrToStringUTF8(GetVersionNative());
        return string.IsNullOrWhiteSpace(version)
            ? throw new InvalidOperationException("The native core returned an empty version.")
            : version;
    }

    public SafeUmaHandle Create(UmaApiCallback callback, IntPtr customArg)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return new SafeUmaHandle(CreateNative(callback, customArg), DestroyNative);
    }

    public int SetUserDir(string path)
    {
        using var utf8Path = new UTF8Pinned(path);
        return SetUserDirNative(utf8Path.Pointer);
    }

    public int LoadResource(string path)
    {
        using var utf8Path = new UTF8Pinned(path);
        return LoadResourceNative(utf8Path.Pointer);
    }

    public UmaStartResult Connect(SafeUmaHandle handle, string adbPath, string serial, string profile)
    {
        using var utf8AdbPath = new UTF8Pinned(adbPath);
        using var utf8Serial = new UTF8Pinned(serial);
        using var utf8Profile = new UTF8Pinned(profile);
        return ConnectNative(handle, utf8AdbPath.Pointer, utf8Serial.Pointer, utf8Profile.Pointer);
    }

    public int CancelConnect(SafeUmaHandle handle, ulong operationId) =>
        CancelConnectNative(handle, operationId);

    public int CancelOperation(SafeUmaHandle handle, ulong operationId) =>
        CancelOperationNative(handle, operationId);

    public UmaStartResult VerifyGame(SafeUmaHandle handle, string packageId)
    {
        using var utf8PackageId = new UTF8Pinned(packageId);
        return VerifyGameNative(handle, utf8PackageId.Pointer);
    }

    public UmaStartResult Capture(SafeUmaHandle handle) => CaptureNative(handle);

    public int GetFramePngSize(SafeUmaHandle handle, ulong frameId, out ulong size)
    {
        ulong nativeSize = 0;
        int result = GetFramePngSizeNative(handle, frameId, &nativeSize);
        size = nativeSize;
        return result;
    }

    public int CopyFramePng(SafeUmaHandle handle, ulong frameId, Span<byte> destination)
    {
        fixed (byte* destinationPointer = destination)
        {
            return CopyFramePngNative(handle, frameId, destinationPointer, (ulong)destination.Length);
        }
    }

    public int ReleaseFrame(SafeUmaHandle handle, ulong frameId) =>
        ReleaseFrameNative(handle, frameId);

    public UmaStartResult Tap(SafeUmaHandle handle, ulong frameId, int x, int y) =>
        TapNative(handle, frameId, x, y);

    public UmaStartResult Swipe(
        SafeUmaHandle handle,
        ulong frameId,
        int x1,
        int y1,
        int x2,
        int y2,
        int durationMs) =>
        SwipeNative(handle, frameId, x1, y1, x2, y2, durationMs);

    [LibraryImport(LibraryName, EntryPoint = "UmaGetVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial IntPtr GetVersionNative();

    [LibraryImport(LibraryName, EntryPoint = "UmaCreate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial IntPtr CreateNative(
        [MarshalAs(UnmanagedType.FunctionPtr)] UmaApiCallback callback,
        IntPtr customArg);

    [LibraryImport(LibraryName, EntryPoint = "UmaDestroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial void DestroyNative(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "UmaSetUserDir")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int SetUserDirNative(byte* path);

    [LibraryImport(LibraryName, EntryPoint = "UmaLoadResource")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int LoadResourceNative(byte* path);

    [LibraryImport(LibraryName, EntryPoint = "UmaConnectAsync")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaStartResult ConnectNative(
        SafeUmaHandle handle,
        byte* adbPath,
        byte* serial,
        byte* profile);

    [LibraryImport(LibraryName, EntryPoint = "UmaCancelConnect")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int CancelConnectNative(SafeUmaHandle handle, ulong operationId);

    [LibraryImport(LibraryName, EntryPoint = "UmaCancelOperation")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int CancelOperationNative(SafeUmaHandle handle, ulong operationId);

    [LibraryImport(LibraryName, EntryPoint = "UmaVerifyGameAsync")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaStartResult VerifyGameNative(SafeUmaHandle handle, byte* packageId);

    [LibraryImport(LibraryName, EntryPoint = "UmaCaptureAsync")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaStartResult CaptureNative(SafeUmaHandle handle);

    [LibraryImport(LibraryName, EntryPoint = "UmaGetFramePngSize")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int GetFramePngSizeNative(SafeUmaHandle handle, ulong frameId, ulong* size);

    [LibraryImport(LibraryName, EntryPoint = "UmaCopyFramePng")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int CopyFramePngNative(
        SafeUmaHandle handle,
        ulong frameId,
        byte* destination,
        ulong capacity);

    [LibraryImport(LibraryName, EntryPoint = "UmaReleaseFrame")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int ReleaseFrameNative(SafeUmaHandle handle, ulong frameId);

    [LibraryImport(LibraryName, EntryPoint = "UmaTapAsync")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaStartResult TapNative(SafeUmaHandle handle, ulong frameId, int x, int y);

    [LibraryImport(LibraryName, EntryPoint = "UmaSwipeAsync")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaStartResult SwipeNative(
        SafeUmaHandle handle,
        ulong frameId,
        int x1,
        int y1,
        int x2,
        int y2,
        int durationMs);
}
