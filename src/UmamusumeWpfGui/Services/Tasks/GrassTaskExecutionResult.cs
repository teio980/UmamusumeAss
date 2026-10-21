using Umamusume.CoreBridge;

namespace UmamusumeWpfGui.Services.Tasks;




public sealed record GrassTaskExecutionResult(
    bool Succeeded,
    bool ProcessDetected,
    string Message)
{
    public ConnectionErrorCode ErrorCode { get; init; }
}
