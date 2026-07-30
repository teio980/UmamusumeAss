using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// Registry of independent task modules that can be offered by the Hachimi~
/// page. The queue never needs to know a module's internal implementation.
/// </summary>
public interface IGrassTaskCatalog
{
    IReadOnlyList<IGrassTaskModule> Modules { get; }
}
