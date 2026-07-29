using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Helper;

/// <summary>
/// GUI-layer emulator discovery and ADB device listing.
/// Uses injected process enumerator, ADB runner, and file-system seam
/// so all discovery behaviors are testable without real emulators.
/// </summary>
public sealed class WinAdapter : IWinAdapter
{
    private readonly IProcessEnumerator _processEnumerator;
    private readonly IAdbRunner _adbRunner;
    private readonly IFileSystem _fileSystem;
    private readonly IAsyncDelay _asyncDelay;

    public WinAdapter(
        IProcessEnumerator processEnumerator,
        IAdbRunner adbRunner,
        IFileSystem fileSystem)
        : this(processEnumerator, adbRunner, fileSystem, new AsyncDelay())
    {
    }

    public WinAdapter(
        IProcessEnumerator processEnumerator,
        IAdbRunner adbRunner,
        IFileSystem fileSystem,
        IAsyncDelay asyncDelay)
    {
        ArgumentNullException.ThrowIfNull(processEnumerator);
        ArgumentNullException.ThrowIfNull(adbRunner);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(asyncDelay);
        _processEnumerator = processEnumerator;
        _adbRunner = adbRunner;
        _fileSystem = fileSystem;
        _asyncDelay = asyncDelay;
    }

    // ================================================================
    // RefreshEmulatorsInfo
    // ================================================================

    public DiscoveryResult RefreshEmulatorsInfo()
    {
        var processes = _processEnumerator.GetProcesses();
        var candidates = new List<DetectedEmulatorInfo>();
        var diagnostics = new List<DiscoveryDiagnostic>();
        var seenAdbPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in processes)
        {
            if (process.MainModulePath == null)
            {
                diagnostics.Add(new DiscoveryDiagnostic(
                    $"Skipped process '{process.Name}': main module path is inaccessible",
                    DiagnosticSeverity.Warning));
                continue;
            }

            if (!EmulatorProfileCatalog.TryGetForProcess(process.Name, out var profile))
            {
                diagnostics.Add(new DiscoveryDiagnostic(
                    $"Skipped unrecognized process '{process.Name}'",
                    DiagnosticSeverity.Info));
                continue;
            }

            var processDir = System.IO.Path.GetDirectoryName(process.MainModulePath);
            if (processDir == null)
            {
                diagnostics.Add(new DiscoveryDiagnostic(
                    $"Cannot derive directory from process path '{process.MainModulePath}'",
                    DiagnosticSeverity.Warning));
                continue;
            }

            string? resolvedAdbPath = null;
            foreach (var relativeCandidate in profile.AdbCandidates)
            {
                var fullCandidate = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(processDir, relativeCandidate));
                if (_fileSystem.FileExists(fullCandidate))
                {
                    resolvedAdbPath = fullCandidate;
                    break;
                }
            }

            // Dedup: skip if we already have a candidate with the same resolved ADB path.
            // Different processes resolving to the same ADB file yield one entry.
            if (resolvedAdbPath != null && !seenAdbPaths.Add(resolvedAdbPath))
                continue;

            // If AdbPath is null (no candidate found), and we already have a
            // null-ADB-path candidate for a different process name, don't dedup —
            // those are different emulator installs even without ADB found.
            if (resolvedAdbPath == null && candidates.Any(c => c.AdbPath == null))
                continue;

            candidates.Add(new DetectedEmulatorInfo(profile.Name, resolvedAdbPath));
        }

        return new DiscoveryResult(candidates.AsReadOnly(), diagnostics.AsReadOnly());
    }

    // ================================================================
    // GetAdbDevices
    // ================================================================

    public AdbDevicesResult GetAdbDevices(string adbPath)
    {
        var diagnostics = new List<DiscoveryDiagnostic>();

        var (stdout, stderr, exitCode, timedOut, error) = _adbRunner.RunDevices(adbPath);

        if (error != null)
        {
            diagnostics.Add(new DiscoveryDiagnostic(
                $"Failed to run 'adb devices': {error.Message}",
                DiagnosticSeverity.Error));
            return new AdbDevicesResult([], diagnostics.AsReadOnly());
        }

        if (timedOut)
        {
            diagnostics.Add(new DiscoveryDiagnostic(
                "'adb devices' timed out",
                DiagnosticSeverity.Error));
            return new AdbDevicesResult([], diagnostics.AsReadOnly());
        }

        if (exitCode != 0)
        {
            diagnostics.Add(new DiscoveryDiagnostic(
                $"'adb devices' exited with non-zero exit code {exitCode}",
                DiagnosticSeverity.Error));
            // Fall through: still try to parse any output
        }

        if (!string.IsNullOrEmpty(stderr))
        {
            diagnostics.Add(new DiscoveryDiagnostic(
                $"'adb devices' stderr: {stderr}",
                DiagnosticSeverity.Warning));
        }

        var records = ParseAdbDevicesOutput(stdout, diagnostics);

        return new AdbDevicesResult(records.AsReadOnly(), diagnostics.AsReadOnly());
    }

    public EndpointResolutionResult ResolveEndpoints(
        string adbPath,
        string profileName,
        CancellationToken cancellationToken) =>
        new EndpointResolver(_adbRunner).Resolve(adbPath, profileName, cancellationToken);

    public Task<EndpointResolutionResult> ResolveEndpointsAsync(
        string adbPath,
        string profileName,
        CancellationToken cancellationToken) =>
        new EndpointResolver(_adbRunner, _asyncDelay)
            .ResolveAsync(adbPath, profileName, cancellationToken);

    /// <summary>
    /// Parses the stdout of <c>adb devices</c> into device records.
    /// Skips the "List of devices attached" header and blank lines.
    /// Splits each data line by tab: column 0 is the serial, column 1 is the state.
    /// Lines without a tab separator are recorded as malformed diagnostics.
    /// </summary>
    private static List<AdbDeviceRecord> ParseAdbDevicesOutput(
        string stdout, List<DiscoveryDiagnostic> diagnostics)
    {
        var records = new List<AdbDeviceRecord>();

        foreach (var rawLine in stdout.Split('\n', StringSplitOptions.None))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // Skip the known header line
            if (line.StartsWith("List of devices", StringComparison.Ordinal))
                continue;

            // Split by tab: serial \t state
            var parts = line.Split('\t', 2, StringSplitOptions.None);
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
            {
                diagnostics.Add(new DiscoveryDiagnostic(
                    $"Malformed 'adb devices' line: '{line}'",
                    DiagnosticSeverity.Warning));
                continue;
            }

            records.Add(new AdbDeviceRecord(parts[0].Trim(), parts[1].Trim()));
        }

        return records;
    }

}
