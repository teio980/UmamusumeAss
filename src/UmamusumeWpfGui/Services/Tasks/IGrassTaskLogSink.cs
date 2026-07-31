using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;





public interface IGrassTaskLogSink
{
    void Add(string type, string details, LogEntryKind kind = LogEntryKind.Info);
}
