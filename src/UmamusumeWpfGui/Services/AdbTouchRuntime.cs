using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;






public sealed class AdbTouchRuntime : IAdbTouchRuntime
{
    private readonly IAdbRunner _adbRunner;

    public AdbTouchRuntime(IAdbRunner adbRunner)
    {
        ArgumentNullException.ThrowIfNull(adbRunner);
        _adbRunner = adbRunner;
    }

    public async Task<AdbTouchSessionStartResult> StartAsync(
        string adbPath,
        string serial,
        string localBinaryPath,
        string remotePath,
        int screenWidth,
        int screenHeight,
        int orientation = 0,
        bool useMaaTouch = false,
        string? eventId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(localBinaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        if (screenWidth <= 0 || screenHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(screenWidth), "Screen dimensions must be positive.");
        }

        var push = await _adbRunner.RunAsync(
            adbPath,
            ["-s", serial, "push", localBinaryPath, remotePath],
            cancellationToken).ConfigureAwait(false);
        if (!IsSuccessful(push))
        {
            return new AdbTouchSessionStartResult(null, push, "Failed to push the touch binary.");
        }

        var chmod = await _adbRunner.RunAsync(
            adbPath,
            ["-s", serial, "shell", "chmod", "700", remotePath],
            cancellationToken).ConfigureAwait(false);
        if (!IsSuccessful(chmod))
        {
            return new AdbTouchSessionStartResult(null, chmod, "Failed to make the touch binary executable.");
        }

        var interactiveArguments = useMaaTouch
            ? CreateMaaTouchArguments(serial, remotePath)
            : CreateMinitouchArguments(serial, remotePath, eventId);
        var started = await _adbRunner.StartInteractiveAsync(
            adbPath, interactiveArguments, cancellationToken).ConfigureAwait(false);
        if (!started.Succeeded || started.Session is null)
        {
            return new AdbTouchSessionStartResult(
                null,
                null,
                started.Error?.Message ?? "Failed to start the touch process.");
        }

        var properties = await ReadPropertiesAsync(started.Session, useMaaTouch, cancellationToken)
            .ConfigureAwait(false);
        if (properties is null)
        {
            await started.Session.DisposeAsync().ConfigureAwait(false);
            return new AdbTouchSessionStartResult(
                null,
                null,
                "Touch process started but did not publish protocol properties.");
        }

        var effectiveProperties = properties with
        {
            Orientation = NormalizeOrientation(orientation),
            UsesMaaTouch = useMaaTouch,
            ScreenWidth = screenWidth,
            ScreenHeight = screenHeight
        };
        return new AdbTouchSessionStartResult(
            new AdbTouchSession(started.Session, effectiveProperties),
            null,
            null);
    }

    private static async Task<AdbTouchProperties?> ReadPropertiesAsync(
        IAdbInteractiveSession session,
        bool usesMaaTouch,
        CancellationToken cancellationToken)
    {
        var output = string.Empty;
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            output += await session.ReadAsync(TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(false);
            var match = Regex.Match(
                output.Replace("\r\n", "\n", StringComparison.Ordinal),
                @"\^\s*(?<contacts>\d+)\s+(?<width>\d+)\s+(?<height>\d+)\s+(?<pressure>\d+)");
            if (!match.Success)
            {
                continue;
            }

            return new AdbTouchProperties(
                int.Parse(match.Groups["contacts"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["height"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["pressure"].Value, CultureInfo.InvariantCulture),
                0,
                usesMaaTouch);
        }

        return null;
    }

    private static List<string> CreateMaaTouchArguments(string serial, string remotePath) =>
    [
        "-s",
        serial,
        "shell",
        "sh",
        "-c",
        $"export CLASSPATH={remotePath}; exec app_process /data/local/tmp com.shxyke.MaaTouch.App"
    ];

    private static List<string> CreateMinitouchArguments(
        string serial,
        string remotePath,
        string? eventId)
    {
        var arguments = new List<string> { "-s", serial, "shell", remotePath, "-i" };
        if (!string.IsNullOrWhiteSpace(eventId))
        {
            arguments.Add("-d");
            arguments.Add($"/dev/input/event{eventId}");
        }

        return arguments;
    }

    private static int NormalizeOrientation(int orientation) =>
        ((orientation % 4) + 4) % 4;

    private static bool IsSuccessful(AdbCommandResult result) =>
        result.Error is null && !result.TimedOut && result.ExitCode == 0;
}

internal sealed class AdbTouchSession : IAdbTouchSession
{
    private static readonly TimeSpan DefaultMoveInterval = TimeSpan.FromMilliseconds(2);

    private readonly IAdbInteractiveSession _session;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public AdbTouchSession(
        IAdbInteractiveSession session,
        AdbTouchProperties properties)
    {
        _session = session;
        Properties = properties;
    }

    public AdbTouchProperties Properties { get; }

    public async Task<AdbTouchOperationResult> TapAsync(
        int x,
        int y,
        CancellationToken cancellationToken = default)
    {
        var scaled = Scale(x, y);
        return await SendAsync(
            $"d 0 {scaled.X} {scaled.Y} {Properties.MaxPressure}\nc\nw 50\nu 0\nc\nw 50\n",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AdbTouchOperationResult> SwipeAsync(
        int startX,
        int startY,
        int endX,
        int endY,
        int durationMilliseconds,
        bool extraSwipe = false,
        CancellationToken cancellationToken = default)
    {
        var start = Scale(startX, startY);
        var end = Scale(endX, endY);
        var duration = durationMilliseconds > 0 ? durationMilliseconds : 200;
        var steps = Math.Max(1, (int)Math.Ceiling(duration / DefaultMoveInterval.TotalMilliseconds));
        var commands = $"d 0 {start.X} {start.Y} {Properties.MaxPressure}\nc\nw 50\n";
        for (var step = 1; step <= steps; step++)
        {
            var ratio = step / (double)steps;
            var x = (int)Math.Round(start.X + ((end.X - start.X) * ratio));
            var y = (int)Math.Round(start.Y + ((end.Y - start.Y) * ratio));
            commands += $"m 0 {x} {y} {Properties.MaxPressure}\nc\nw 2\n";
        }

        if (extraSwipe)
        {
            commands += "w 150\n";
            var extraEnd = Scale(endX, endY - 100);
            commands += $"m 0 {extraEnd.X} {extraEnd.Y} {Properties.MaxPressure}\nc\nw 500\n";
        }

        commands += "u 0\nc\nw 50\n";
        return await SendAsync(commands, cancellationToken).ConfigureAwait(false);
    }

    public Task<AdbTouchOperationResult> InjectAsync(
        AdbTouchEvent touchEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(touchEvent);
        var command = touchEvent.Type switch
        {
            AdbTouchEventType.KeyDown => $"k {touchEvent.KeyCode} d\n",
            AdbTouchEventType.KeyUp => $"k {touchEvent.KeyCode} u\n",
            AdbTouchEventType.TouchDown => CreateDownCommand(touchEvent),
            AdbTouchEventType.TouchMove => CreateMoveCommand(touchEvent),
            AdbTouchEventType.TouchUp => $"u {touchEvent.PointerId}\n",
            AdbTouchEventType.TouchReset => "r\n",
            AdbTouchEventType.Wait => $"w {Math.Max(0, touchEvent.Milliseconds)}\n",
            AdbTouchEventType.Commit => "c\n",
            _ => throw new ArgumentOutOfRangeException(nameof(touchEvent), "Unknown touch event type.")
        };
        return SendAsync(command, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }
        finally
        {
            _writeLock.Release();
        }

        await _session.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    private async Task<AdbTouchOperationResult> SendAsync(
        string commands,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || _session.HasExited)
            {
                return AdbTouchOperationResult.Failure("Touch session is not running.");
            }

            return await _session.WriteAsync(commands, cancellationToken).ConfigureAwait(false)
                ? AdbTouchOperationResult.Success
                : AdbTouchOperationResult.Failure("Touch session rejected the command.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private string CreateDownCommand(AdbTouchEvent touchEvent)
    {
        var scaled = Scale(touchEvent.X, touchEvent.Y);
        return $"d {touchEvent.PointerId} {scaled.X} {scaled.Y} {Properties.MaxPressure}\n";
    }

    private string CreateMoveCommand(AdbTouchEvent touchEvent)
    {
        var scaled = Scale(touchEvent.X, touchEvent.Y);
        return $"m {touchEvent.PointerId} {scaled.X} {scaled.Y} {Properties.MaxPressure}\n";
    }

    private (int X, int Y) Scale(int x, int y)
    {
        var scaledX = (int)Math.Round(
            x * (double)Properties.MaxX / Math.Max(1, Properties.ScreenWidth));
        var scaledY = (int)Math.Round(
            y * (double)Properties.MaxY / Math.Max(1, Properties.ScreenHeight));


        scaledX = Math.Clamp(scaledX, 0, Properties.MaxX);
        scaledY = Math.Clamp(scaledY, 0, Properties.MaxY);
        return Properties.Orientation switch
        {
            1 => (Properties.MaxY - scaledY, scaledX),
            2 => (Properties.MaxX - scaledX, Properties.MaxY - scaledY),
            3 => (scaledY, Properties.MaxX - scaledX),
            _ => (scaledX, scaledY)
        };
    }
}
