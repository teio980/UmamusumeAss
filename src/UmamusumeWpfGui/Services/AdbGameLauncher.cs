using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// MAA-style game lifecycle implementation:
/// start the configured component, retry the launch while the client is not
/// visible, and force-stop it on request. The retry window mirrors MAA's
/// StartGameTaskPlugin (30 attempts with a 1.5 second interval).
/// </summary>
public sealed class AdbGameLauncher : IGameLauncher
{
    private const int MaxStartAttempts = 30;
    private static readonly TimeSpan StartRetryInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan StartProbeWindow = TimeSpan.FromMilliseconds(250);

    private readonly IAdbRuntime _adbRuntime;
    private readonly IAsyncDelay _asyncDelay;

    public AdbGameLauncher(
        IAdbRuntime adbRuntime,
        IAsyncDelay asyncDelay)
    {
        ArgumentNullException.ThrowIfNull(adbRuntime);
        ArgumentNullException.ThrowIfNull(asyncDelay);
        _adbRuntime = adbRuntime;
        _asyncDelay = asyncDelay;
    }

    public Task<GameLaunchResult> StartAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default) =>
        StartAsync(adbPath, serial, packageName, null, cancellationToken);

    public async Task<GameLaunchResult> StartAsync(
        string adbPath,
        string serial,
        string packageName,
        string? activityName,
        CancellationToken cancellationToken = default)
    {
        var normalizedActivity = string.IsNullOrWhiteSpace(activityName)
            ? null
            : activityName.Trim();
        AdbCommandResult? lastStart = null;

        for (var attempt = 1; attempt <= MaxStartAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastStart = normalizedActivity is null
                ? await _adbRuntime.StartPackageAsync(
                    adbPath, serial, packageName, cancellationToken).ConfigureAwait(false)
                : await _adbRuntime.StartActivityAsync(
                    adbPath,
                    serial,
                    packageName,
                    normalizedActivity,
                    cancellationToken).ConfigureAwait(false);

            if (IsSuccessful(lastStart))
            {
                var running = await WaitForRunningAsync(
                    adbPath,
                    serial,
                    packageName,
                    StartProbeWindow,
                    TimeSpan.FromMilliseconds(250),
                    cancellationToken).ConfigureAwait(false);
                if (running.Value == true)
                {
                    return new GameLaunchResult(
                        true,
                        true,
                        "The game process is running.",
                        lastStart);
                }
            }

            if (attempt < MaxStartAttempts)
            {
                await _asyncDelay.DelayAsync(
                    StartRetryInterval,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // Preserve the last ADB result so the task log can still show the
        // concrete failure after the bounded retry window.
        var finalResult = lastStart!;
        return new GameLaunchResult(
            IsSuccessful(finalResult),
            false,
            IsSuccessful(finalResult)
                ? "The launch command completed, but the game process was not detected."
                : "The game launch command failed after the retry window.",
            finalResult);
    }

    public async Task<GameLaunchResult> StopAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        var stop = await _adbRuntime.StopPackageAsync(
            adbPath, serial, packageName, cancellationToken).ConfigureAwait(false);
        return new GameLaunchResult(
            IsSuccessful(stop),
            false,
            IsSuccessful(stop)
                ? "The game was stopped."
                : "The game stop command failed.",
            stop);
    }

    private async Task<AdbRuntimeQueryResult<bool>> WaitForRunningAsync(
        string adbPath,
        string serial,
        string packageName,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        AdbRuntimeQueryResult<bool>? latest = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            latest = await _adbRuntime.IsPackageRunningAsync(
                adbPath, serial, packageName, cancellationToken).ConfigureAwait(false);
            if (latest.Value == true
                || System.Diagnostics.Stopwatch.GetElapsedTime(started) >= timeout)
            {
                return latest;
            }

            await _asyncDelay.DelayAsync(pollInterval, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool IsSuccessful(AdbCommandResult result) =>
        result.Error is null && !result.TimedOut && result.ExitCode == 0;
}
