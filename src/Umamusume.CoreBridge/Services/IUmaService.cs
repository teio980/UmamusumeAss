namespace Umamusume.CoreBridge;

public interface IUmaService : IAsyncDisposable
{
    string? CoreVersion { get; }
    event Action<ConnectionEvent>? ConnectionEventReceived;
    event Action<BridgeDiagnostic>? DiagnosticReceived;
    Task InitializeAsync(
        string appBaseDir,
        string appDataDir,
        CancellationToken cancellationToken = default);
    Task<ConnectionTerminalEvent> ConnectAsync(
        string adbPath,
        string serial,
        string profile,
        CancellationToken cancellationToken = default);
    Task CancelOperationAsync(ulong operationId, CancellationToken cancellationToken = default);
}
