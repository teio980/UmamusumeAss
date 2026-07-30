using UmamusumeWpfGui.Helper;

namespace UmamusumeWpfGui.Models;

public sealed record GameLaunchResult(
    bool Succeeded,
    bool ProcessDetected,
    string Message,
    AdbCommandResult CommandResult);
