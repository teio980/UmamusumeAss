using System.Globalization;

namespace UmamusumeWpfGui.Services.Tasks;

public sealed record GrassTaskExecutionProgress(int CompletedRuns, int TotalRuns)
{
    public string DisplayText => string.Create(
        CultureInfo.InvariantCulture, $"{CompletedRuns}/{TotalRuns}");
}
