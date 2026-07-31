using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;





public interface IGrassTaskModule
{
    GrassTaskDefinition Definition { get; }

    object Settings { get; }


    JsonObject ExportSettings();


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
