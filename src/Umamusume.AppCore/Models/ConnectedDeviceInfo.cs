namespace Umamusume.AppCore.Models;

public sealed record ConnectedDeviceInfo(
    string Serial,
    string AndroidId,
    string AndroidVersion,
    int Width,
    int Height,
    int PhysicalWidth,
    int PhysicalHeight);
