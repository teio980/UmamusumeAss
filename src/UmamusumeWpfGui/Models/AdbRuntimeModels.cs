namespace UmamusumeWpfGui.Models;

/// <summary>
/// A device row returned by <c>adb devices -l</c>.
/// </summary>
public sealed record AdbDeviceInfo(
    string Serial,
    string State,
    IReadOnlyDictionary<string, string> Attributes)
{
    public string? Product => GetAttribute("product");
    public string? Model => GetAttribute("model");
    public string? Device => GetAttribute("device");
    public string? TransportId => GetAttribute("transport_id");

    public bool IsReady => State.Equals("device", StringComparison.OrdinalIgnoreCase);

    private string? GetAttribute(string name) =>
        Attributes.TryGetValue(name, out var value) ? value : null;
}

public sealed record AdbScreenSize(int Width, int Height);

public enum AdbScreenshotMethod
{
    EncodedPng,
    EncodedPngWithShell,
    Raw,
    RawWithGzip
}

public sealed record AdbRawScreenshot(
    int Width,
    int Height,
    byte[] RgbaBytes);

public sealed record AdbScreenshotResult(
    AdbScreenshotMethod Method,
    byte[] Data,
    TimeSpan Duration,
    AdbRawScreenshot? DecodedRaw = null);

public sealed record AdbScreenshotCaptureResult(
    AdbScreenshotResult? Screenshot,
    IReadOnlyList<UmamusumeWpfGui.Helper.AdbBinaryCommandResult> Attempts)
{
    public bool Succeeded => Screenshot is not null;
}

public sealed record AdbDeviceProperties(
    string AndroidId,
    string AndroidVersion,
    string AbiList,
    bool BootCompleted,
    AdbScreenSize? ScreenSize);

public sealed record AdbRuntimeQueryResult<T>(
    T? Value,
    IReadOnlyList<UmamusumeWpfGui.Helper.AdbCommandResult> CommandResults)
{
    public bool Succeeded =>
        CommandResults.Count > 0 && CommandResults.All(IsSuccessful);

    public UmamusumeWpfGui.Helper.AdbCommandResult? FirstFailure =>
        CommandResults.FirstOrDefault(result => !IsSuccessful(result));

    private static bool IsSuccessful(UmamusumeWpfGui.Helper.AdbCommandResult result) =>
        result.Error is null && !result.TimedOut && result.ExitCode == 0;
}

public sealed record AdbDeviceListResult(
    IReadOnlyList<AdbDeviceInfo> Devices,
    UmamusumeWpfGui.Helper.AdbCommandResult CommandResult)
{
    public bool Succeeded =>
        CommandResult.Error is null
        && !CommandResult.TimedOut
        && CommandResult.ExitCode == 0;
}

/// <summary>
/// MAA-compatible defaults for the generic ADB input path. The extra swipe
/// is deliberately opt-in per call, because it is a workaround rather than
/// the semantic meaning of a swipe.
/// </summary>
public sealed record AdbRuntimeOptions(
    double SwipeDurationMultiplier,
    int ExtraSwipeDistance,
    int ExtraSwipeDurationMilliseconds)
{
    public static AdbRuntimeOptions Default { get; } = new(
        SwipeDurationMultiplier: 10.0,
        ExtraSwipeDistance: 100,
        ExtraSwipeDurationMilliseconds: 500);
}
