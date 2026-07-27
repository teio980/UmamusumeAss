namespace Umamusume.CoreBridge;

internal interface IUmaNativeApi
{
    string GetVersion();
    SafeUmaHandle Create(UmaApiCallback callback, IntPtr customArg);
    int SetUserDir(string path);
    int LoadResource(string path);
    UmaStartResult Connect(SafeUmaHandle handle, string adbPath, string serial, string profile);
    int CancelConnect(SafeUmaHandle handle, ulong operationId);
    int CancelOperation(SafeUmaHandle handle, ulong operationId);
    UmaStartResult VerifyGame(SafeUmaHandle handle, string packageId);
    UmaStartResult Capture(SafeUmaHandle handle);
    int GetFramePngSize(SafeUmaHandle handle, ulong frameId, out ulong size);
    int CopyFramePng(SafeUmaHandle handle, ulong frameId, Span<byte> destination);
    int ReleaseFrame(SafeUmaHandle handle, ulong frameId);
    UmaStartResult Tap(SafeUmaHandle handle, ulong frameId, int x, int y);
    UmaStartResult Swipe(SafeUmaHandle handle, ulong frameId, int x1, int y1, int x2, int y2, int durationMs);
}
