namespace UmamusumeWpfGui.Models;

/// <summary>
/// Display-only snapshot of the current S2 control session state.
/// Defaults to <see cref="ConnectionState.Disconnected"/> until Phase 7.
/// </summary>
public sealed record ControlSessionSnapshot(
    string Serial,
    string? TargetPackageId,
    long GeometryGeneration,
    int? FrameWidth,
    int? FrameHeight,
    DateTimeOffset? CapturedAt,
    ConnectionState State);
