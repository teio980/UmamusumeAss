using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Tasks;




public sealed record GrassTaskExecutionContext(
    LastVerifiedConnection? Connection,
    IGrassTaskLogSink? LogSink = null);
