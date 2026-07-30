using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// Registry of task types that can be offered by the grass page.
/// It is intentionally empty until task modules are implemented.
/// </summary>
public interface IGrassTaskCatalog
{
    IReadOnlyList<GrassTaskDefinition> Definitions { get; }
}
