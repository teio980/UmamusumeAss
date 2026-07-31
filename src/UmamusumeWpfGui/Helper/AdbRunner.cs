using System.Diagnostics;
using System.IO;

namespace UmamusumeWpfGui.Helper;






public sealed class AdbRunner : IAdbRunner
{
    private readonly TimeSpan _timeout;




    public AdbRunner()
        : this(TimeSpan.FromSeconds(15))
    {
    }




    public AdbRunner(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    public (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error) RunDevices(string adbPath)
    {
        var result = Run(adbPath, ["devices"]);
        return (result.Stdout, result.Stderr, result.ExitCode, result.TimedOut, result.Error);
    }

    public async Task<AdbCommandResult> RunAsync(
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
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                return new AdbCommandResult(
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
                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    throw new OperationCanceledException(
                        "ADB output could not be drained after cancellation.",
                        exception,
                        cancellationToken);
                }
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var terminationError = TryTerminate(process);
                var output = await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                return new AdbCommandResult(
                    output[0].TrimEnd(),
                    output[1].TrimEnd(),
                    -1,
                    true,
                    terminationError);
            }

            var completedOutput = await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new AdbCommandResult(
                completedOutput[0].TrimEnd(),
                completedOutput[1].TrimEnd(),
                process.ExitCode,
                false,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new AdbCommandResult(
                "",
                $"Exception running ADB: {exception.Message}",
                -1,
                false,
                exception);
        }
    }

    public async Task<AdbBinaryCommandResult> RunBinaryAsync(
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
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                return new AdbBinaryCommandResult(
                    [],
                    $"Failed to start process: {adbPath}",
                    -1,
                    false,
                    new InvalidOperationException($"Could not start {adbPath}"));
            }

            using var stdout = new MemoryStream();
            var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout, CancellationToken.None);
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
                await DrainBinaryOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var terminationError = TryTerminate(process);
                await DrainBinaryOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                return new AdbBinaryCommandResult(
                    stdout.ToArray(),
                    (await stderrTask.ConfigureAwait(false)).TrimEnd(),
                    -1,
                    true,
                    terminationError);
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new AdbBinaryCommandResult(
                stdout.ToArray(),
                (await stderrTask.ConfigureAwait(false)).TrimEnd(),
                process.ExitCode,
                false,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new AdbBinaryCommandResult(
                [],
                $"Exception running ADB: {exception.Message}",
                -1,
                false,
                exception);
        }
    }

    public async Task<AdbInteractiveSessionStartResult> StartInteractiveAsync(
        string adbPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var process = new Process();
            process.StartInfo.FileName = adbPath;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                process.Dispose();
                return new AdbInteractiveSessionStartResult(
                    null,
                    new InvalidOperationException($"Could not start {adbPath}"));
            }

            var session = new AdbInteractiveProcessSession(process);
            if (session.HasExited)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return new AdbInteractiveSessionStartResult(
                    null,
                    new InvalidOperationException("Interactive ADB process exited immediately."));
            }

            return new AdbInteractiveSessionStartResult(session, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new AdbInteractiveSessionStartResult(null, exception);
        }
    }

    public AdbCommandResult Run(string adbPath, IReadOnlyList<string> arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = adbPath;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

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
                return new AdbCommandResult(
                    "",
                    $"Failed to start process: {adbPath}",
                    -1,
                    false,
                    new InvalidOperationException($"Could not start {adbPath}"));
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var completed = process.WaitForExit((int)_timeout.TotalMilliseconds);

            if (!completed)
            {
                Exception? terminationException = null;
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException exception)
                {
                    terminationException = exception;
                }

                return new AdbCommandResult("", "", -1, true, terminationException);
            }


            stdoutWaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            stderrWaitHandle.WaitOne(TimeSpan.FromSeconds(5));

            var stdout = stdoutBuilder.ToString().TrimEnd();
            var stderr = stderrBuilder.ToString().TrimEnd();
            return new AdbCommandResult(stdout, stderr, process.ExitCode, false, null);
        }
        catch (Exception ex)
        {
            return new AdbCommandResult("", $"Exception running ADB: {ex.Message}", -1, false, ex);
        }
    }

    private static Exception? TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            return null;
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return exception;
        }
    }

    private static async Task DrainBinaryOutputAsync(
        Task stdoutTask,
        Task<string> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch
        {



        }
    }

    private sealed class AdbInteractiveProcessSession : IAdbInteractiveSession
    {
        private readonly Process _process;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _ioLock = new(1, 1);
        private readonly Task<string> _stderrDrain;
        private bool _disposed;

        public AdbInteractiveProcessSession(Process process)
        {
            _process = process;
            _reader = process.StandardOutput;
            _writer = process.StandardInput;
            _writer.AutoFlush = true;
            _stderrDrain = process.StandardError.ReadToEndAsync(CancellationToken.None);
        }

        public bool HasExited => _process.HasExited;

        public async Task<bool> WriteAsync(
            string data,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(data);
            await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed || _process.HasExited)
                {
                    return false;
                }

                await _writer.WriteAsync(data.AsMemory(), cancellationToken).ConfigureAwait(false);
                await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            finally
            {
                _ioLock.Release();
            }
        }

        public async Task<string> ReadAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var safeTimeout = timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout;
            await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed || _process.HasExited)
                {
                    return string.Empty;
                }

                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(safeTimeout);
                var buffer = new char[4096];
                try
                {
                    var count = await _reader.ReadAsync(buffer.AsMemory(), timeoutSource.Token)
                        .ConfigureAwait(false);
                    return count == 0 ? string.Empty : new string(buffer, 0, count);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return string.Empty;
                }
                catch (IOException)
                {
                    return string.Empty;
                }
            }
            finally
            {
                _ioLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _writer.Dispose();
            }
            catch (ObjectDisposedException)
            {

            }

            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {

            }
            catch (System.ComponentModel.Win32Exception)
            {

            }

            try
            {
                await _stderrDrain.ConfigureAwait(false);
            }
            catch
            {

            }

            _reader.Dispose();
            _ioLock.Dispose();
            _process.Dispose();
        }
    }
}
