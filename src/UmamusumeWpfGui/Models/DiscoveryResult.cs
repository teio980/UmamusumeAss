namespace UmamusumeWpfGui.Models;

/// <summary>
/// The complete result of emulator process discovery.
/// <see cref="Candidates"/> lists each detected emulator with its resolved ADB path.
/// <see cref="Diagnostics"/> contains any informational, warning, or error messages
/// recorded during discovery.
/// </summary>
public sealed record DiscoveryResult(
    IReadOnlyList<DetectedEmulatorInfo> Candidates,
    IReadOnlyList<DiscoveryDiagnostic> Diagnostics);
