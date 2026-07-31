using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;





public interface IGameLauncher
{
    Task<GameLaunchResult> StartAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default);

    Task<GameLaunchResult> StartAsync(
        string adbPath,
        string serial,
        string packageName,
        string? activityName,
        CancellationToken cancellationToken = default);

    Task<GameLaunchResult> StopAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default);
}
