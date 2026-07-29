namespace Umamusume.CoreBridge;

public enum ConnectionPhase
{
    AdbDevices,
    AdbGetState,
    AdbConnect,
    ReadyPoll,
    BootPoll,
    AndroidId,
    AndroidVersion,
    WmSize,
}

public enum DisplaySizeSource
{
    Physical,
    Override,
}

public abstract record ConnectionEvent(ulong OperationId);

public sealed record ConnectionStartedEvent(ulong OperationId) : ConnectionEvent(OperationId);

public sealed record ConnectionProgressEvent(ulong OperationId, ConnectionPhase Phase)
    : ConnectionEvent(OperationId);

public abstract record ConnectionTerminalEvent(ulong OperationId) : ConnectionEvent(OperationId);

public sealed record ConnectionSucceededEvent(
    ulong OperationId,
    string Serial,
    string AndroidId,
    string AndroidVersion,
    int Width,
    int Height,
    int PhysicalWidth,
    int PhysicalHeight,
    DisplaySizeSource SizeSource)
    : ConnectionTerminalEvent(OperationId);

public sealed record ConnectionFailedEvent(
    ulong OperationId,
    ConnectionErrorCode ErrorCode,
    string Phase,
    string Message,
    int Attempt,
    int MaxAttempts)
    : ConnectionTerminalEvent(OperationId);
