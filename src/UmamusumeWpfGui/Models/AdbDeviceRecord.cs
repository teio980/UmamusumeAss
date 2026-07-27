namespace UmamusumeWpfGui.Models;

/// <summary>
/// A single device record parsed from <c>adb devices</c> output.
/// The <see cref="State"/> is the exact second column from the tab-separated output.
/// </summary>
public sealed record AdbDeviceRecord(string Serial, string State);
