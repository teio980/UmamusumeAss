using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Receives lifecycle messages produced by one Hachimi task run.
/// The queue owns the sink so task modules do not depend on a particular view.
/// </summary>
public interface IGrassTaskLogSink
{
    void Add(string type, string details, LogEntryKind kind = LogEntryKind.Info);
}
