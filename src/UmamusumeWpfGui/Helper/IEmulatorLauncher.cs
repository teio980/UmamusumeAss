using System.Diagnostics;
using System.IO;

namespace UmamusumeWpfGui.Helper;

public interface IEmulatorLauncher
{
    EmulatorLaunchResult Start(string executablePath);
}

public sealed record EmulatorLaunchResult(bool Started, string Message);

public sealed class EmulatorLauncher : IEmulatorLauncher
{
    public EmulatorLaunchResult Start(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return new EmulatorLaunchResult(false, "An emulator executable path is required.");

        if (!File.Exists(executablePath))
            return new EmulatorLaunchResult(false, "The configured emulator executable was not found.");

        try
        {
            var process = Process.Start(new ProcessStartInfo(executablePath) { UseShellExecute = true });
            return process is null
                ? new EmulatorLaunchResult(false, "The emulator process could not be started.")
                : new EmulatorLaunchResult(true, "Emulator startup was requested.");
        }
        catch (Exception exception)
        {
            return new EmulatorLaunchResult(false, $"The emulator could not be started: {exception.Message}");
        }
    }
}
