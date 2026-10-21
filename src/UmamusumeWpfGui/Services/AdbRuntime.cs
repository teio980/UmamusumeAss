using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using ActivityKind = UmamusumeWpfGui.Models.ActivityKind;

namespace UmamusumeWpfGui.Services;

/// <summary>Provides ADB device discovery for connection and diagnostics UI.</summary>
public sealed class AdbRuntime : IAdbRuntime
{
    private readonly IAdbRunner _adbRunner;
    private readonly IActivityRegistry? _activityRegistry;

    public AdbRuntime(IAdbRunner adbRunner, IActivityRegistry? activityRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(adbRunner);
        _adbRunner = adbRunner;
        _activityRegistry = activityRegistry;
    }

    public async Task<AdbDeviceListResult> ListDevicesAsync(
        string adbPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbPath);
        using var activityLease = _activityRegistry?.Acquire(ActivityKind.NativeOperation);
        var result = await _adbRunner.RunAsync(adbPath, ["devices", "-l"], cancellationToken)
            .ConfigureAwait(false);
        return new AdbDeviceListResult(ParseDevices(result.Stdout), result);
    }

    private static List<AdbDeviceInfo> ParseDevices(string stdout)
    {
        var devices = new List<AdbDeviceInfo>();
        foreach (var rawLine in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.Equals("List of devices attached", StringComparison.OrdinalIgnoreCase))
                continue;

            var fields = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || fields[0].Equals("*", StringComparison.Ordinal))
                continue;

            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in fields.Skip(2))
            {
                var separator = field.IndexOf(':');
                if (separator > 0 && separator < field.Length - 1)
                    attributes[field[..separator]] = field[(separator + 1)..];
            }

            devices.Add(new AdbDeviceInfo(fields[0], fields[1], attributes));
        }

        return devices;
    }
}
