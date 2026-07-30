using System.Diagnostics;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Helper;

public sealed record EndpointResolutionPolicy(
    TimeSpan ReadyPollTimeout,
    TimeSpan PollInterval,
    int MaxAttempts,
    TimeSpan RetryInterval)
{
    public static EndpointResolutionPolicy Default { get; } = new(
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMilliseconds(250),
        3,
        TimeSpan.FromSeconds(2));
}

public sealed record EndpointResolutionResult(
    IReadOnlyList<string> VerifiedEndpoints,
    IReadOnlyList<DiscoveryDiagnostic> Diagnostics);

internal sealed class EndpointResolver
{
    private readonly IAdbRunner _adbRunner;
    private readonly IAsyncDelay _asyncDelay;

    public EndpointResolver(IAdbRunner adbRunner)
        : this(adbRunner, new AsyncDelay())
    {
    }

    public EndpointResolver(IAdbRunner adbRunner, IAsyncDelay asyncDelay)
    {
        ArgumentNullException.ThrowIfNull(adbRunner);
        ArgumentNullException.ThrowIfNull(asyncDelay);
        _adbRunner = adbRunner;
        _asyncDelay = asyncDelay;
    }

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

            var connectOutput = $"{connect.Stdout}\n{connect.Stderr}";
            if (!ReportsConnected(connectOutput))
            {
                diagnostics.Add(new DiscoveryDiagnostic(
                    $"ADB 'connect {endpoint}' did not report a successful connection",
                    DiagnosticSeverity.Warning));
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

    public async Task<EndpointResolutionResult> ResolveAsync(
        string adbPath,
        string profileName,
        CancellationToken cancellationToken,
        EndpointResolutionPolicy? policy = null)
    {
        var effectivePolicy = policy ?? EndpointResolutionPolicy.Default;
        var diagnostics = new List<DiscoveryDiagnostic>();
        cancellationToken.ThrowIfCancellationRequested();

        var devices = await _adbRunner.RunAsync(
            adbPath, ["devices"], cancellationToken).ConfigureAwait(false);
        var listedEndpoints = ParseDeviceEndpoints(devices, diagnostics);
        if (listedEndpoints.Count > 0)
        {
            return new EndpointResolutionResult(listedEndpoints, diagnostics);
        }

        var maxAttempts = Math.Max(effectivePolicy.MaxAttempts, 1);
        foreach (var endpoint in EmulatorProfileCatalog.GetFallbackEndpoints(profileName))
        {
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var connect = await _adbRunner.RunAsync(
                    adbPath, ["connect", endpoint], cancellationToken).ConfigureAwait(false);
                AddCommandDiagnostic(connect, $"connect {endpoint}", diagnostics);
                if (!IsSuccessful(connect))
                {
                    await DelayBeforeRetryAsync(attempt, maxAttempts, effectivePolicy, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                var connectOutput = $"{connect.Stdout}\n{connect.Stderr}";
                if (!ReportsConnected(connectOutput))
                {
                    diagnostics.Add(new DiscoveryDiagnostic(
                        $"ADB 'connect {endpoint}' did not report a successful connection",
                        DiagnosticSeverity.Warning));
                    await DelayBeforeRetryAsync(attempt, maxAttempts, effectivePolicy, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                var pollStart = Stopwatch.GetTimestamp();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var poll = await _adbRunner.RunAsync(
                        adbPath, ["devices"], cancellationToken).ConfigureAwait(false);
                    if (ContainsReadyEndpoint(poll, endpoint, diagnostics))
                    {
                        return new EndpointResolutionResult([endpoint], diagnostics);
                    }

                    if (!IsSuccessful(poll)
                        || Stopwatch.GetElapsedTime(pollStart) >= NonNegative(effectivePolicy.ReadyPollTimeout))
                    {
                        diagnostics.Add(new DiscoveryDiagnostic(
                            $"ADB endpoint '{endpoint}' did not become ready during attempt {attempt}/{maxAttempts}",
                            DiagnosticSeverity.Warning));
                        break;
                    }

                    await _asyncDelay.DelayAsync(
                        NonNegative(effectivePolicy.PollInterval), cancellationToken).ConfigureAwait(false);
                }

                await DelayBeforeRetryAsync(attempt, maxAttempts, effectivePolicy, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return new EndpointResolutionResult([], diagnostics);
    }

    private async Task DelayBeforeRetryAsync(
        int attempt,
        int maxAttempts,
        EndpointResolutionPolicy policy,
        CancellationToken cancellationToken)
    {
        if (attempt >= maxAttempts) return;
        await _asyncDelay.DelayAsync(
            NonNegative(policy.RetryInterval), cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSuccessful(AdbCommandResult result) =>
        result.Error is null && !result.TimedOut && result.ExitCode == 0;

    private static bool ReportsConnected(string output) =>
        output.Contains("connected", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan NonNegative(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static List<string> ParseDeviceEndpoints(
        AdbCommandResult result,
        List<DiscoveryDiagnostic> diagnostics)
    {
        AddCommandDiagnostic(result, "devices", diagnostics);
        if (!IsSuccessful(result))
        {
            return [];
        }

        return ParseDeviceStates(result.Stdout)
            .Where(device => device.State == "device")
            .Select(device => device.Serial)
            .ToList();
    }

    private static bool ContainsReadyEndpoint(
        AdbCommandResult result,
        string endpoint,
        List<DiscoveryDiagnostic> diagnostics)
    {
        AddCommandDiagnostic(result, "devices", diagnostics);
        return IsSuccessful(result)
               && ParseDeviceStates(result.Stdout).Any(
                   device => device.Serial == endpoint && device.State == "device");
    }

    private static IEnumerable<(string Serial, string State)> ParseDeviceStates(string stdout)
    {
        foreach (var rawLine in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Equals("List of devices attached", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split('\t', 2, StringSplitOptions.None);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                yield return (parts[0].Trim(), parts[1].Trim());
            }
        }
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
