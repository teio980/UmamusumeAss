namespace UmamusumeWpfGui.Models;




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
    AdbRawScreenshot? DecodedRaw = null)
{
    public FrameProvenance? SourceFrame { get; init; }
}

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
