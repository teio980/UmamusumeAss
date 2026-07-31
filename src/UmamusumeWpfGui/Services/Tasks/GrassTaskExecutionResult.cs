namespace UmamusumeWpfGui.Services.Tasks;




public sealed record GrassTaskExecutionResult(
    bool Succeeded,
    bool ProcessDetected,
    string Message);
