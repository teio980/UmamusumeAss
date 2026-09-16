using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>
/// Receives concise semantic milestones for one Hachimi queue task.
/// This channel is deliberately separate from <see cref="IGrassTaskLogSink"/>
/// so the workspace can show meaningful progress without diagnostic noise.
/// </summary>
public interface IHachimiTaskLogSink
{
    void Add(
        string phase,
        string message,
        HachimiTaskLogEventKind kind = HachimiTaskLogEventKind.Info);
}
