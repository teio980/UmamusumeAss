using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

/// <summary>ADB access retained for emulator discovery and connection UI.</summary>
public interface IAdbRuntime
{
    Task<AdbDeviceListResult> ListDevicesAsync(
        string adbPath,
        CancellationToken cancellationToken = default);
}
