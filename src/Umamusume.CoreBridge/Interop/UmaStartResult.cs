using System.Runtime.InteropServices;

namespace Umamusume.CoreBridge;

[StructLayout(LayoutKind.Sequential)]
internal readonly struct UmaStartResult(ulong operationId, int errorCode)
{
    public readonly ulong OperationId = operationId;
    public readonly int ErrorCode = errorCode;
}
