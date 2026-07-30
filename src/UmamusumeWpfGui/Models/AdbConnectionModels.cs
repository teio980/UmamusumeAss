using UmamusumeWpfGui.Helper;

namespace UmamusumeWpfGui.Models;

public sealed record AdbConnectionOptions(
    string AdbPath,
    string Serial,
    TimeSpan ReadyTimeout,
    TimeSpan PollInterval)
{
    public static AdbConnectionOptions Create(
        string adbPath,
        string serial,
        TimeSpan? readyTimeout = null,
        TimeSpan? pollInterval = null) =>
        new(
            adbPath,
            serial,
            readyTimeout ?? TimeSpan.FromSeconds(30),
            pollInterval ?? TimeSpan.FromMilliseconds(250));
}

public sealed record AdbConnectionSessionStartResult(
    Services.IAdbConnectionSession? Session,
    IReadOnlyList<AdbCommandResult> CommandResults,
    string? Error)
{
    public bool Succeeded => Session is not null && string.IsNullOrWhiteSpace(Error);
}
