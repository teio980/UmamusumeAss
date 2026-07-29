namespace UmamusumeWpfGui.Helper;

public sealed record AdbCommandResult(
    string Stdout,
    string Stderr,
    int ExitCode,
    bool TimedOut,
    Exception? Error);

/// <summary>
/// Abstraction over executing bounded ADB commands without a real ADB installation.
/// </summary>
public interface IAdbRunner
{
    /// <summary>Runs an ADB command with individual argument tokens.</summary>
    AdbCommandResult Run(string adbPath, IReadOnlyList<string> arguments) =>
        arguments.Count == 1 && arguments[0] == "devices"
            ? FromLegacyResult(RunDevices(adbPath))
            : throw new NotSupportedException("This ADB runner supports only 'adb devices'.");

    Task<AdbCommandResult> RunAsync(
        string adbPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Run(adbPath, arguments), cancellationToken);

    /// <summary>
    /// Runs <c>adb devices</c> with the given ADB executable path.
    /// Returns the raw stdout, stderr, exit code, timeout flag, and any exception.
    /// </summary>
    (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error) RunDevices(string adbPath);

    private static AdbCommandResult FromLegacyResult(
        (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error) result) =>
        new(result.Stdout, result.Stderr, result.ExitCode, result.TimedOut, result.Error);
}
