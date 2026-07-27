namespace UmamusumeWpfGui.Models;

/// <summary>
/// A diagnostic message produced during emulator discovery or ADB device listing.
/// </summary>
/// <param name="Message">Human-readable diagnostic description.</param>
/// <param name="Severity">Severity level of the diagnostic.</param>
public sealed record DiscoveryDiagnostic(string Message, DiagnosticSeverity Severity);

/// <summary>
/// Severity classification for <see cref="DiscoveryDiagnostic"/>.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>Informational message, not a problem.</summary>
    Info,

    /// <summary>Warning that something unexpected was encountered.</summary>
    Warning,

    /// <summary>Error that caused discovery to produce incomplete results.</summary>
    Error,
}
