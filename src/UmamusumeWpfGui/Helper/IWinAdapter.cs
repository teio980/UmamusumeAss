using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Helper;

/// <summary>
/// GUI-layer emulator discovery and ADB device listing.
/// Uses process scanning for emulator detection and the configured
/// ADB executable for <c>adb devices</c>.
/// </summary>
public interface IWinAdapter
{
    /// <summary>
    /// Scans running processes for known emulator executables and
    /// derives candidate ADB paths. Results include diagnostics for
    /// unknown processes, inaccessible entries, and mismatched files.
    /// </summary>
    DiscoveryResult RefreshEmulatorsInfo();

    /// <summary>
    /// Runs <c>adb devices</c> with the given ADB executable and
    /// returns the parsed device records. Includes diagnostics for
    /// non-zero exit codes, stderr, timeouts, and malformed output.
    /// </summary>
    AdbDevicesResult GetAdbDevices(string adbPath);

    /// <summary>
    /// Asynchronously runs <c>adb devices</c>. The cancellation token covers
    /// both process execution and the caller's discovery operation.
    /// </summary>
    Task<AdbDevicesResult> GetAdbDevicesAsync(
        string adbPath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(GetAdbDevices(adbPath));

    EndpointResolutionResult ResolveEndpoints(string adbPath, string profileName, CancellationToken cancellationToken);

    Task<EndpointResolutionResult> ResolveEndpointsAsync(
        string adbPath,
        string profileName,
        CancellationToken cancellationToken) =>
        Task.FromResult(ResolveEndpoints(adbPath, profileName, cancellationToken));
}
