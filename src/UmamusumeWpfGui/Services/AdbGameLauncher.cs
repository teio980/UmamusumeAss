using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// MAA-style game lifecycle implementation:
/// launch the configured package, poll for its process, and force-stop it on
/// request. The generic package launch uses Android's monkey launcher so the
/// UI does not need to hard-code a vendor-specific Activity name.
/// </summary>
public sealed class AdbGameLauncher : IGameLauncher
{
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

    public async Task<GameLaunchResult> StartAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        var start = await _adbRuntime.StartPackageAsync(
            adbPath, serial, packageName, cancellationToken).ConfigureAwait(false);
        if (!IsSuccessful(start))
        {
            return new GameLaunchResult(
                false,
                false,
                "The game launch command failed.",
                start);
        }

        var running = await WaitForRunningAsync(
            adbPath,
            serial,
            packageName,
            TimeSpan.FromSeconds(8),
            TimeSpan.FromMilliseconds(250),
            cancellationToken).ConfigureAwait(false);
        if (running.Value == true)
        {
            return new GameLaunchResult(true, true, "The game process is running.", start);
        }

        // A few Android images return from monkey before the Unity process is
        // visible to pidof. The command itself succeeded, so report a warning
        // state instead of pretending launch failed.
        return new GameLaunchResult(
            true,
            false,
            "The launch command completed, but the game process was not detected yet.",
            start);
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
