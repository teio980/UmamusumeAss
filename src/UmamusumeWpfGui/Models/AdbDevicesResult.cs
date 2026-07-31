namespace UmamusumeWpfGui.Models;







public sealed record AdbDevicesResult(
    IReadOnlyList<AdbDeviceRecord> Records,
    IReadOnlyList<DiscoveryDiagnostic> Diagnostics);
