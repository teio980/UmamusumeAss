using System;

namespace Umamusume.AppCore.Models;

public sealed record LastVerifiedConnection(
    string Serial,
    int Width,
    int Height,
    int PhysicalWidth,
    int PhysicalHeight,
    DateTime VerifiedAt);
