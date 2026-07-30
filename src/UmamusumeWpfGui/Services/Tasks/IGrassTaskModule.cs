using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Independent task module contract. A module owns its definition, settings,
/// execution and stop behavior; the queue only orchestrates module instances.
/// </summary>
public interface IGrassTaskModule
{
    GrassTaskDefinition Definition { get; }

    object Settings { get; }

    IGrassTaskModule CreateInstance();

    bool CanExecute(GrassTaskExecutionContext context);

    Task<GrassTaskExecutionResult> ExecuteAsync(
        GrassTaskExecutionContext context,
        CancellationToken cancellationToken = default);

    Task<GrassTaskExecutionResult> StopAsync(
        GrassTaskExecutionContext context,
        CancellationToken cancellationToken = default);
}
