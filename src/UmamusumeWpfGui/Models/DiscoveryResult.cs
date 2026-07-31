namespace UmamusumeWpfGui.Models;







public sealed record DiscoveryResult(
    IReadOnlyList<DetectedEmulatorInfo> Candidates,
    IReadOnlyList<DiscoveryDiagnostic> Diagnostics);
