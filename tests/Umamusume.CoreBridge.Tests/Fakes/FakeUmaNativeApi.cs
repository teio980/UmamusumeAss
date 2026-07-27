using System.Runtime.InteropServices;
using System.Text;

namespace Umamusume.CoreBridge.Tests.Fakes;

internal sealed class FakeUmaNativeApi : IUmaNativeApi
{
    private UmaApiCallback? _callback;

    internal List<string> Calls { get; } = [];
    internal int SetUserDirResult { get; set; }
    internal int LoadResourceResult { get; set; }
    internal string Version { get; set; } = "0.1.0";
    internal bool CreateInvalidHandle { get; set; }
    internal int DestroyCalls { get; private set; }
    internal UmaStartResult ConnectResult { get; set; } = new(0, (int)ConnectionErrorCode.InvalidArgument);

    public string GetVersion()
    {
        Calls.Add(nameof(GetVersion));
        return Version;
    }

    public SafeUmaHandle Create(UmaApiCallback callback, IntPtr customArg)
    {
        Calls.Add(nameof(Create));
        _callback = callback;
        IntPtr value = CreateInvalidHandle ? IntPtr.Zero : (IntPtr)42;
        return new SafeUmaHandle(value, _ => DestroyCalls++);
    }

    public int SetUserDir(string path)
    {
        Calls.Add(nameof(SetUserDir));
        return SetUserDirResult;
    }

    public int LoadResource(string path)
    {
        Calls.Add(nameof(LoadResource));
        return LoadResourceResult;
    }

    public UmaStartResult Connect(SafeUmaHandle handle, string adbPath, string serial, string profile)
    {
        Calls.Add(nameof(Connect));
        return ConnectResult;
    }

    public int CancelConnect(SafeUmaHandle handle, ulong operationId) =>
        (int)ConnectionErrorCode.InvalidArgument;

    public int CancelOperation(SafeUmaHandle handle, ulong operationId) =>
        (int)ConnectionErrorCode.InvalidArgument;

    public UmaStartResult VerifyGame(SafeUmaHandle handle, string packageId) =>
        new(0, (int)ConnectionErrorCode.InvalidArgument);

    public UmaStartResult Capture(SafeUmaHandle handle) =>
        new(0, (int)ConnectionErrorCode.InvalidArgument);

    public int GetFramePngSize(SafeUmaHandle handle, ulong frameId, out ulong size)
    {
        size = 0;
        return (int)ConnectionErrorCode.InvalidArgument;
    }

    public int CopyFramePng(SafeUmaHandle handle, ulong frameId, Span<byte> destination) =>
        (int)ConnectionErrorCode.InvalidArgument;

    public int ReleaseFrame(SafeUmaHandle handle, ulong frameId) =>
        (int)ConnectionErrorCode.InvalidArgument;

    public UmaStartResult Tap(SafeUmaHandle handle, ulong frameId, int x, int y) =>
        new(0, (int)ConnectionErrorCode.InvalidArgument);

    public UmaStartResult Swipe(
        SafeUmaHandle handle,
        ulong frameId,
        int x1,
        int y1,
        int x2,
        int y2,
        int durationMs) =>
        new(0, (int)ConnectionErrorCode.InvalidArgument);

    internal unsafe void Emit(int messageId, string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json + '\0');
        fixed (byte* pointer = bytes)
        {
            _callback?.Invoke(messageId, (IntPtr)pointer, IntPtr.Zero);
        }
    }
}
