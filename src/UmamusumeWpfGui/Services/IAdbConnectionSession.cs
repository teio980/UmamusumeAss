using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;





public interface IAdbConnectionSession : IAsyncDisposable
{
    string AdbPath { get; }
    string Serial { get; }
    AdbDeviceProperties Properties { get; }
    bool IsConnected { get; }

    Task<AdbRuntimeQueryResult<AdbDeviceProperties>> RefreshPropertiesAsync(
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> DisconnectAsync(
        CancellationToken cancellationToken = default);





    Task<AdbCommandResult> ExecuteWithReconnectAsync(
        Func<CancellationToken, Task<AdbCommandResult>> command,
        int maxReconnectAttempts = 5,
        TimeSpan? reconnectDelay = null,
        CancellationToken cancellationToken = default);
}

public interface IAdbConnectionSessionFactory
{
    Task<AdbConnectionSessionStartResult> ConnectAsync(
        AdbConnectionOptions options,
        CancellationToken cancellationToken = default);
}
