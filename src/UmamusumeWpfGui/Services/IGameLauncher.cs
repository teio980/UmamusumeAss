using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// Game lifecycle boundary. It mirrors MAA's start_game/stop_game methods
/// without coupling the Hachimi page to ADB command construction.
/// </summary>
public interface IGameLauncher
{
    Task<GameLaunchResult> StartAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default);

    Task<GameLaunchResult> StopAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default);
}
