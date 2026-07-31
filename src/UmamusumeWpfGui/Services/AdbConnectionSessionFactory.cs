using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

public sealed class AdbConnectionSessionFactory : IAdbConnectionSessionFactory
{
    private readonly IAdbRuntime _adbRuntime;

    public AdbConnectionSessionFactory(IAdbRuntime adbRuntime)
    {
        ArgumentNullException.ThrowIfNull(adbRuntime);
        _adbRuntime = adbRuntime;
    }

    public async Task<AdbConnectionSessionStartResult> ConnectAsync(
        AdbConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var commandResults = new List<AdbCommandResult>();

        var listed = await _adbRuntime.ListDevicesAsync(
            options.AdbPath, cancellationToken).ConfigureAwait(false);
        commandResults.Add(listed.CommandResult);
        if (!listed.Succeeded)
        {
            return Failure(commandResults, "ADB devices query failed.");
        }

        var ready = listed.Devices.Any(device =>
            device.Serial.Equals(options.Serial, StringComparison.OrdinalIgnoreCase)
            && device.IsReady);
        if (!ready)
        {
            if (!options.Serial.Contains(':', StringComparison.Ordinal))
            {
                return Failure(
                    commandResults,
                    "The serial device is not listed as ready and does not look like a TCP endpoint.");
            }

            var connect = await _adbRuntime.ConnectAsync(
                options.AdbPath, options.Serial, cancellationToken).ConfigureAwait(false);
            commandResults.Add(connect);
            if (!IsSuccessful(connect)
                || !ReportsConnected($"{connect.Stdout}\n{connect.Stderr}"))
            {
                return Failure(commandResults, "ADB connect did not report a successful connection.");
            }
        }

        var waited = await _adbRuntime.WaitForDeviceAsync(
            options.AdbPath,
            options.Serial,
            options.ReadyTimeout,
            options.PollInterval,
            cancellationToken).ConfigureAwait(false);
        commandResults.Add(waited.CommandResult);
        if (!waited.Succeeded || !waited.Devices.Any(device =>
                device.Serial.Equals(options.Serial, StringComparison.OrdinalIgnoreCase)
                && device.IsReady))
        {
            return Failure(commandResults, "ADB device did not become ready within the connection timeout.");
        }

        var properties = await _adbRuntime.GetDevicePropertiesAsync(
            options.AdbPath, options.Serial, cancellationToken).ConfigureAwait(false);
        commandResults.AddRange(properties.CommandResults);
        if (!properties.Succeeded || properties.Value is null)
        {
            return Failure(commandResults, "Failed to read the Android device properties.");
        }

        if (string.IsNullOrWhiteSpace(properties.Value.AndroidId)
            || string.IsNullOrWhiteSpace(properties.Value.AndroidVersion)
            || properties.Value.ScreenSize is null)
        {
            return Failure(commandResults, "Android device properties were incomplete.");
        }

        return new AdbConnectionSessionStartResult(
            new AdbConnectionSession(_adbRuntime, options, properties.Value),
            commandResults,
            null);
    }

    private static AdbConnectionSessionStartResult Failure(
        IReadOnlyList<AdbCommandResult> commandResults,
        string message) =>
        new(null, commandResults, message);

    private static bool ReportsConnected(string output) =>
        output.Contains("connected", StringComparison.OrdinalIgnoreCase)
        || output.Contains("already connected", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuccessful(AdbCommandResult result) =>
        result.Error is null && !result.TimedOut && result.ExitCode == 0;
}

public sealed class AdbConnectionSession : IAdbConnectionSession
{
    private readonly IAdbRuntime _adbRuntime;
    private readonly AdbConnectionOptions _options;
    private bool _disposed;

    internal AdbConnectionSession(
        IAdbRuntime adbRuntime,
        AdbConnectionOptions options,
        AdbDeviceProperties properties)
    {
        _adbRuntime = adbRuntime;
        _options = options;
        AdbPath = options.AdbPath;
        Serial = options.Serial;
        Properties = properties;
        IsConnected = true;
    }

    public string AdbPath { get; }
    public string Serial { get; }
    public AdbDeviceProperties Properties { get; private set; }
    public bool IsConnected { get; private set; }

    public async Task<AdbRuntimeQueryResult<AdbDeviceProperties>> RefreshPropertiesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _adbRuntime.GetDevicePropertiesAsync(
            AdbPath, Serial, cancellationToken).ConfigureAwait(false);
        if (result.Value is not null)
        {
            Properties = result.Value;
        }

        return result;
    }

    public async Task<AdbCommandResult> DisconnectAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return new AdbCommandResult("", "", 0, false, null);
        }

        var result = await _adbRuntime.DisconnectAsync(
            AdbPath, Serial, cancellationToken).ConfigureAwait(false);
        if (result.Error is null && !result.TimedOut && result.ExitCode == 0)
        {
            IsConnected = false;
        }

        return result;
    }

    public async Task<AdbCommandResult> ExecuteWithReconnectAsync(
        Func<CancellationToken, Task<AdbCommandResult>> command,
        int maxReconnectAttempts = 5,
        TimeSpan? reconnectDelay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var attempts = Math.Max(0, maxReconnectAttempts);
        var delay = reconnectDelay ?? TimeSpan.FromSeconds(10);
        var result = await command(cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; !IsSuccessful(result) && attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            if (_options.Serial.Contains(':', StringComparison.Ordinal))
            {
                var reconnect = await _adbRuntime.ConnectAsync(
                    AdbPath, Serial, cancellationToken).ConfigureAwait(false);
                if (!IsSuccessful(reconnect))
                {
                    continue;
                }
            }

            var ready = await _adbRuntime.WaitForDeviceAsync(
                AdbPath,
                Serial,
                _options.ReadyTimeout,
                _options.PollInterval,
                cancellationToken).ConfigureAwait(false);
            if (!ready.Succeeded || !ready.Devices.Any(device =>
                    device.Serial.Equals(Serial, StringComparison.OrdinalIgnoreCase)
                    && device.IsReady))
            {
                continue;
            }

            result = await command(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;



        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static bool IsSuccessful(AdbCommandResult result) =>
        result.Error is null && !result.TimedOut && result.ExitCode == 0;
}
