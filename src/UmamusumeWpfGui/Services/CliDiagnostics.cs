using System.IO;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services;

/// <summary>Native-Core pipeline diagnostic entry point.</summary>
public static class CliDiagnostics
{
    private const string Switch = "--diagnostics";
    private const string DefaultTask = "independent_skills_search_checkbox_ocr";
    private const string DefaultDefinition = "resource/hachimi/ura/manifest.json";

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(argument => argument.Equals(Switch, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        IAdbRuntime adbRuntime,
        ISettingsService settingsService,
        IUmaService umaService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(adbRuntime);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(umaService);

        if (!TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine($"DIAGNOSTIC usage error: {error}");
            PrintUsage();
            return 2;
        }

        StreamWriter? logFile = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(options.LogFilePath))
            {
                var fullPath = Path.GetFullPath(options.LogFilePath);
                var parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                logFile = new StreamWriter(new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true,
                };
                Console.SetOut(logFile);
                Console.SetError(logFile);
            }

            var settings = settingsService.Load();
            var adbPath = string.IsNullOrWhiteSpace(settings.AdbPath) ? "adb" : settings.AdbPath.Trim();
            var serial = await ResolveSerialAsync(options.Serial, settings, adbRuntime, adbPath, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(serial))
            {
                Console.Error.WriteLine("DIAGNOSTIC connection error: no ADB serial was supplied or discovered.");
                return 2;
            }

            var definitionPath = ResolveDefinitionPath(options.DefinitionPath);
            var pipelineJson = await File.ReadAllTextAsync(definitionPath, cancellationToken).ConfigureAwait(false);
            var validationError = umaService.ValidatePipelineJson(pipelineJson);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                Console.Error.WriteLine($"DIAGNOSTIC definition error: {validationError}");
                return 2;
            }

            Console.WriteLine(
                $"DIAGNOSTIC target='{options.TaskName}' adb='{adbPath}' serial='{serial}' definition='{definitionPath}'.");
            var terminal = await umaService.ConnectAsync(adbPath, serial, settings.ConnectConfig, cancellationToken)
                .ConfigureAwait(false);
            if (terminal is not ConnectionSucceededEvent connected)
            {
                Console.Error.WriteLine(terminal is ConnectionFailedEvent failure
                    ? $"DIAGNOSTIC connection error: {failure.ErrorCode} at {failure.Phase}: {failure.Message}"
                    : "DIAGNOSTIC connection error: native ADB connection failed.");
                return 2;
            }

            Console.WriteLine($"DIAGNOSTIC connected serial='{connected.Serial}' screen={connected.Width}x{connected.Height}.");
            var result = await umaService.RunPipelineAsync(pipelineJson, options.TaskName, cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine(
                $"DIAGNOSTIC result finalTask='{result.FinalTask}' totalSteps={result.TotalSteps} "
                + $"succeeded={result.Succeeded} message='{result.Message ?? string.Empty}'.");
            Console.WriteLine($"DIAGNOSTIC exitCode={(result.Succeeded ? 0 : 1)}.");
            return result.Succeeded ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("DIAGNOSTIC canceled.");
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"DIAGNOSTIC exception: {exception.GetType().Name}: {exception.Message}");
            return 3;
        }
        finally
        {
            if (logFile is not null)
            {
                await logFile.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                logFile.Dispose();
            }
        }
    }

    private static string ResolveDefinitionPath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

    private static async Task<string?> ResolveSerialAsync(
        string? requested,
        ConnectionSettings settings,
        IAdbRuntime adbRuntime,
        string adbPath,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();
        if (!string.IsNullOrWhiteSpace(settings.ConnectAddress)) return settings.ConnectAddress.Trim();
        var listed = await adbRuntime.ListDevicesAsync(adbPath, cancellationToken).ConfigureAwait(false);
        return listed.Devices.FirstOrDefault(device => device.IsReady)?.Serial
            ?? settings.ConnectAddressHistory.FirstOrDefault(address => !string.IsNullOrWhiteSpace(address));
    }

    private static bool TryParse(IReadOnlyList<string> args, out CliOptions options, out string error)
    {
        var taskName = DefaultTask;
        var serial = (string?)null;
        var definition = DefaultDefinition;
        var logFile = (string?)null;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.Equals(Switch, StringComparison.OrdinalIgnoreCase)) continue;
            if (TryReadValue(args, ref index, "--task-name", out var value)
                || TryReadValue(args, ref index, "--task", out value)
                || TryReadValue(args, ref index, "--entry-task", out value))
                taskName = value;
            else if (TryReadValue(args, ref index, "--serial", out value)) serial = value;
            else if (TryReadValue(args, ref index, "--definition", out value)) definition = value;
            else if (TryReadValue(args, ref index, "--log-file", out value)) logFile = value;
            else
            {
                error = $"unknown argument '{argument}'.";
                options = new(DefaultTask, DefaultDefinition, null, null);
                return false;
            }
        }
        if (string.IsNullOrWhiteSpace(taskName))
        {
            error = "entry task cannot be empty.";
            options = new(DefaultTask, DefaultDefinition, null, null);
            return false;
        }
        options = new(taskName.Trim(), definition, serial, logFile);
        error = string.Empty;
        return true;
    }

    private static bool TryReadValue(IReadOnlyList<string> args, ref int index, string name, out string value)
    {
        if (index < 0 || index >= args.Count || !args[index].Equals(name, StringComparison.OrdinalIgnoreCase)
            || index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }
        value = args[++index];
        return true;
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        "Usage: UmamusumeAss.exe --diagnostics [--task-name <entry-task>] [--serial <adb-serial>] [--definition <path>] [--log-file <path>]");

    private sealed record CliOptions(string TaskName, string DefinitionPath, string? Serial, string? LogFilePath);
}
