using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Umamusume.AppCore.Services;

public sealed record AdbRunResult(
    string Stdout,
    string Stderr,
    int ExitCode,
    bool TimedOut,
    Exception? Error)
{
    public bool Success => ExitCode == 0 && !TimedOut && Error is null;
}

public sealed record AdbDeviceInfo(string Serial, string State);

public interface IAdbRunner
{
    Task<AdbRunResult> RunAsync(
        string adbPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdbDeviceInfo>> GetDevicesAsync(
        string adbPath,
        CancellationToken cancellationToken = default);

    Task<AdbRunResult> ConnectAsync(
        string adbPath,
        string endpoint,
        CancellationToken cancellationToken = default);
}

public sealed class AdbRunner : IAdbRunner
{
    private readonly TimeSpan _timeout;

    public AdbRunner(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    public async Task<AdbRunResult> RunAsync(
        string adbPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var process = new Process();
            process.StartInfo.FileName = adbPath;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            foreach (var arg in arguments)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            if (!process.Start())
            {
                return new AdbRunResult(
                    "",
                    $"Failed to start process: {adbPath}",
                    -1,
                    false,
                    new InvalidOperationException($"Could not start {adbPath}"));
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_timeout);

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryTerminate(process);
                return new AdbRunResult("", "Operation canceled", -1, false, new OperationCanceledException(cancellationToken));
            }
            catch (OperationCanceledException)
            {
                TryTerminate(process);
                return new AdbRunResult("", "Process execution timed out", -1, true, new TimeoutException($"Process timed out after {_timeout}"));
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            return new AdbRunResult(stdout, stderr, process.ExitCode, false, null);
        }
        catch (Exception ex)
        {
            return new AdbRunResult("", ex.Message, -1, false, ex);
        }
    }

    public async Task<IReadOnlyList<AdbDeviceInfo>> GetDevicesAsync(
        string adbPath,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(adbPath, ["devices"], cancellationToken).ConfigureAwait(false);
        if (!result.Success) return [];

        var list = new List<AdbDeviceInfo>();
        using var reader = new StringReader(result.Stdout);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                list.Add(new AdbDeviceInfo(parts[0], parts[1]));
            }
        }
        return list;
    }

    public Task<AdbRunResult> ConnectAsync(
        string adbPath,
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(adbPath, ["connect", endpoint], cancellationToken);
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore termination failures
        }
    }
}
