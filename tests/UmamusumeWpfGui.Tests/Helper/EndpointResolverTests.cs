using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using Xunit;

namespace UmamusumeWpfGui.Tests.Helper;

public sealed class EndpointResolverTests
{
    [Fact]
    public void Resolve_WhenMuMuHasNoListedDevice_ConnectsAndVerifiesFirstKnownEndpoint()
    {
        // Given: MuMu's bundled ADB starts with no listed devices, but its first MAA endpoint is available.
        var runner = new ScriptedAdbRunner(
        [
            new AdbCommandResult("List of devices attached\n", "", 0, false, null),
            new AdbCommandResult("connected to 127.0.0.1:16384", "", 0, false, null),
            new AdbCommandResult("device", "", 0, false, null),
        ]);
        var resolver = new EndpointResolver(runner);

        // When: resolution runs for the selected MuMu profile.
        var result = resolver.Resolve(
            @"C:\MuMu\nx_main\adb.exe",
            "MuMuEmulator12",
            CancellationToken.None);

        // Then: it returns the only ADB endpoint that was actively verified as a device.
        Assert.Equal(["127.0.0.1:16384"], result.VerifiedEndpoints);
        Assert.Equal(
            [
                ["devices"],
                ["connect", "127.0.0.1:16384"],
                ["-s", "127.0.0.1:16384", "get-state"],
            ],
            runner.Commands);
    }

    [Fact]
    public void Resolve_WhenExistingDeviceIsListed_DoesNotProbeFallbackEndpoints()
    {
        // Given: an ADB server that already lists a usable device.
        var runner = new ScriptedAdbRunner(
        [
            new AdbCommandResult("List of devices attached\nemulator-5554\tdevice\n", "", 0, false, null),
        ]);
        var resolver = new EndpointResolver(runner);

        // When: resolution runs for LDPlayer.
        var result = resolver.Resolve(@"C:\LDPlayer\adb.exe", "LDPlayer", CancellationToken.None);

        // Then: it preserves the existing device and does not issue a connect command.
        Assert.Equal(["emulator-5554"], result.VerifiedEndpoints);
        Assert.Equal([["devices"]], runner.Commands);
    }

    [Fact]
    public async Task ResolveAsync_WhenExistingDeviceIsListed_ReturnsItWithoutFallbackProbes()
    {
        var runner = new ScriptedAdbRunner(
        [
            new AdbCommandResult("List of devices attached\nemulator-5554\tdevice\n", "", 0, false, null),
        ]);
        var resolver = new EndpointResolver(runner, new ImmediateDelay());

        var result = await resolver.ResolveAsync(
            @"C:\LDPlayer\adb.exe",
            "LDPlayer",
            CancellationToken.None);

        Assert.Equal(["emulator-5554"], result.VerifiedEndpoints);
        Assert.Equal([["devices"]], runner.Commands);
    }

    [Fact]
    public async Task ResolveAsync_WhenConnectSucceeds_PollsUntilEndpointIsReady()
    {
        var runner = new ScriptedAdbRunner(
        [
            new AdbCommandResult("List of devices attached\n", "", 0, false, null),
            new AdbCommandResult("connected to 127.0.0.1:16384", "", 0, false, null),
            new AdbCommandResult("List of devices attached\n127.0.0.1:16384\toffline\n", "", 0, false, null),
            new AdbCommandResult("List of devices attached\n127.0.0.1:16384\tdevice\n", "", 0, false, null),
        ]);
        var resolver = new EndpointResolver(runner, new ImmediateDelay());

        var result = await resolver.ResolveAsync(
            @"C:\MuMu\nx_main\adb.exe",
            "MuMuEmulator12",
            CancellationToken.None);

        Assert.Equal(["127.0.0.1:16384"], result.VerifiedEndpoints);
        Assert.Equal(
            [
                ["devices"],
                ["connect", "127.0.0.1:16384"],
                ["devices"],
                ["devices"],
            ],
            runner.Commands);
    }

    [Fact]
    public async Task ResolveAsync_WhenConnectFailsTransiently_RetriesTheEndpoint()
    {
        var runner = new ScriptedAdbRunner(
        [
            new AdbCommandResult("List of devices attached\n", "", 0, false, null),
            new AdbCommandResult("", "connection refused", 1, false, null),
            new AdbCommandResult("connected to 127.0.0.1:16384", "", 0, false, null),
            new AdbCommandResult("List of devices attached\n127.0.0.1:16384\tdevice\n", "", 0, false, null),
        ]);
        var resolver = new EndpointResolver(runner, new ImmediateDelay());
        var policy = new EndpointResolutionPolicy(TimeSpan.Zero, TimeSpan.Zero, 2, TimeSpan.Zero);

        var result = await resolver.ResolveAsync(
            @"C:\MuMu\nx_main\adb.exe",
            "MuMuEmulator12",
            CancellationToken.None,
            policy);

        Assert.Equal(["127.0.0.1:16384"], result.VerifiedEndpoints);
        Assert.Equal(2, runner.Commands.Count(command => command[0] == "connect"));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("exited", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveAsync_WhenEndpointNeverBecomesReady_ReturnsDiagnostics()
    {
        var runner = new ScriptedAdbRunner(
        [
            new AdbCommandResult("List of devices attached\n", "", 0, false, null),
            new AdbCommandResult("connected to 127.0.0.1:5555", "", 0, false, null),
            new AdbCommandResult("List of devices attached\n127.0.0.1:5555\toffline\n", "", 0, false, null),
        ]);
        var resolver = new EndpointResolver(runner, new ImmediateDelay());
        var policy = new EndpointResolutionPolicy(TimeSpan.Zero, TimeSpan.Zero, 1, TimeSpan.Zero);

        var result = await resolver.ResolveAsync(
            @"C:\Androws\adb.exe",
            "Androws",
            CancellationToken.None,
            policy);

        Assert.Empty(result.VerifiedEndpoints);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("ready", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveAsync_WhenCancellationArrivesDuringPolling_StopsWithoutMoreCommands()
    {
        using var source = new CancellationTokenSource();
        var delay = new ImmediateDelay(() => source.Cancel());
        var runner = new ScriptedAdbRunner(
        [
            new AdbCommandResult("List of devices attached\n", "", 0, false, null),
            new AdbCommandResult("connected to 127.0.0.1:5555", "", 0, false, null),
            new AdbCommandResult("List of devices attached\n127.0.0.1:5555\toffline\n", "", 0, false, null),
        ]);
        var resolver = new EndpointResolver(runner, delay);
        var policy = new EndpointResolutionPolicy(TimeSpan.FromMinutes(1), TimeSpan.Zero, 3, TimeSpan.Zero);

        await Assert.ThrowsAsync<OperationCanceledException>(() => resolver.ResolveAsync(
            @"C:\Androws\adb.exe",
            "Androws",
            source.Token,
            policy));

        Assert.Equal(3, runner.Commands.Count);
    }

    private sealed class ScriptedAdbRunner : IAdbRunner
    {
        private readonly Queue<AdbCommandResult> _results;

        public ScriptedAdbRunner(IEnumerable<AdbCommandResult> results) => _results = new(results);

        public List<IReadOnlyList<string>> Commands { get; } = [];

        public AdbCommandResult Run(string adbPath, IReadOnlyList<string> arguments)
        {
            Commands.Add(arguments);
            return _results.Dequeue();
        }

        public Task<AdbCommandResult> RunAsync(
            string adbPath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Run(adbPath, arguments));
        }

        public (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error) RunDevices(string adbPath)
        {
            var result = Run(adbPath, ["devices"]);
            return (result.Stdout, result.Stderr, result.ExitCode, result.TimedOut, result.Error);
        }
    }

    private sealed class ImmediateDelay : IAsyncDelay
    {
        private readonly Action? _onDelay;

        public ImmediateDelay(Action? onDelay = null) => _onDelay = onDelay;

        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        {
            _onDelay?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
