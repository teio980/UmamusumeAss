namespace UmamusumeWpfGui.Helper;

/// <summary>
/// Abstraction over executing <c>adb devices</c>, allowing the parser
/// to be tested without a real ADB installation.
/// </summary>
public interface IAdbRunner
{
    /// <summary>
    /// Runs <c>adb devices</c> with the given ADB executable path.
    /// Returns the raw stdout, stderr, exit code, timeout flag, and any exception.
    /// </summary>
    (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error) RunDevices(string adbPath);
}
