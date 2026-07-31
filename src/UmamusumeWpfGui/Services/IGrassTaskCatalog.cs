using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services;





public interface IGrassTaskCatalog
{
    IReadOnlyList<IGrassTaskModule> Modules { get; }
}
