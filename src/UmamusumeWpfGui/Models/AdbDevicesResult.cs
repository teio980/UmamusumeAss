namespace UmamusumeWpfGui.Models;

/// <summary>
/// The result of running <c>adb devices</c> against a specific ADB executable.
/// <see cref="Records"/> contains each parsed device entry.
/// <see cref="Diagnostics"/> contains any warnings or errors encountered
/// during execution or parsing.
/// </summary>
public sealed record AdbDevicesResult(
    IReadOnlyList<AdbDeviceRecord> Records,
    IReadOnlyList<DiscoveryDiagnostic> Diagnostics);
