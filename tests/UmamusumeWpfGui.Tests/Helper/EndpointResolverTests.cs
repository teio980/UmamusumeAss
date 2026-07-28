using System;
using System.Collections.Generic;
using System.Threading;
using UmamusumeWpfGui.Helper;
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

        public (string Stdout, string Stderr, int ExitCode, bool TimedOut, Exception? Error) RunDevices(string adbPath)
        {
            var result = Run(adbPath, ["devices"]);
            return (result.Stdout, result.Stderr, result.ExitCode, result.TimedOut, result.Error);
        }
    }
}
