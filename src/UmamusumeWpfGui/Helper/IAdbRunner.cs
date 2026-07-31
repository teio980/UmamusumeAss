namespace UmamusumeWpfGui.Helper;

public sealed record AdbCommandResult(
    string Stdout,
    string Stderr,
    int ExitCode,
    bool TimedOut,
    Exception? Error);





public sealed record AdbBinaryCommandResult(
    byte[] Stdout,
    string Stderr,
    int ExitCode,
    bool TimedOut,
    Exception? Error);





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




public interface IAdbRunner
{

    AdbCommandResult Run(string adbPath, IReadOnlyList<string> arguments) =>
        arguments.Count == 1 && arguments[0] == "devices"
            ? FromLegacyResult(RunDevices(adbPath))
            : throw new NotSupportedException("This ADB runner supports only 'adb devices'.");

    Task<AdbCommandResult> RunAsync(
        string adbPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Run(adbPath, arguments), cancellationToken);





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





    (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error) RunDevices(string adbPath);

    private static AdbCommandResult FromLegacyResult(
        (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error) result) =>
        new(result.Stdout, result.Stderr, result.ExitCode, result.TimedOut, result.Error);
}
