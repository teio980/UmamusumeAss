using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Immutable runtime data shared with a task during one queue run.
/// </summary>
public sealed record GrassTaskExecutionContext(LastVerifiedConnection? Connection);
