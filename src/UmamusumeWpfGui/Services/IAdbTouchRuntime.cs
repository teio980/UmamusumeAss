using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;






public interface IAdbTouchSession : IAsyncDisposable
{
    AdbTouchProperties Properties { get; }

    Task<AdbTouchOperationResult> TapAsync(
        int x,
        int y,
        CancellationToken cancellationToken = default);

    Task<AdbTouchOperationResult> SwipeAsync(
        int startX,
        int startY,
        int endX,
        int endY,
        int durationMilliseconds,
        bool extraSwipe = false,
        CancellationToken cancellationToken = default);

    Task<AdbTouchOperationResult> InjectAsync(
        AdbTouchEvent touchEvent,
        CancellationToken cancellationToken = default);
}

public interface IAdbTouchRuntime
{
    Task<AdbTouchSessionStartResult> StartAsync(
        string adbPath,
        string serial,
        string localBinaryPath,
        string remotePath,
        int screenWidth,
        int screenHeight,
        int orientation = 0,
        bool useMaaTouch = false,
        string? eventId = null,
        CancellationToken cancellationToken = default);
}
