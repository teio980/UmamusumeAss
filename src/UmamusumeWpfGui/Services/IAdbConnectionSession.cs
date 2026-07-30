using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// MAA-style device connection context. It owns the identity and negotiated
/// device properties while IAdbRuntime remains the reusable command facade.
/// </summary>
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

    /// <summary>
    /// Runs a device command and applies MAA's bounded reconnect policy when
    /// the command fails because the ADB transport disappeared.
    /// </summary>
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
