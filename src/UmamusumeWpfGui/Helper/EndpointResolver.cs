using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Helper;

public sealed record EndpointResolutionResult(
    IReadOnlyList<string> VerifiedEndpoints,
    IReadOnlyList<DiscoveryDiagnostic> Diagnostics);

internal sealed class EndpointResolver
{
    private readonly IAdbRunner _adbRunner;

    public EndpointResolver(IAdbRunner adbRunner) => _adbRunner = adbRunner;

    public EndpointResolutionResult Resolve(
        string adbPath,
        string profileName,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<DiscoveryDiagnostic>();
        var devices = _adbRunner.Run(adbPath, ["devices"]);
        var listedEndpoints = ParseDeviceEndpoints(devices, diagnostics);
        if (listedEndpoints.Count > 0)
        {
            return new EndpointResolutionResult(listedEndpoints, diagnostics);
        }

        foreach (var endpoint in EmulatorProfileCatalog.GetFallbackEndpoints(profileName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connect = _adbRunner.Run(adbPath, ["connect", endpoint]);
            AddCommandDiagnostic(connect, $"connect {endpoint}", diagnostics);
            if (connect.Error is not null || connect.TimedOut || connect.ExitCode != 0)
            {
                continue;
            }

            var state = _adbRunner.Run(adbPath, ["-s", endpoint, "get-state"]);
            AddCommandDiagnostic(state, $"get-state {endpoint}", diagnostics);
            if (state.Error is null && !state.TimedOut && state.ExitCode == 0 && state.Stdout.Trim() == "device")
            {
                return new EndpointResolutionResult([endpoint], diagnostics);
            }
        }

        return new EndpointResolutionResult([], diagnostics);
    }

    private static List<string> ParseDeviceEndpoints(
        AdbCommandResult result,
        List<DiscoveryDiagnostic> diagnostics)
    {
        AddCommandDiagnostic(result, "devices", diagnostics);
        if (result.Error is not null || result.TimedOut || result.ExitCode != 0)
        {
            return [];
        }

        return result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t', 2, StringSplitOptions.None))
            .Where(parts => parts.Length == 2 && parts[1] == "device")
            .Select(parts => parts[0])
            .ToList();
    }

    private static void AddCommandDiagnostic(
        AdbCommandResult result,
        string command,
        List<DiscoveryDiagnostic> diagnostics)
    {
        if (result.Error is not null)
        {
            diagnostics.Add(new DiscoveryDiagnostic($"ADB '{command}' failed: {result.Error.Message}", DiagnosticSeverity.Error));
        }
        else if (result.TimedOut)
        {
            diagnostics.Add(new DiscoveryDiagnostic($"ADB '{command}' timed out", DiagnosticSeverity.Error));
        }
        else if (result.ExitCode != 0)
        {
            diagnostics.Add(new DiscoveryDiagnostic($"ADB '{command}' exited with code {result.ExitCode}", DiagnosticSeverity.Warning));
        }
    }
}
