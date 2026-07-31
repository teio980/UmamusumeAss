namespace UmamusumeWpfGui.Models;






public sealed record DiscoveryDiagnostic(string Message, DiagnosticSeverity Severity);




public enum DiagnosticSeverity
{

    Info,


    Warning,


    Error,
}
