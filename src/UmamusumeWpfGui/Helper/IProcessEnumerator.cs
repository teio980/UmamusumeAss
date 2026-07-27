namespace UmamusumeWpfGui.Helper;

/// <summary>
/// Abstraction over system process enumeration, allowing discovery
/// to be tested without real processes.
/// </summary>
public interface IProcessEnumerator
{
    /// <summary>
    /// Returns a snapshot of currently running processes.
    /// Each entry contains the process name and the main module path,
    /// or null if the process is inaccessible.
    /// </summary>
    ProcessEntry[] GetProcesses();
}

/// <summary>
/// Lightweight process information used during emulator discovery.
/// </summary>
/// <param name="Name">Process name without extension (e.g. "HD-Player").</param>
/// <param name="MainModulePath">Full path to the main module, or null if inaccessible.</param>
public sealed record ProcessEntry(string Name, string? MainModulePath);
