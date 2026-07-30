namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Normalized result returned by every task module.
/// </summary>
public sealed record GrassTaskExecutionResult(
    bool Succeeded,
    bool ProcessDetected,
    string Message);
