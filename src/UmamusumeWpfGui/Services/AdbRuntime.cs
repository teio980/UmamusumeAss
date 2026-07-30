using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// The common ADB runtime path used by the UI and, later, the task engine.
/// It follows MAA's device-scoped command shape while keeping every argument
/// as an individual token so paths, serials, and user input are not manually
/// concatenated into a shell command.
/// </summary>
public sealed class AdbRuntime : IAdbRuntime
{
    private static readonly Regex ScreenSizeRegex = new(
        @"(?:(?<kind>Physical|Override)\s+size:|size:)\s*(?<width>\d+)\s*x\s*(?<height>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PackageVersionRegex = new(
        @"(?:versionName=)(?<version>[^\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OrientationRegex = new(
        @"(?:SurfaceOrientation|mSurfaceOrientation)[^0-9]*(?<orientation>[0-3])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InputEventRegex = new(
        @"/dev/input/event(?<event>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IAdbRunner _adbRunner;
    private readonly IAsyncDelay _asyncDelay;
    private readonly AdbRuntimeOptions _options;
    private readonly ConcurrentDictionary<string, AdbScreenshotMethod> _screenshotMethods = new();

    public AdbRuntime(
        IAdbRunner adbRunner,
        IAsyncDelay asyncDelay)
    {
        ArgumentNullException.ThrowIfNull(adbRunner);
        ArgumentNullException.ThrowIfNull(asyncDelay);
        _adbRunner = adbRunner;
        _asyncDelay = asyncDelay;
        _options = AdbRuntimeOptions.Default;
    }

    public async Task<AdbDeviceListResult> ListDevicesAsync(
        string adbPath,
        CancellationToken cancellationToken = default)
    {
        var result = await _adbRunner.RunAsync(
            RequireAdbPath(adbPath),
            ["devices", "-l"],
            cancellationToken).ConfigureAwait(false);
        return new AdbDeviceListResult(ParseDevices(result.Stdout), result);
    }

    public async Task<AdbDeviceListResult> WaitForDeviceAsync(
        string adbPath,
        string serial,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default)
    {
        RequireSerial(serial);
        var safeTimeout = NonNegative(timeout);
        var safePollInterval = NonNegative(pollInterval);
        var started = Stopwatch.GetTimestamp();
        AdbDeviceListResult? latest = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            latest = await ListDevicesAsync(adbPath, cancellationToken).ConfigureAwait(false);
            if (latest.Devices.Any(device =>
                    device.Serial.Equals(serial, StringComparison.OrdinalIgnoreCase)
                    && device.IsReady))
            {
                return latest;
            }

            if (Stopwatch.GetElapsedTime(started) >= safeTimeout)
            {
                return latest;
            }

            await _asyncDelay.DelayAsync(safePollInterval, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public Task<AdbCommandResult> ConnectAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default) =>
        RunAsync(adbPath, ["connect", RequireSerial(serial)], cancellationToken);

    public Task<AdbCommandResult> DisconnectAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default) =>
        RunAsync(adbPath, ["disconnect", RequireSerial(serial)], cancellationToken);

    public async Task<AdbRuntimeQueryResult<string>> GetStateAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            adbPath,
            ["-s", RequireSerial(serial), "get-state"],
            cancellationToken).ConfigureAwait(false);
        var state = result.Stdout.Trim();
        return new AdbRuntimeQueryResult<string>(
            string.IsNullOrWhiteSpace(state) ? null : state,
            [result]);
    }

    public async Task<AdbRuntimeQueryResult<bool>> IsBootCompletedAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default)
    {
        var result = await RunShellAsync(
            adbPath, serial, ["getprop", "sys.boot_completed"], cancellationToken)
            .ConfigureAwait(false);
        return new AdbRuntimeQueryResult<bool>(result.Stdout.Trim() == "1", [result]);
    }

    public async Task<AdbRuntimeQueryResult<int>> GetOrientationAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default)
    {
        var result = await RunShellAsync(
            adbPath, serial, ["dumpsys", "input"], cancellationToken).ConfigureAwait(false);
        var match = OrientationRegex.Match(result.Stdout);
        var orientation = match.Success
            && int.TryParse(match.Groups["orientation"].Value, out var parsed)
            ? parsed
            : -1;
        return new AdbRuntimeQueryResult<int>(orientation, [result]);
    }

    public async Task<AdbRuntimeQueryResult<string>> GetDisplayIdAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        RequirePackageName(packageName);
        var result = await RunShellAsync(
            adbPath, serial, ["dumpsys", "activity", "activities"], cancellationToken)
            .ConfigureAwait(false);
        var displayId = FindDisplayId(result.Stdout, packageName);
        return new AdbRuntimeQueryResult<string>(displayId, [result]);
    }

    public async Task<AdbRuntimeQueryResult<string>> GetInputEventIdAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default)
    {
        var result = await RunShellAsync(
            adbPath, serial, ["getevent", "-pl"], cancellationToken).ConfigureAwait(false);
        var match = InputEventRegex.Match(result.Stdout);
        return new AdbRuntimeQueryResult<string>(
            match.Success ? match.Groups["event"].Value : null,
            [result]);
    }

    public async Task<AdbRuntimeQueryResult<double>> GetRefreshRateAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default)
    {
        var result = await RunShellAsync(
            adbPath,
            serial,
            ["dumpsys", "SurfaceFlinger", "--latency"],
            cancellationToken).ConfigureAwait(false);
        var firstLine = result.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        var digits = firstLine is null
            ? string.Empty
            : new string(firstLine.Where(char.IsDigit).ToArray());
        var refreshRate = long.TryParse(digits, out var periodNanoseconds)
            && periodNanoseconds > 0
            ? 1_000_000_000d / periodNanoseconds
            : 0d;
        return new AdbRuntimeQueryResult<double>(refreshRate, [result]);
    }

    public async Task<AdbRuntimeQueryResult<bool>> WaitForBootCompletedAsync(
        string adbPath,
        string serial,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        AdbRuntimeQueryResult<bool>? latest = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            latest = await IsBootCompletedAsync(adbPath, serial, cancellationToken)
                .ConfigureAwait(false);
            if (latest.Value == true)
            {
                return latest;
            }

            if (Stopwatch.GetElapsedTime(started) >= NonNegative(timeout))
            {
                return latest;
            }

            await _asyncDelay.DelayAsync(NonNegative(pollInterval), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public Task<AdbCommandResult> ShellAsync(
        string adbPath,
        string serial,
        IReadOnlyList<string> shellArguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shellArguments);
        if (shellArguments.Count == 0)
        {
            throw new ArgumentException("At least one shell argument is required.", nameof(shellArguments));
        }

        return RunShellAsync(adbPath, serial, shellArguments, cancellationToken);
    }

    public Task<AdbCommandResult> TapAsync(
        string adbPath,
        string serial,
        int x,
        int y,
        int? displayId = null,
        CancellationToken cancellationToken = default)
    {
        var inputArguments = CreateInputArguments(
            displayId,
            "tap",
            x.ToString(CultureInfo.InvariantCulture),
            y.ToString(CultureInfo.InvariantCulture));
        return RunShellAsync(adbPath, serial, inputArguments, cancellationToken);
    }

    public async Task<AdbCommandResult> SwipeAsync(
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
        CancellationToken cancellationToken = default)
    {
        var effectiveOptions = options ?? _options;
        var duration = durationMilliseconds <= 0
            ? 0
            : Math.Max(1, (int)(durationMilliseconds * effectiveOptions.SwipeDurationMultiplier));

        var primaryArguments = CreateSwipeArguments(
            displayId, startX, startY, endX, endY, duration);
        var primary = await RunShellAsync(
            adbPath, serial, primaryArguments, cancellationToken).ConfigureAwait(false);

        if (!extraSwipe || effectiveOptions.ExtraSwipeDurationMilliseconds <= 0)
        {
            return primary;
        }

        var extraArguments = CreateSwipeArguments(
            displayId,
            endX,
            endY,
            endX,
            endY - effectiveOptions.ExtraSwipeDistance,
            effectiveOptions.ExtraSwipeDurationMilliseconds);
        var extra = await RunShellAsync(
            adbPath, serial, extraArguments, cancellationToken).ConfigureAwait(false);
        return Combine(primary, extra);
    }

    public Task<AdbCommandResult> InputTextAsync(
        string adbPath,
        string serial,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return RunShellAsync(
            adbPath,
            serial,
            ["input", "text", EscapeInputText(text)],
            cancellationToken);
    }

    public Task<AdbCommandResult> KeyEventAsync(
        string adbPath,
        string serial,
        string keyCode,
        int? displayId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyCode);
        return RunShellAsync(
            adbPath,
            serial,
            CreateInputArguments(displayId, "keyevent", keyCode),
            cancellationToken);
    }

    public Task<AdbCommandResult> BackAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default) =>
        KeyEventAsync(adbPath, serial, "KEYCODE_BACK", cancellationToken: cancellationToken);

    public Task<AdbCommandResult> HomeAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default) =>
        KeyEventAsync(adbPath, serial, "KEYCODE_HOME", cancellationToken: cancellationToken);

    public Task<AdbCommandResult> PressEscapeAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default) =>
        KeyEventAsync(adbPath, serial, "111", cancellationToken: cancellationToken);

    public Task<AdbCommandResult> StartPackageAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        RequirePackageName(packageName);
        // monkey works without knowing the package's launcher activity. A
        // caller that knows the exact Unity activity can use StartActivityAsync.
        return RunShellAsync(
            adbPath,
            serial,
            ["monkey", "-p", packageName, "1"],
            cancellationToken);
    }

    public Task<AdbCommandResult> StartActivityAsync(
        string adbPath,
        string serial,
        string packageName,
        string activityName,
        CancellationToken cancellationToken = default)
    {
        RequirePackageName(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(activityName);
        var component = activityName.Contains('/', StringComparison.Ordinal)
            ? activityName
            : $"{packageName}/{activityName}";
        return RunShellAsync(
            adbPath,
            serial,
            ["am", "start", "-n", component],
            cancellationToken);
    }

    public Task<AdbCommandResult> StopPackageAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        RequirePackageName(packageName);
        return RunShellAsync(
            adbPath,
            serial,
            ["am", "force-stop", packageName],
            cancellationToken);
    }

    public async Task<AdbRuntimeQueryResult<bool>> IsPackageRunningAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        RequirePackageName(packageName);
        var pidOf = await RunShellAsync(
            adbPath, serial, ["pidof", packageName], cancellationToken).ConfigureAwait(false);
        if (IsSuccessful(pidOf))
        {
            return new AdbRuntimeQueryResult<bool>(
                !string.IsNullOrWhiteSpace(pidOf.Stdout),
                [pidOf]);
        }

        // Some Android images do not ship pidof. The ps fallback mirrors the
        // compatibility behavior needed across emulator families.
        var ps = await RunShellAsync(
            adbPath, serial, ["ps", "-A"], cancellationToken).ConfigureAwait(false);
        var running = IsSuccessful(ps)
            && ps.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(line => ContainsProcessName(line, packageName));
        return new AdbRuntimeQueryResult<bool>(running, [pidOf, ps]);
    }

    public async Task<AdbRuntimeQueryResult<IReadOnlyList<string>>> ListPackagesAsync(
        string adbPath,
        string serial,
        string? packageNameFilter = null,
        CancellationToken cancellationToken = default)
    {
        if (packageNameFilter is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(packageNameFilter);
        }

        var arguments = new List<string> { "pm", "list", "packages" };
        if (packageNameFilter is not null)
        {
            arguments.Add(packageNameFilter);
        }

        var result = await RunShellAsync(
            adbPath, serial, arguments, cancellationToken).ConfigureAwait(false);
        var packages = result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimEnd('\r'))
            .Where(line => line.StartsWith("package:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["package:".Length..].Trim())
            .Where(package => package.Length > 0)
            .ToList();
        return new AdbRuntimeQueryResult<IReadOnlyList<string>>(packages, [result]);
    }

    public async Task<AdbRuntimeQueryResult<string>> GetPackageVersionAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        RequirePackageName(packageName);
        var result = await RunShellAsync(
            adbPath, serial, ["dumpsys", "package", packageName], cancellationToken)
            .ConfigureAwait(false);
        var version = PackageVersionRegex.Match(result.Stdout).Groups["version"].Value;
        return new AdbRuntimeQueryResult<string>(
            string.IsNullOrWhiteSpace(version) ? null : version,
            [result]);
    }

    public Task<AdbCommandResult> PushAsync(
        string adbPath,
        string serial,
        string localPath,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        return RunAsync(
            adbPath,
            ["-s", RequireSerial(serial), "push", localPath, remotePath],
            cancellationToken);
    }

    public Task<AdbCommandResult> PullAsync(
        string adbPath,
        string serial,
        string remotePath,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        return RunAsync(
            adbPath,
            ["-s", RequireSerial(serial), "pull", remotePath, localPath],
            cancellationToken);
    }

    public Task<AdbCommandResult> RemoveAsync(
        string adbPath,
        string serial,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        return RunShellAsync(
            adbPath,
            serial,
            ["rm", "-f", remotePath],
            cancellationToken);
    }

    public Task<AdbCommandResult> InstallApkAsync(
        string adbPath,
        string serial,
        string apkPath,
        bool replaceExisting = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apkPath);
        var arguments = new List<string> { "-s", RequireSerial(serial), "install" };
        if (replaceExisting)
        {
            arguments.Add("-r");
        }

        arguments.Add(apkPath);
        return RunAsync(adbPath, arguments, cancellationToken);
    }

    public Task<AdbCommandResult> UninstallPackageAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        RequirePackageName(packageName);
        return RunAsync(
            adbPath,
            ["-s", RequireSerial(serial), "uninstall", packageName],
            cancellationToken);
    }

    public Task<AdbCommandResult> ClearPackageDataAsync(
        string adbPath,
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        RequirePackageName(packageName);
        return RunShellAsync(
            adbPath, serial, ["pm", "clear", packageName], cancellationToken);
    }

    public Task<AdbCommandResult> RebootAsync(
        string adbPath,
        string serial,
        string? mode = null,
        CancellationToken cancellationToken = default)
    {
        if (mode is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        }

        var arguments = new List<string> { "-s", RequireSerial(serial), "reboot" };
        if (mode is not null)
        {
            arguments.Add(mode);
        }

        return RunAsync(adbPath, arguments, cancellationToken);
    }

    public Task<AdbCommandResult> RootAsync(
        string adbPath,
        CancellationToken cancellationToken = default) =>
        RunAsync(adbPath, ["root"], cancellationToken);

    public Task<AdbCommandResult> UnrootAsync(
        string adbPath,
        CancellationToken cancellationToken = default) =>
        RunAsync(adbPath, ["unroot"], cancellationToken);

    public Task<AdbCommandResult> RemountAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default) =>
        RunAsync(adbPath, ["-s", RequireSerial(serial), "remount"], cancellationToken);

    public Task<AdbCommandResult> KillServerAsync(
        string adbPath,
        CancellationToken cancellationToken = default) =>
        RunAsync(adbPath, ["kill-server"], cancellationToken);

    public async Task<AdbRuntimeQueryResult<AdbScreenSize>> GetScreenSizeAsync(
        string adbPath,
        string serial,
        int? displayId = null,
        CancellationToken cancellationToken = default)
    {
        var arguments = displayId is int id
            ? new List<string> { "wm", "size", "-d", id.ToString(CultureInfo.InvariantCulture) }
            : ["wm", "size"];
        var result = await RunShellAsync(
            adbPath, serial, arguments, cancellationToken).ConfigureAwait(false);
        return new AdbRuntimeQueryResult<AdbScreenSize>(
            TryParseScreenSize(result.Stdout),
            [result]);
    }

    public async Task<AdbRuntimeQueryResult<AdbDeviceProperties>> GetDevicePropertiesAsync(
        string adbPath,
        string serial,
        CancellationToken cancellationToken = default)
    {
        var androidId = await RunShellAsync(
            adbPath, serial, ["settings", "get", "secure", "android_id"], cancellationToken)
            .ConfigureAwait(false);
        var androidVersion = await RunShellAsync(
            adbPath, serial, ["getprop", "ro.build.version.release"], cancellationToken)
            .ConfigureAwait(false);
        var abiList = await RunShellAsync(
            adbPath, serial, ["getprop", "ro.product.cpu.abilist"], cancellationToken)
            .ConfigureAwait(false);
        var bootCompleted = await RunShellAsync(
            adbPath, serial, ["getprop", "sys.boot_completed"], cancellationToken)
            .ConfigureAwait(false);
        var screen = await GetScreenSizeAsync(
            adbPath, serial, cancellationToken: cancellationToken).ConfigureAwait(false);

        var commands = new[]
        {
            androidId,
            androidVersion,
            abiList,
            bootCompleted,
            screen.CommandResults[0]
        };
        var properties = new AdbDeviceProperties(
            androidId.Stdout.Trim(),
            androidVersion.Stdout.Trim(),
            abiList.Stdout.Trim(),
            bootCompleted.Stdout.Trim() == "1",
            screen.Value);
        return new AdbRuntimeQueryResult<AdbDeviceProperties>(properties, commands);
    }

    public async Task<AdbBinaryCommandResult> CaptureScreenshotAsync(
        string adbPath,
        string serial,
        int? displayId = null,
        CancellationToken cancellationToken = default)
    {
        var screencapArguments = displayId is int id
            ? new List<string>
            {
                "-s", RequireSerial(serial), "exec-out", "screencap", "-d",
                id.ToString(CultureInfo.InvariantCulture), "-p"
            }
            : ["-s", RequireSerial(serial), "exec-out", "screencap", "-p"];
        var direct = await _adbRunner.RunBinaryAsync(
            RequireAdbPath(adbPath), screencapArguments, cancellationToken).ConfigureAwait(false);
        if (IsSuccessful(direct))
        {
            return direct;
        }

        // MAA has a CapWithShell profile for emulators where exec-out is not
        // available. Keep this as a binary operation so PNG bytes are never
        // converted through a text encoding.
        var shellArguments = displayId is int shellDisplayId
            ? new List<string>
            {
                "-s", RequireSerial(serial), "shell", "screencap", "-d",
                shellDisplayId.ToString(CultureInfo.InvariantCulture), "-p"
            }
            : ["-s", RequireSerial(serial), "shell", "screencap", "-p"];
        var fallback = await _adbRunner.RunBinaryAsync(
            RequireAdbPath(adbPath), shellArguments, cancellationToken).ConfigureAwait(false);
        return IsSuccessful(fallback) ? fallback : Combine(direct, fallback);
    }

    public Task<AdbBinaryCommandResult> CaptureRawScreenshotAsync(
        string adbPath,
        string serial,
        bool gzip = false,
        int? displayId = null,
        CancellationToken cancellationToken = default)
    {
        var serialToken = RequireSerial(serial);
        var command = displayId is int id
            ? $"screencap -d {id.ToString(CultureInfo.InvariantCulture)}"
            : "screencap";
        if (gzip)
        {
            command += " | gzip -1";
        }

        IReadOnlyList<string> arguments;
        if (gzip)
        {
            arguments = ["-s", serialToken, "shell", "sh", "-c", command];
        }
        else if (displayId is int rawDisplayId)
        {
            arguments = [
                "-s", serialToken, "exec-out", "screencap", "-d",
                rawDisplayId.ToString(CultureInfo.InvariantCulture)
            ];
        }
        else
        {
            arguments = ["-s", serialToken, "exec-out", "screencap"];
        }
        return _adbRunner.RunBinaryAsync(
            RequireAdbPath(adbPath), arguments, cancellationToken);
    }

    public async Task<AdbRuntimeQueryResult<AdbRawScreenshot>> DecodeRawScreenshotAsync(
        string adbPath,
        string serial,
        bool gzip = false,
        CancellationToken cancellationToken = default)
    {
        var result = await CaptureRawScreenshotAsync(
            adbPath, serial, gzip, cancellationToken: cancellationToken).ConfigureAwait(false);
        var decoded = IsSuccessful(result)
            && AdbScreenshotCodec.TryDecodeRaw(result.Stdout, gzip, out var screenshot)
            ? screenshot
            : null;
        var commandResult = new AdbCommandResult(
            string.Empty,
            result.Stderr,
            result.ExitCode,
            result.TimedOut,
            result.Error);
        return new AdbRuntimeQueryResult<AdbRawScreenshot>(decoded, [commandResult]);
    }

    public async Task<AdbScreenshotCaptureResult> CaptureBestScreenshotAsync(
        string adbPath,
        string serial,
        int? displayId = null,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{adbPath}\n{serial}\n{displayId?.ToString(CultureInfo.InvariantCulture) ?? "default"}";
        var attempts = new List<AdbBinaryCommandResult>();

        if (_screenshotMethods.TryGetValue(cacheKey, out var cachedMethod))
        {
            var cached = await CaptureScreenshotMethodAsync(
                adbPath, serial, displayId, cachedMethod, cancellationToken).ConfigureAwait(false);
            attempts.Add(cached.CommandResult);
            if (cached.Screenshot is not null)
            {
                return new AdbScreenshotCaptureResult(cached.Screenshot, attempts);
            }

            _screenshotMethods.TryRemove(cacheKey, out _);
        }

        // MAA probes multiple methods once and then keeps the fastest one.
        // The NC socket and emulator vendor extras remain separate optional
        // backends; these four methods cover the portable ADB paths.
        var methods = new[]
        {
            AdbScreenshotMethod.Raw,
            AdbScreenshotMethod.RawWithGzip,
            AdbScreenshotMethod.EncodedPng,
            AdbScreenshotMethod.EncodedPngWithShell
        };
        var successful = new List<(AdbScreenshotMethod Method, AdbScreenshotResult Screenshot)>();
        foreach (var method in methods)
        {
            var captured = await CaptureScreenshotMethodAsync(
                adbPath, serial, displayId, method, cancellationToken).ConfigureAwait(false);
            attempts.Add(captured.CommandResult);
            if (captured.Screenshot is not null)
            {
                successful.Add((method, captured.Screenshot));
            }
        }

        if (successful.Count == 0)
        {
            return new AdbScreenshotCaptureResult(null, attempts);
        }

        var fastest = successful.MinBy(item => item.Screenshot.Duration);
        _screenshotMethods[cacheKey] = fastest.Method;
        return new AdbScreenshotCaptureResult(fastest.Screenshot, attempts);
    }

    private async Task<(AdbScreenshotResult? Screenshot, AdbBinaryCommandResult CommandResult)>
        CaptureScreenshotMethodAsync(
            string adbPath,
            string serial,
            int? displayId,
            AdbScreenshotMethod method,
            CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var serialToken = RequireSerial(serial);
        IReadOnlyList<string> arguments;
        switch (method)
        {
            case AdbScreenshotMethod.Raw:
                arguments = displayId is int rawDisplayId
                    ? ["-s", serialToken, "exec-out", "screencap", "-d", rawDisplayId.ToString(CultureInfo.InvariantCulture)]
                    : ["-s", serialToken, "exec-out", "screencap"];
                break;
            case AdbScreenshotMethod.RawWithGzip:
                arguments = displayId is int gzipDisplayId
                    ? ["-s", serialToken, "shell", "sh", "-c", $"screencap -d {gzipDisplayId.ToString(CultureInfo.InvariantCulture)} | gzip -1"]
                    : ["-s", serialToken, "shell", "sh", "-c", "screencap | gzip -1"];
                break;
            case AdbScreenshotMethod.EncodedPng:
                arguments = displayId is int pngDisplayId
                    ? ["-s", serialToken, "exec-out", "screencap", "-d", pngDisplayId.ToString(CultureInfo.InvariantCulture), "-p"]
                    : ["-s", serialToken, "exec-out", "screencap", "-p"];
                break;
            case AdbScreenshotMethod.EncodedPngWithShell:
                arguments = displayId is int shellDisplayId
                    ? ["-s", serialToken, "shell", "screencap", "-d", shellDisplayId.ToString(CultureInfo.InvariantCulture), "-p"]
                    : ["-s", serialToken, "shell", "screencap", "-p"];
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(method));
        }

        var commandResult = await _adbRunner.RunBinaryAsync(
            RequireAdbPath(adbPath), arguments, cancellationToken).ConfigureAwait(false);
        if (!IsSuccessful(commandResult))
        {
            return (null, commandResult);
        }

        AdbRawScreenshot? decoded = null;
        if (method is AdbScreenshotMethod.Raw or AdbScreenshotMethod.RawWithGzip
            && !AdbScreenshotCodec.TryDecodeRaw(
                commandResult.Stdout,
                method == AdbScreenshotMethod.RawWithGzip,
                out decoded))
        {
            return (null, commandResult);
        }

        return (
            new AdbScreenshotResult(
                method,
                commandResult.Stdout,
                Stopwatch.GetElapsedTime(started),
                decoded),
            commandResult);
    }

    private Task<AdbCommandResult> RunAsync(
        string adbPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        _adbRunner.RunAsync(RequireAdbPath(adbPath), arguments, cancellationToken);

    private Task<AdbCommandResult> RunShellAsync(
        string adbPath,
        string serial,
        IReadOnlyList<string> shellArguments,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>(shellArguments.Count + 3)
        {
            "-s",
            RequireSerial(serial),
            "shell"
        };
        arguments.AddRange(shellArguments);
        return RunAsync(adbPath, arguments, cancellationToken);
    }

    private static List<string> CreateInputArguments(
        int? displayId,
        params string[] arguments)
    {
        var result = new List<string> { "input" };
        if (displayId is int id)
        {
            result.Add("-d");
            result.Add(id.ToString(CultureInfo.InvariantCulture));
        }

        result.AddRange(arguments);
        return result;
    }

    private static List<string> CreateSwipeArguments(
        int? displayId,
        int startX,
        int startY,
        int endX,
        int endY,
        int durationMilliseconds)
    {
        var result = CreateInputArguments(
            displayId,
            "swipe",
            startX.ToString(CultureInfo.InvariantCulture),
            startY.ToString(CultureInfo.InvariantCulture),
            endX.ToString(CultureInfo.InvariantCulture),
            endY.ToString(CultureInfo.InvariantCulture));
        if (durationMilliseconds > 0)
        {
            result.Add(durationMilliseconds.ToString(CultureInfo.InvariantCulture));
        }

        return result;
    }

    private static List<AdbDeviceInfo> ParseDevices(string stdout)
    {
        var devices = new List<AdbDeviceInfo>();
        foreach (var rawLine in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.Equals("List of devices attached", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fields = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || fields[0].Equals("*", StringComparison.Ordinal))
            {
                continue;
            }

            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in fields.Skip(2))
            {
                var separator = field.IndexOf(':');
                if (separator > 0 && separator < field.Length - 1)
                {
                    attributes[field[..separator]] = field[(separator + 1)..];
                }
            }

            devices.Add(new AdbDeviceInfo(fields[0], fields[1], attributes));
        }

        return devices;
    }

    private static AdbScreenSize? TryParseScreenSize(string stdout)
    {
        Match? physical = null;
        Match? fallback = null;
        foreach (Match match in ScreenSizeRegex.Matches(stdout))
        {
            if (match.Groups["kind"].Value.Equals("Override", StringComparison.OrdinalIgnoreCase))
            {
                fallback = match;
            }
            else if (match.Groups["kind"].Value.Equals("Physical", StringComparison.OrdinalIgnoreCase))
            {
                physical = match;
            }
            else
            {
                fallback = match;
            }
        }

        var selected = fallback ?? physical;
        return selected is null
            || !int.TryParse(selected.Groups["width"].Value, out var width)
            || !int.TryParse(selected.Groups["height"].Value, out var height)
            ? null
            : new AdbScreenSize(width, height);
    }

    private static bool ContainsProcessName(string line, string packageName)
    {
        var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return fields.Any(field => field.Equals(packageName, StringComparison.Ordinal));
    }

    private static string? FindDisplayId(string output, string packageName)
    {
        var pattern = $"Display #(?<id>\\d+)[\\s\\S]{{0,2000}}{Regex.Escape(packageName)}";
        var match = Regex.Match(output, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static string EscapeInputText(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            switch (character)
            {
                case ' ':
                    builder.Append("%s");
                    break;
                case '\\':
                case '%':
                case '&':
                case '|':
                case ';':
                case '<':
                case '>':
                case '(':
                case ')':
                case '\'':
                case '"':
                case '`':
                case '$':
                case '*':
                case '?':
                case '~':
                case '#':
                    builder.Append('\\');
                    builder.Append(character);
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    private static AdbCommandResult Combine(
        AdbCommandResult first,
        AdbCommandResult second)
    {
        var stderr = string.Join(
            Environment.NewLine,
            new[] { first.Stderr, second.Stderr }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new AdbCommandResult(
            string.Join(Environment.NewLine, first.Stdout, second.Stdout),
            stderr,
            IsSuccessful(first) && IsSuccessful(second) ? 0 : second.ExitCode,
            first.TimedOut || second.TimedOut,
            first.Error ?? second.Error);
    }

    private static AdbBinaryCommandResult Combine(
        AdbBinaryCommandResult first,
        AdbBinaryCommandResult second) =>
        new(
            second.Stdout.Length > 0 ? second.Stdout : first.Stdout,
            string.Join(
                Environment.NewLine,
                new[] { first.Stderr, second.Stderr }.Where(value => !string.IsNullOrWhiteSpace(value))),
            IsSuccessful(first) && IsSuccessful(second) ? 0 : second.ExitCode,
            first.TimedOut || second.TimedOut,
            first.Error ?? second.Error);

    private static bool IsSuccessful(AdbCommandResult result) =>
        result.Error is null && !result.TimedOut && result.ExitCode == 0;

    private static bool IsSuccessful(AdbBinaryCommandResult result) =>
        result.Error is null && !result.TimedOut && result.ExitCode == 0;

    private static TimeSpan NonNegative(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static string RequireAdbPath(string adbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbPath);
        return adbPath;
    }

    private static string RequireSerial(string serial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        return serial;
    }

    private static void RequirePackageName(string packageName) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
}
