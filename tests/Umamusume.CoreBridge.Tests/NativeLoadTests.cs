using System.Diagnostics;
using System.Text.Json;

namespace Umamusume.CoreBridge.Tests;

public sealed class NativeLoadTests
{
    private static readonly string[] Scenarios =
    [
        "load",
        "missing-resource",
        "corrupt-resource",
        "valid-resource",
        "s2-stubs",
        "fake-adb-connect",
    ];

    [NativeFact]
    public async Task IntegrationHostPassesEveryScenarioInFreshProcess()
    {
        string hostPath = RequiredEnvironmentVariable("UMA_INTEGRATION_HOST_PATH");
        string fakeAdbPath = RequiredEnvironmentVariable("UMA_FAKE_ADB_PATH");
        Assert.True(File.Exists(hostPath), $"Integration host does not exist: {hostPath}");
        Assert.True(File.Exists(fakeAdbPath), $"Fake ADB does not exist: {fakeAdbPath}");

        foreach (string scenario in Scenarios)
        {
            ProcessResult result = await RunHost(hostPath, scenario, fakeAdbPath);
            Assert.True(result.ExitCode == 0, $"{scenario} failed: {result.StandardError}\n{result.StandardOutput}");
            Assert.InRange(result.StandardOutput.Length, 1, 4096);
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            Assert.True(document.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(scenario, document.RootElement.GetProperty("scenario").GetString());
        }
    }

    private static async Task<ProcessResult> RunHost(string hostPath, string scenario, string fakeAdbPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList = { hostPath, scenario, fakeAdbPath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(hostPath)
                    ?? throw new InvalidOperationException("Integration host has no parent directory."),
            },
        };
        Assert.True(process.Start());
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static string RequiredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Required environment variable is missing: {name}");

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
