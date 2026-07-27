namespace Umamusume.CoreBridge;

public enum DiagnosticCategory
{
    NativeContractViolation,
    MalformedCallback,
    UnknownEvent,
    LateEvent,
    CancellationFailure,
    DispatcherFailure,
    FatalShutdownTimeout,
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record BridgeDiagnostic(
    DiagnosticCategory Category,
    DiagnosticSeverity Severity,
    ulong? OperationId,
    string Message);
