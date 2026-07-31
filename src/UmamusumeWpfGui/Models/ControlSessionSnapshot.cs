namespace UmamusumeWpfGui.Models;





public sealed record ControlSessionSnapshot(
    string Serial,
    string? TargetPackageId,
    long GeometryGeneration,
    int? FrameWidth,
    int? FrameHeight,
    DateTimeOffset? CapturedAt,
    ConnectionState State);
