using System;

namespace Umamusume.AppCore.Models;

public sealed record ControlSessionSnapshot(
    string Serial,
    string? TargetPackageId,
    ulong GeometryGeneration,
    int? FrameWidth,
    int? FrameHeight,
    DateTime? CapturedAt,
    ConnectionState State);
