namespace Umamusume.CoreBridge;

public enum ConnectionPhase
{
    AdbDevices,
    AdbGetState,
    AdbConnect,
    ReadyPoll,
    BootPoll,
    AndroidId,
    AndroidVersion,
    WmSize,
}

public enum DisplaySizeSource
{
    Physical,
    Override,
}

public enum ScreenshotCaptureMethod
{
    Auto,
    Raw,
    Gzip,
    Png,
    ShellPng,
}

public enum TouchInputBackend
{
    Adb,
    Minitouch,
    MaaTouch,
}

public enum PackageOperationKind
{
    StartPackage = 0,
    StartActivity = 1,
    StopPackage = 2,
    QueryRunning = 3,
}

public sealed record DeviceInstanceOptions(
    int? DisplayId = null,
    string? SurfaceDisplayId = null,
    ScreenshotCaptureMethod ScreenshotMethod = ScreenshotCaptureMethod.Auto,
    double SwipeDurationMultiplier = 10.0,
    int ExtraSwipeDistance = 100,
    int ExtraSwipeDurationMilliseconds = 500,
    TouchInputBackend TouchBackend = TouchInputBackend.Adb,
    string? TouchBinaryPath = null,
    string TouchRemotePath = "/data/local/tmp/uma-touch",
    string? TouchEventId = null);

public abstract record ConnectionEvent;

public sealed record ConnectionStartedEvent(ulong OperationId) : ConnectionEvent;

public sealed record ConnectionProgressEvent(ulong OperationId, ConnectionPhase Phase)
    : ConnectionEvent;

public abstract record ConnectionTerminalEvent(ulong OperationId) : ConnectionEvent;

public sealed record ConnectionSucceededEvent(
    ulong OperationId,
    string Serial,
    string AndroidId,
    string AndroidVersion,
    int Width,
    int Height,
    int PhysicalWidth,
    int PhysicalHeight,
    DisplaySizeSource SizeSource)
    : ConnectionTerminalEvent(OperationId);

public sealed record ConnectionFailedEvent(
    ulong OperationId,
    ConnectionErrorCode ErrorCode,
    string Phase,
    string Message,
    int Attempt,
    int MaxAttempts)
    : ConnectionTerminalEvent(OperationId);

public sealed record DeviceDisplayTransform(
    int CanonicalWidth,
    int CanonicalHeight,
    int RotationDegrees,
    int DeviceWidth,
    int DeviceHeight,
    double Scale,
    double OffsetX,
    double OffsetY,
    double ContentWidth,
    double ContentHeight);

public sealed record DeviceFrameMetadata(
    ulong FrameId,
    ulong SessionGeneration,
    ulong GeometryGeneration,
    int Width,
    int Height,
    int Orientation,
    ulong ByteCount,
    DeviceDisplayTransform Transform);

public abstract record DeviceOperationEvent(ulong OperationId, ulong Sequence)
    : ConnectionTerminalEvent(OperationId);

public sealed record GameVerifiedEvent(
    ulong OperationId,
    ulong Sequence,
    string PackageId)
    : DeviceOperationEvent(OperationId, Sequence);

public sealed record GameVerificationFailedEvent(
    ulong OperationId,
    ulong Sequence,
    ConnectionErrorCode ErrorCode,
    string Phase,
    string Message)
    : DeviceOperationEvent(OperationId, Sequence);

public sealed record FrameCapturedEvent(
    ulong OperationId,
    ulong Sequence,
    DeviceFrameMetadata Frame,
    byte[] PngBytes)
    : DeviceOperationEvent(OperationId, Sequence)
{
    public DeviceFrame? FrameLease { get; init; }
}

public sealed record DeviceInputPoint(
    int CanonicalX,
    int CanonicalY,
    int? DeviceX,
    int? DeviceY);

public sealed record InputSucceededEvent(
    ulong OperationId,
    ulong Sequence,
    DeviceFrameMetadata SourceFrame,
    DeviceInputPoint? FirstPoint,
    DeviceInputPoint? SecondPoint)
    : DeviceOperationEvent(OperationId, Sequence);

public sealed record InputFailedEvent(
    ulong OperationId,
    ulong Sequence,
    ConnectionErrorCode ErrorCode,
    string Phase,
    string Message,
    DeviceFrameMetadata? SourceFrame,
    DeviceInputPoint? FirstPoint,
    DeviceInputPoint? SecondPoint)
    : DeviceOperationEvent(OperationId, Sequence);

public sealed record DeviceOperationFailedEvent(
    ulong OperationId,
    ulong Sequence,
    ConnectionErrorCode ErrorCode,
    string Phase,
    string Message)
    : DeviceOperationEvent(OperationId, Sequence);

public sealed record PackageCommandResult(
    bool Started,
    int ExitCode,
    bool TimedOut,
    bool Canceled,
    ConnectionErrorCode ErrorCode,
    string ErrorMessage,
    string StandardOutput,
    string StandardError);

public sealed record PackageOperationCompletedEvent(
    ulong OperationId,
    ulong Sequence,
    PackageOperationKind Operation,
    PackageCommandResult Command,
    bool ProcessRunning)
    : DeviceOperationEvent(OperationId, Sequence);

public sealed record DeviceDisconnectedEvent(
    ulong SessionGeneration,
    ulong Sequence,
    ConnectionErrorCode ErrorCode)
    : ConnectionEvent;

public sealed record DeviceReconnectedEvent(
    ulong PreviousSessionGeneration,
    ulong SessionGeneration,
    ulong Sequence,
    string Serial,
    int Width,
    int Height,
    int PhysicalWidth,
    int PhysicalHeight,
    DisplaySizeSource SizeSource)
    : ConnectionEvent;

/// <summary>Current ADB endpoint and native session generation, independent of event subscription timing.</summary>
public sealed record DeviceSessionIdentity(
    string AdbPath,
    string Serial,
    ulong SessionGeneration);

public sealed record TaskProgressEvent(
    ulong OperationId,
    ulong Sequence,
    int EventType,
    string TaskName,
    string PipelineName,
    int StepIndex,
    bool Succeeded,
    double Score,
    int TargetX,
    int TargetY,
    string Message,
    long TimestampMs,
    ulong TaskInstanceId = 0)
    : ConnectionEvent;

/// <summary>Native task-engine event kinds carried by direct game-task progress callbacks.</summary>
public enum GameTaskEventType
{
    PipelineStarted,
    PipelineCompleted,
    PipelineFailed,
    TaskEntering,
    RecognitionEvaluated,
    ActionExecuting,
    ActionExecuted,
    TransitionVerified,
    MonitorTriggered,
    TaskCompleted,
    TaskFailed,
    TaskRetrying,
}

/// <summary>Progress for one native single-task operation.</summary>
public sealed record GameTaskProgressEvent(
    ulong OperationId,
    ulong Sequence,
    string TaskId,
    GameTaskEventType EventType,
    string TaskName,
    string PipelineName,
    int StepIndex,
    int CompletedUnits,
    int TotalUnits,
    bool Succeeded,
    double Score,
    int TargetX,
    int TargetY,
    string Message,
    long TimestampMs,
    ulong TaskInstanceId = 0)
    : ConnectionEvent;

public sealed record GameTaskCompletedEvent(
    ulong OperationId,
    ulong Sequence,
    string TaskId,
    int CompletedUnits,
    int TotalUnits,
    bool ProcessDetected,
    ConnectionErrorCode ErrorCode,
    string Message)
    : ConnectionTerminalEvent(OperationId);

public sealed record GameTaskFailedEvent(
    ulong OperationId,
    ulong Sequence,
    string TaskId,
    int CompletedUnits,
    int TotalUnits,
    bool ProcessDetected,
    ConnectionErrorCode ErrorCode,
    string Message)
    : ConnectionTerminalEvent(OperationId);

public enum RuntimeDiagnosticLevel
{
    Debug,
    Information,
    Warning,
    Error,
}

/// <summary>Native runtime log attached to a specific task operation.</summary>
public sealed record RuntimeDiagnosticEvent(
    ulong OperationId,
    ulong Sequence,
    string TaskId,
    RuntimeDiagnosticLevel Level,
    string Message,
    ulong TaskInstanceId = 0)
    : ConnectionEvent;

public sealed record PipelineCompletedEvent(
    ulong OperationId,
    ulong Sequence,
    int Status,
    string FinalTask,
    int TotalSteps)
    : ConnectionTerminalEvent(OperationId);

public sealed record PipelineFailedEvent(
    ulong OperationId,
    ulong Sequence,
    int Status,
    string FinalTask,
    int TotalSteps,
    string ErrorMessage)
    : ConnectionTerminalEvent(OperationId);

public sealed record PipelineRunResult(
    bool Succeeded,
    string FinalTask,
    int TotalSteps,
    string? Message);

public sealed record GameTaskRunResult(
    string TaskId,
    bool Succeeded,
    int CompletedUnits,
    int TotalUnits,
    bool ProcessDetected,
    ConnectionErrorCode ErrorCode,
    string Message);
public sealed record TaskStartedEvent(
    ulong OperationId,
    ulong Sequence,
    ulong TaskId,
    string Name,
    int Index,
    int TotalTasks)
    : ConnectionEvent;

public sealed record TaskDecisionEvent(
    ulong OperationId,
    ulong Sequence,
    ulong TaskId,
    string DecisionType,
    string Details)
    : ConnectionEvent;

public sealed record TaskActionConfirmedEvent(
    ulong OperationId,
    ulong Sequence,
    ulong TaskId,
    string ActionName,
    bool Confirmed)
    : ConnectionEvent;

public sealed record TaskCompletedEvent(
    ulong OperationId,
    ulong Sequence,
    ulong TaskId,
    string Name,
    int CompletedUnits,
    int TotalUnits,
    string Message)
    : ConnectionEvent;

public sealed record TaskFailedEvent(
    ulong OperationId,
    ulong Sequence,
    ulong TaskId,
    string Name,
    ConnectionErrorCode ErrorCode,
    string Message)
    : ConnectionEvent;

public sealed record QueueCompletedEvent(
    ulong OperationId,
    ulong Sequence,
    int SucceededTasks,
    int FailedTasks,
    int SkippedTasks,
    int TotalTasks)
    : ConnectionTerminalEvent(OperationId);

public sealed record QueueFailedEvent(
    ulong OperationId,
    ulong Sequence,
    ConnectionErrorCode ErrorCode,
    string Message,
    int SucceededTasks,
    int FailedTasks,
    int SkippedTasks,
    int TotalTasks)
    : ConnectionTerminalEvent(OperationId);

/// <summary>
/// Result of the queue run itself. <see cref="Succeeded"/> means Core reached
/// its normal queue terminal state; inspect <see cref="FailedTasks"/> for
/// individual task failures that did not abort the remaining queue.
/// </summary>
public sealed record QueueRunResult(
    bool Succeeded,
    int SucceededTasks,
    int FailedTasks,
    int SkippedTasks,
    int TotalTasks,
    ConnectionErrorCode ErrorCode,
    string Message);
