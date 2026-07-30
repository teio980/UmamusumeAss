using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Models;

public enum AdbTouchEventType
{
    KeyDown,
    KeyUp,
    TouchDown,
    TouchMove,
    TouchUp,
    TouchReset,
    Wait,
    Commit
}

public sealed record AdbTouchEvent(
    AdbTouchEventType Type,
    int X = 0,
    int Y = 0,
    int PointerId = 0,
    int KeyCode = 0,
    int Milliseconds = 0);

public sealed record AdbTouchProperties(
    int MaxContacts,
    int MaxX,
    int MaxY,
    int MaxPressure,
    int Orientation,
    bool UsesMaaTouch)
{
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }
}

public sealed record AdbTouchOperationResult(
    bool Succeeded,
    string? Error = null)
{
    public static AdbTouchOperationResult Success { get; } = new(true);

    public static AdbTouchOperationResult Failure(string error) =>
        new(false, error);
}

public sealed record AdbTouchSessionStartResult(
    IAdbTouchSession? Session,
    AdbCommandResult? CommandResult,
    string? Error)
{
    public bool Succeeded => Session is not null && string.IsNullOrWhiteSpace(Error);
}
