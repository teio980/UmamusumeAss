using System.Runtime.InteropServices;

namespace Umamusume.CoreBridge;

[StructLayout(LayoutKind.Sequential)]
internal readonly struct UmaAppendTaskResult(ulong taskId, int errorCode)
{
    public readonly ulong TaskId = taskId;
    public readonly int ErrorCode = errorCode;
}
