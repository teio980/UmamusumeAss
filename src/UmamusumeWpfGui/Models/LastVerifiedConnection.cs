namespace UmamusumeWpfGui.Models;

/// <summary>
/// Immutable snapshot recorded after a successful connection verification.
/// This is historical data, not an operation state.
/// </summary>
public sealed record LastVerifiedConnection(
    string AdbPath,
    string Serial,
    string AndroidId,
    string AndroidVersion,
    int Width,
    int Height,
    int PhysicalWidth,
    int PhysicalHeight,
    DateTimeOffset VerifiedAt);
