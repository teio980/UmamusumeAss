namespace Umamusume.CoreBridge;

public sealed class ManagedBridgeException : Exception
{
    public ManagedBridgeException()
        : this("The managed native bridge operation failed.")
    {
    }

    public ManagedBridgeException(string message)
        : base(message)
    {
    }

    public ManagedBridgeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal ManagedBridgeException(
        DiagnosticCategory category,
        ulong? operationId,
        string message,
        Exception? innerException = null,
        int? nativeErrorCode = null)
        : base(message, innerException)
    {
        Category = category;
        OperationId = operationId;
        NativeErrorCode = nativeErrorCode;
    }

    public DiagnosticCategory Category { get; } = DiagnosticCategory.NativeContractViolation;
    public ulong? OperationId { get; }
    internal int? NativeErrorCode { get; }
}
