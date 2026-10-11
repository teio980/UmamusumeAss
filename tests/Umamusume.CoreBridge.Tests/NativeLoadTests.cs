using System.Diagnostics;
using System.Text.Json;

namespace Umamusume.CoreBridge.Tests;

public sealed class NativeLoadTests
{
    private static readonly string[] Scenarios =
    [
        "load",
        "default-resource",
        "corrupt-resource",
        "valid-resource",
        "device-controls",
        "fake-adb-connect",
        "native-game-task",
        "native-queue-task",
        "native-career-checkpoint-resume",
    ];

    [NativeFact]
    public async Task IntegrationHostPassesEveryScenarioInFreshProcess()
    {
        string hostPath = RequiredEnvironmentVariable("UMA_INTEGRATION_HOST_PATH");
        string fakeAdbPath = RequiredEnvironmentVariable("UMA_FAKE_ADB_PATH");
        Assert.True(File.Exists(hostPath), $"Integration host does not exist: {hostPath}");
        Assert.True(File.Exists(fakeAdbPath), $"Fake ADB does not exist: {fakeAdbPath}");

        var scenarios = Scenarios.ToList();
        string? ocrGoldenPath = Environment.GetEnvironmentVariable("UMA_OCR_GOLDEN_PATH");
        if (!string.IsNullOrWhiteSpace(ocrGoldenPath))
        {
            Assert.True(File.Exists(ocrGoldenPath), $"OCR golden corpus does not exist: {ocrGoldenPath}");
            scenarios.Add("native-ocr");
        }

        foreach (string scenario in scenarios)
        {
            ProcessResult result = await RunHost(hostPath, scenario, fakeAdbPath, ocrGoldenPath ?? "");
            Assert.True(result.ExitCode == 0, $"{scenario} failed: {result.StandardError}\n{result.StandardOutput}");
            Assert.InRange(result.StandardOutput.Length, 1, 4096);
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            Assert.True(document.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(scenario, document.RootElement.GetProperty("scenario").GetString());
            if (scenario == "native-ocr")
            {
                int validatedCases = document.RootElement.GetProperty("validatedCases").GetInt32();
                Assert.True(validatedCases >= 14,
                    $"Native OCR validated only {validatedCases} fixed golden cases.");
            }
        }
    }

    private static async Task<ProcessResult> RunHost(
        string hostPath,
        string scenario,
        string fakeAdbPath,
        string? ocrGoldenPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList = { hostPath, scenario, fakeAdbPath, ocrGoldenPath ?? "" },
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
        // MSVC Debug performs real packaged Career observer matches, and the
        // queue scenario runs three native queues including an in-scene cancel.
        // Both do substantial work beyond the short load/control scenarios.
        TimeSpan budget = TimeSpan.FromSeconds(
            scenario is "native-career-checkpoint-resume" or "native-queue-task" ? 90 : 30);
        using var timeout = new CancellationTokenSource(budget);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanup.Token);
            throw new TimeoutException(
                $"{scenario} exceeded {budget.TotalSeconds}s: {await standardError}\n{await standardOutput}");
        }
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static string RequiredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Required environment variable is missing: {name}");

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
