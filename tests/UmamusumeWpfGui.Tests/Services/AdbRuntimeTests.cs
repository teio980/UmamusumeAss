using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class AdbRuntimeTests
{
    [Fact]
    public async Task ListDevicesParsesExtendedAdbRows()
    {
        var runner = new RecordingAdbRunner(new AdbCommandResult(
            "List of devices attached\nemulator-5554\tdevice product:sdk model:Pixel_8 device:emu transport_id:1\n",
            "", 0, false, null));
        var runtime = new AdbRuntime(runner);

        var result = await runtime.ListDevicesAsync("adb.exe");

        var device = Assert.Single(result.Devices);
        Assert.True(result.Succeeded);
        Assert.Equal("emulator-5554", device.Serial);
        Assert.Equal("Pixel_8", device.Model);
        Assert.Equal("emu", device.Device);
        Assert.Equal(["devices", "-l"], runner.Command);
    }

    [Fact]
    public async Task DeviceDiscoveryRetainsCommandFailureForManagementUi()
    {
        var failure = new AdbCommandResult("", "adb unavailable", 1, false, null);
        var result = await new AdbRuntime(new RecordingAdbRunner(failure)).ListDevicesAsync("adb.exe");

        Assert.False(result.Succeeded);
        Assert.Empty(result.Devices);
        Assert.Equal("adb unavailable", result.CommandResult.Stderr);
    }

    private sealed class RecordingAdbRunner(AdbCommandResult result) : IAdbRunner
    {
        public IReadOnlyList<string> Command { get; private set; } = [];

        public AdbCommandResult Run(string adbPath, IReadOnlyList<string> arguments)
        {
            Command = arguments;
            return result;
        }

        public Task<AdbCommandResult> RunAsync(
            string adbPath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Command = arguments;
            return Task.FromResult(result);
        }

        public (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error) RunDevices(string adbPath)
        {
            var captured = Run(adbPath, ["devices"]);
            return (captured.Stdout, captured.Stderr, captured.ExitCode, captured.TimedOut, captured.Error);
        }
    }
}
