namespace UmamusumeWpfGui.Helper;

public sealed record AdbCommandResult(
    string Stdout,
    string Stderr,
    int ExitCode,
    bool TimedOut,
    Exception? Error);

/// <summary>
/// Result for commands whose stdout is binary data, such as
/// <c>adb exec-out screencap -p</c>.
/// </summary>
public sealed record AdbBinaryCommandResult(
    byte[] Stdout,
    string Stderr,
    int ExitCode,
    bool TimedOut,
    Exception? Error);

/// <summary>
/// A long-lived ADB shell used by interactive protocols such as minitouch
/// and MaaTouch. It is deliberately separate from one-shot command results.
/// </summary>
public interface IAdbInteractiveSession : IAsyncDisposable
{
    bool HasExited { get; }

    Task<bool> WriteAsync(
        string data,
        CancellationToken cancellationToken = default);

    Task<string> ReadAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record AdbInteractiveSessionStartResult(
    IAdbInteractiveSession? Session,
    Exception? Error)
{
    public bool Succeeded => Session is not null && Error is null;
}

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
    /// Runs an ADB command while preserving stdout as bytes. Implementations
    /// that only support text commands can keep the default unsupported result.
    /// </summary>
    Task<AdbBinaryCommandResult> RunBinaryAsync(
        string adbPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AdbBinaryCommandResult(
            [],
            "This ADB runner does not support binary output.",
            -1,
            false,
            new NotSupportedException("Binary ADB output is not supported.")));

    Task<AdbInteractiveSessionStartResult> StartInteractiveAsync(
        string adbPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AdbInteractiveSessionStartResult(
            null,
            new NotSupportedException("Interactive ADB sessions are not supported.")));

    /// <summary>
    /// Runs <c>adb devices</c> with the given ADB executable path.
    /// Returns the raw stdout, stderr, exit code, timeout flag, and any exception.
    /// </summary>
    (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error) RunDevices(string adbPath);

    private static AdbCommandResult FromLegacyResult(
        (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error) result) =>
        new(result.Stdout, result.Stderr, result.ExitCode, result.TimedOut, result.Error);
}
