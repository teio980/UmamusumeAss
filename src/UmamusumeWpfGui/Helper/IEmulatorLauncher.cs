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

        var (fileName, arguments) = SplitCommand(executablePath);
        if (!File.Exists(fileName))
            return new EmulatorLaunchResult(false, "The configured emulator executable was not found.");

        try
        {
            var isDirectExecutable = string.Equals(
                Path.GetExtension(fileName),
                ".exe",
                StringComparison.OrdinalIgnoreCase);
            var process = Process.Start(new ProcessStartInfo(fileName)
            {
                Arguments = arguments,




                UseShellExecute = !isDirectExecutable,
                CreateNoWindow = isDirectExecutable,
                WindowStyle = isDirectExecutable
                    ? ProcessWindowStyle.Hidden
                    : ProcessWindowStyle.Normal,
            });
            return process is null
                ? new EmulatorLaunchResult(false, "The emulator process could not be started.")
                : new EmulatorLaunchResult(true, "Emulator startup was requested.");
        }
        catch (Exception exception)
        {
            return new EmulatorLaunchResult(false, $"The emulator could not be started: {exception.Message}");
        }
    }

    private static (string FileName, string Arguments) SplitCommand(string command)
    {
        var trimmed = command.Trim();
        if (!trimmed.StartsWith('"'))
            return (trimmed, string.Empty);

        var closingQuote = trimmed.IndexOf('"', 1);
        if (closingQuote < 0)
            return (trimmed, string.Empty);

        return (
            trimmed[1..closingQuote],
            trimmed[(closingQuote + 1)..].TrimStart());
    }
}
