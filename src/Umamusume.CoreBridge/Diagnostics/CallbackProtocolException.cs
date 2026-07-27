namespace Umamusume.CoreBridge;

internal sealed class CallbackProtocolException(
    DiagnosticCategory category,
    ulong? operationId,
    string message,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    internal DiagnosticCategory Category { get; } = category;
    internal ulong? OperationId { get; } = operationId;
}
