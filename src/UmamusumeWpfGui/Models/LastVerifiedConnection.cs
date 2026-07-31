namespace UmamusumeWpfGui.Models;





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
