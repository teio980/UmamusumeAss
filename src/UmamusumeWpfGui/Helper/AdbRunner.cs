using System.Diagnostics;

namespace UmamusumeWpfGui.Helper;

/// <summary>
/// Real implementation of <see cref="IAdbRunner"/> that executes
/// <c>adb devices</c> via <see cref="Process"/> with stdout/stderr capture,
/// a timeout, and basic exception handling.
/// </summary>
public sealed class AdbRunner : IAdbRunner
{
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Creates a runner with the default 15-second timeout for <c>adb devices</c>.
    /// </summary>
    public AdbRunner()
        : this(TimeSpan.FromSeconds(15))
    {
    }

    /// <summary>
    /// Creates a runner with an explicit timeout (testability seam).
    /// </summary>
    public AdbRunner(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    public (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error) RunDevices(
        string adbPath)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = adbPath;
            process.StartInfo.Arguments = "devices";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            var stdoutBuilder = new System.Text.StringBuilder();
            var stderrBuilder = new System.Text.StringBuilder();

            using var stdoutWaitHandle = new ManualResetEvent(false);
            using var stderrWaitHandle = new ManualResetEvent(false);

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null)
                    stdoutWaitHandle.Set();
                else
                    stdoutBuilder.AppendLine(e.Data);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                    stderrWaitHandle.Set();
                else
                    stderrBuilder.AppendLine(e.Data);
            };

            if (!process.Start())
            {
                return ("", $"Failed to start process: {adbPath}", -1, false,
                    new InvalidOperationException($"Could not start {adbPath}"));
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var completed = process.WaitForExit((int)_timeout.TotalMilliseconds);

            if (!completed)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort kill on timeout
                }

                return ("", "", -1, true, null);
            }

            // Ensure async read completion
            stdoutWaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            stderrWaitHandle.WaitOne(TimeSpan.FromSeconds(5));

            var stdout = stdoutBuilder.ToString().TrimEnd();
            var stderr = stderrBuilder.ToString().TrimEnd();
            return (stdout, stderr, process.ExitCode, false, null);
        }
        catch (Exception ex)
        {
            return ("", $"Exception running 'adb devices': {ex.Message}", -1, false, ex);
        }
    }
}
