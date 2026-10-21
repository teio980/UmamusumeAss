using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>UI settings and serialization for a task implemented by Core.</summary>
public interface IGrassTaskModule
{
    GrassTaskDefinition Definition { get; }

    object Settings { get; }

    JsonObject ExportSettings();

    void ImportSettings(JsonObject settings);

    IGrassTaskModule CreateInstance();
}
