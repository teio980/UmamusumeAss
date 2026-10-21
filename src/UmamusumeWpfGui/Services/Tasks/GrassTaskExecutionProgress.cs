using System.Globalization;

namespace UmamusumeWpfGui.Services.Tasks;

public sealed record GrassTaskExecutionProgress(
    int CompletedRuns,
    int TotalRuns,
    string? TaskId = null,
    ulong? OperationId = null,
    int? StepIndex = null,
    string? TaskName = null)
{
    public string DisplayText => string.Create(
        CultureInfo.InvariantCulture, $"{CompletedRuns}/{TotalRuns}");
}
