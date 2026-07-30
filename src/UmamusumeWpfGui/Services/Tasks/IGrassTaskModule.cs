using System.Text.Json.Nodes;
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

    /// <summary>Exports only this module's persisted settings.</summary>
    JsonObject ExportSettings();

    /// <summary>Restores this module's settings from the queue cache.</summary>
    void ImportSettings(JsonObject settings);

    IGrassTaskModule CreateInstance();

    bool CanExecute(GrassTaskExecutionContext context);

    Task<GrassTaskExecutionResult> ExecuteAsync(
        GrassTaskExecutionContext context,
        CancellationToken cancellationToken = default);

    Task<GrassTaskExecutionResult> StopAsync(
        GrassTaskExecutionContext context,
        CancellationToken cancellationToken = default);
}
