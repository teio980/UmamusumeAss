namespace UmamusumeWpfGui.Models;

/// <summary>
/// Information about a detected emulator from process scanning.
/// <see cref="AdbPath"/> is null when no candidate ADB executable
/// was found at the expected paths.
/// </summary>
public sealed record DetectedEmulatorInfo(string EmulatorName, string? AdbPath);
