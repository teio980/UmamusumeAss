using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// Device-scoped ADB operations used by the future task engine.
///
/// The interface intentionally exposes command results instead of throwing on
/// normal ADB failures. A missing device, an offline device, and a command
/// rejection are expected runtime states that the UI/task engine can report
/// and recover from. Cancellation and invalid arguments still throw.
/// </summary>
public interface IAdbRuntime
{
    Task<AdbDeviceListResult> ListDevicesAsync(
        string adbPath,
        CancellationToken cancellationToken = default);

    Task<AdbDeviceListResult> WaitForDeviceAsync(
        string adbPath,
        string serial,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> ConnectAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> DisconnectAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default);

    Task<AdbRuntimeQueryResult<string>> GetStateAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default);

    Task<AdbRuntimeQueryResult<bool>> IsBootCompletedAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default);

    Task<AdbRuntimeQueryResult<int>> GetOrientationAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default);

    Task<AdbRuntimeQueryResult<string>> GetDisplayIdAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default);

    Task<AdbRuntimeQueryResult<string>> GetInputEventIdAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default);

    Task<AdbRuntimeQueryResult<double>> GetRefreshRateAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default);

    Task<AdbRuntimeQueryResult<bool>> WaitForBootCompletedAsync(
        string adbPath,
        string serial,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> ShellAsync(
        string adbPath,
        string serial,
        IReadOnlyList<string> shellArguments,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> TapAsync(
        string adbPath,
        string serial,
        int x,
        int y,
        int? displayId = null,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> SwipeAsync(
        string adbPath,
        string serial,
        int startX,
        int startY,
        int endX,
        int endY,
        int durationMilliseconds,
        bool extraSwipe = false,
        int? displayId = null,
        AdbRuntimeOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> InputTextAsync(
        string adbPath,
        string serial,
        string text,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> KeyEventAsync(
        string adbPath,
        string serial,
        string keyCode,
        int? displayId = null,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> BackAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> HomeAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> PressEscapeAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> StartPackageAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> StartActivityAsync(
        string adbPath,
        string serial,
        string packageName,
        string activityName,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> StopPackageAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default);

    Task<AdbRuntimeQueryResult<bool>> IsPackageRunningAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default);

    Task<AdbRuntimeQueryResult<IReadOnlyList<string>>> ListPackagesAsync(
        string adbPath,
        string serial,
        string? packageNameFilter = null,
        CancellationToken cancellationToken = default);

    Task<AdbRuntimeQueryResult<string>> GetPackageVersionAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> PushAsync(
        string adbPath,
        string serial,
        string localPath,
        string remotePath,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> PullAsync(
        string adbPath,
        string serial,
        string remotePath,
        string localPath,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> RemoveAsync(
        string adbPath,
        string serial,
        string remotePath,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> InstallApkAsync(
        string adbPath,
        string serial,
        string apkPath,
        bool replaceExisting = true,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> UninstallPackageAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> ClearPackageDataAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> RebootAsync(
        string adbPath,
        string serial,
        string? mode = null,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> RootAsync(
        string adbPath,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> UnrootAsync(
        string adbPath,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> RemountAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> KillServerAsync(
        string adbPath,
        CancellationToken cancellationToken = default);

    Task<AdbRuntimeQueryResult<AdbScreenSize>> GetScreenSizeAsync(
        string adbPath,
        string serial,
        int? displayId = null,
        CancellationToken cancellationToken = default);

    Task<AdbRuntimeQueryResult<AdbDeviceProperties>> GetDevicePropertiesAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default);

    Task<AdbBinaryCommandResult> CaptureScreenshotAsync(
        string adbPath,
        string serial,
        int? displayId = null,
        CancellationToken cancellationToken = default);

    Task<AdbBinaryCommandResult> CaptureRawScreenshotAsync(
        string adbPath,
        string serial,
        bool gzip = false,
        int? displayId = null,
        CancellationToken cancellationToken = default);

    Task<AdbRuntimeQueryResult<AdbRawScreenshot>> DecodeRawScreenshotAsync(
        string adbPath,
        string serial,
        bool gzip = false,
        CancellationToken cancellationToken = default);

    Task<AdbScreenshotCaptureResult> CaptureBestScreenshotAsync(
        string adbPath,
        string serial,
        int? displayId = null,
        CancellationToken cancellationToken = default);
}
