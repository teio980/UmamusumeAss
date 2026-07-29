using Umamusume.CoreBridge;

namespace UmamusumeWpfGui.Services;

public sealed record ConnectionHealthTarget(
    string AdbPath,
    string Serial,
    string ProfileName);

public sealed record ConnectionHealthFailure(
    string Serial,
    ConnectionErrorCode ErrorCode,
    string Diagnostic);

public interface IConnectionHealthMonitor : IAsyncDisposable
{
    bool IsRunning { get; }
    event Action<ConnectionHealthFailure>? Failed;
    void Start(ConnectionHealthTarget target);
    Task StopAsync();
}
