using System.Buffers.Binary;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class AdbRuntimeTests
{
    [Fact]
    public async Task ListDevicesParsesExtendedAdbRows()
    {
        var runner = new RecordingAdbRunner([
            new AdbCommandResult(
                "List of devices attached\nemulator-5554\tdevice product:sdk model:Pixel_8 device:emu transport_id:1\n",
                "",
                0,
                false,
                null)
        ]);
        var runtime = CreateRuntime(runner);

        var result = await runtime.ListDevicesAsync("adb.exe");

        var device = Assert.Single(result.Devices);
        Assert.True(result.Succeeded);
        Assert.Equal("emulator-5554", device.Serial);
        Assert.Equal("Pixel_8", device.Model);
        Assert.Equal("emu", device.Device);
        Assert.Equal(["devices", "-l"], runner.Commands[0]);
    }

    [Fact]
    public async Task SwipeUsesMaaDurationMultiplierAndOptionalExtraSwipe()
    {
        var runner = new RecordingAdbRunner([
            SuccessfulCommand(),
            SuccessfulCommand()
        ]);
        var runtime = CreateRuntime(runner);

        var result = await runtime.SwipeAsync(
            "adb.exe",
            "emulator-5554",
            10,
            20,
            100,
            200,
            100,
            extraSwipe: true);

        Assert.True(result.ExitCode == 0);
        Assert.Equal(
            ["-s", "emulator-5554", "shell", "input", "swipe", "10", "20", "100", "200", "1000"],
            runner.Commands[0]);
        Assert.Equal(
            ["-s", "emulator-5554", "shell", "input", "swipe", "100", "200", "100", "100", "500"],
            runner.Commands[1]);
    }

    [Fact]
    public async Task InputTextEscapesAndroidInputSpecialCharacters()
    {
        var runner = new RecordingAdbRunner([SuccessfulCommand()]);
        var runtime = CreateRuntime(runner);

        await runtime.InputTextAsync("adb.exe", "device", "hello world&42");

        Assert.Equal(
            ["-s", "device", "shell", "input", "text", "hello%sworld\\&42"],
            runner.Commands[0]);
    }

    [Fact]
    public async Task ScreenshotFallsBackFromExecOutToShell()
    {
        var runner = new RecordingAdbRunner(
            commandResults: [],
            binaryResults: [
                new AdbBinaryCommandResult([], "exec-out unsupported", 1, false, null),
                new AdbBinaryCommandResult([1, 2, 3], "", 0, false, null)
            ]);
        var runtime = CreateRuntime(runner);

        var result = await runtime.CaptureScreenshotAsync("adb.exe", "device");

        Assert.Equal([1, 2, 3], result.Stdout);
        Assert.Equal(
            ["-s", "device", "exec-out", "screencap", "-p"],
            runner.BinaryCommands[0]);
        Assert.Equal(
            ["-s", "device", "shell", "screencap", "-p"],
            runner.BinaryCommands[1]);
    }

    [Fact]
    public void RawScreenshotCodecDecodesAndroidHeaderAndPixels()
    {
        var raw = new byte[12 + 2 * 1 * 4];
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0, 4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(4, 4), 1);
        raw[^1] = 255;

        var decoded = AdbScreenshotCodec.TryDecodeRaw(raw, gzip: false, out var screenshot);

        Assert.True(decoded);
        Assert.NotNull(screenshot);
        Assert.Equal(new AdbScreenSize(2, 1), new AdbScreenSize(screenshot!.Width, screenshot.Height));
        Assert.Equal(8, screenshot.RgbaBytes.Length);
    }

    [Fact]
    public async Task DevicePropertiesUsesMaaConnectionProbeCommands()
    {
        var runner = new RecordingAdbRunner([
            new AdbCommandResult("abc123", "", 0, false, null),
            new AdbCommandResult("35", "", 0, false, null),
            new AdbCommandResult("arm64-v8a,x86_64", "", 0, false, null),
            new AdbCommandResult("1", "", 0, false, null),
            new AdbCommandResult("Physical size: 1080x1920\nOverride size: 720x1280", "", 0, false, null)
        ]);
        var runtime = CreateRuntime(runner);

        var result = await runtime.GetDevicePropertiesAsync("adb.exe", "device");

        Assert.True(result.Succeeded);
        Assert.Equal("abc123", result.Value!.AndroidId);
        Assert.Equal("35", result.Value.AndroidVersion);
        Assert.Equal(new AdbScreenSize(720, 1280), result.Value.ScreenSize);
        Assert.Equal(["-s", "device", "shell", "settings", "get", "secure", "android_id"], runner.Commands[0]);
        Assert.Equal(["-s", "device", "shell", "wm", "size"], runner.Commands[4]);
    }

    [Fact]
    public async Task TouchRuntimeStartsInteractiveProtocolAndScalesCoordinates()
    {
        var interactive = new FakeInteractiveSession("$\n^ 10 1000 2000 255\n");
        var runner = new RecordingAdbRunner(
            commandResults: [SuccessfulCommand(), SuccessfulCommand()],
            interactiveSession: interactive);
        var runtime = new AdbTouchRuntime(runner);

        var started = await runtime.StartAsync(
            "adb.exe",
            "device",
            "minitouch",
            "/data/local/tmp/uma-touch",
            screenWidth: 500,
            screenHeight: 1000);

        Assert.True(started.Succeeded);
        var result = await started.Session!.TapAsync(250, 500);
        Assert.True(result.Succeeded);
        Assert.Contains("d 0 500 1000 255", interactive.Writes[0]);
        Assert.Equal(
            ["-s", "device", "shell", "chmod", "700", "/data/local/tmp/uma-touch"],
            runner.Commands[1]);
        await started.Session.DisposeAsync();
    }

    private static AdbRuntime CreateRuntime(RecordingAdbRunner runner) =>
        new(runner, new ImmediateDelay());

    private static AdbCommandResult SuccessfulCommand() =>
        new("", "", 0, false, null);

    private sealed class RecordingAdbRunner : IAdbRunner
    {
        private readonly Queue<AdbCommandResult> _commandResults;
        private readonly Queue<AdbBinaryCommandResult> _binaryResults;
        private readonly IAdbInteractiveSession? _interactiveSession;

        public RecordingAdbRunner(
            IEnumerable<AdbCommandResult> commandResults,
            IEnumerable<AdbBinaryCommandResult>? binaryResults = null,
            IAdbInteractiveSession? interactiveSession = null)
        {
            _commandResults = new(commandResults);
            _binaryResults = new(binaryResults ?? []);
            _interactiveSession = interactiveSession;
        }

        public List<IReadOnlyList<string>> Commands { get; } = [];
        public List<IReadOnlyList<string>> BinaryCommands { get; } = [];

        public AdbCommandResult Run(string adbPath, IReadOnlyList<string> arguments) =>
            DequeueCommand(arguments);

        public Task<AdbCommandResult> RunAsync(
            string adbPath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DequeueCommand(arguments));
        }

        public Task<AdbBinaryCommandResult> RunBinaryAsync(
            string adbPath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BinaryCommands.Add(arguments);
            return Task.FromResult(_binaryResults.Dequeue());
        }

        public Task<AdbInteractiveSessionStartResult> StartInteractiveAsync(
            string adbPath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(arguments);
            return Task.FromResult(new AdbInteractiveSessionStartResult(_interactiveSession, null));
        }

        public (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error) RunDevices(string adbPath)
        {
            var result = DequeueCommand(["devices"]);
            return (result.Stdout, result.Stderr, result.ExitCode, result.TimedOut, result.Error);
        }

        private AdbCommandResult DequeueCommand(IReadOnlyList<string> arguments)
        {
            Commands.Add(arguments);
            return _commandResults.Dequeue();
        }
    }

    private sealed class FakeInteractiveSession : IAdbInteractiveSession
    {
        private readonly Queue<string> _reads;

        public FakeInteractiveSession(params string[] reads) => _reads = new(reads);

        public bool HasExited => false;
        public List<string> Writes { get; } = [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<string> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(_reads.Count == 0 ? string.Empty : _reads.Dequeue());

        public Task<bool> WriteAsync(string data, CancellationToken cancellationToken = default)
        {
            Writes.Add(data);
            return Task.FromResult(true);
        }
    }

    private sealed class ImmediateDelay : IAsyncDelay
    {
        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
