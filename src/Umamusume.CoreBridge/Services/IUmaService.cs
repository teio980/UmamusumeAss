namespace Umamusume.CoreBridge;

public interface IUmaService : IAsyncDisposable
{
    string? CoreVersion { get; }
    /// <summary>Loaded application resource directory, or <see langword="null"/> before initialization.</summary>
    string? ResourcePath => null;
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
