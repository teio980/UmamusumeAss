using System.Globalization;
using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services;

/// <summary>
/// Explicit, non-UI diagnostics entry point for exercising an existing JSON
/// pipeline against a manually prepared emulator screen.  It deliberately
/// accepts semantic text only; task behavior, ROI and click anchors remain in
/// the JSON definition.
/// </summary>
public static class CliDiagnostics
{
    private const string Switch = "--diagnostics";
    private const string DefaultTask = "independent_skills_search_checkbox_ocr";
    private const string DefaultDefinition = HachimiResourcePaths.UraExecution;

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(argument => argument.Equals(Switch, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        HachimiJsonPipelineRunner runner,
        IAdbConnectionSessionFactory sessionFactory,
        IAdbRuntime adbRuntime,
        ISettingsService settingsService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(adbRuntime);
        ArgumentNullException.ThrowIfNull(settingsService);

        if (!TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine($"DIAGNOSTIC usage error: {error}");
            PrintUsage();
            return 2;
        }

        StreamWriter? logFile = null;
        if (!string.IsNullOrWhiteSpace(options.LogFilePath))
        {
            try
            {
                var fullPath = Path.GetFullPath(options.LogFilePath);
                var parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(parent))
                    Directory.CreateDirectory(parent);
                logFile = new StreamWriter(
                    new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true,
                };
                Console.SetOut(logFile);
                Console.SetError(logFile);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"DIAGNOSTIC log-file error: {exception.Message}");
                return 2;
            }
        }

        try
        {
            var settings = settingsService.Load();
            var adbPath = string.IsNullOrWhiteSpace(settings.AdbPath)
                ? "adb"
                : settings.AdbPath.Trim();
            var serial = await ResolveSerialAsync(
                    options.Serial,
                    settings,
                    adbRuntime,
                    adbPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(serial))
            {
                Console.Error.WriteLine(
                    "DIAGNOSTIC connection error: no ADB serial was supplied or discovered.");
                return 2;
            }

            Console.WriteLine(
                $"DIAGNOSTIC target='{options.TargetText}' task='{options.TaskName}' "
                + $"adb='{adbPath}' serial='{serial}' definition='{options.DefinitionPath}'.");

            var connected = await sessionFactory.ConnectAsync(
                AdbConnectionOptions.Create(
                    adbPath,
                    serial,
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromMilliseconds(250)),
                cancellationToken)
            .ConfigureAwait(false);
            await using var session = connected.Session;
            if (!connected.Succeeded || session is null)
            {
                Console.Error.WriteLine(
                    $"DIAGNOSTIC connection error: {connected.Error ?? "ADB connection failed."}");
                return 2;
            }

            var properties = session.Properties;
            var screen = properties.ScreenSize;
            if (screen is null || screen.Width <= 0 || screen.Height <= 0)
            {
                Console.Error.WriteLine(
                    "DIAGNOSTIC connection error: device screen size is unavailable.");
                return 2;
            }

            var connection = new LastVerifiedConnection(
            session.AdbPath,
            session.Serial,
            properties.AndroidId,
            properties.AndroidVersion,
            screen.Width,
            screen.Height,
            screen.Width,
            screen.Height,
            DateTimeOffset.UtcNow);

            var definitionPath = ResolveDefinitionPath(options.DefinitionPath);
            var logSink = new ConsoleLogSink();
            var result = await runner.RunAsync(
                    connection,
                    definitionPath,
                    options.TaskName,
                    new HachimiPipelineRunOptions
                    {
                        TargetTextOverrides = new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            [options.TaskName] = options.TargetText,
                        },
                    },
                    logSink,
                    cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine(
                $"DIAGNOSTIC result target='{options.TargetText}' "
                + $"recognized={logSink.RecognizedCount} match={logSink.MatchCount} "
                + $"click={logSink.ClickCount} "
                + $"lastTask='{result.LastTask ?? ""}' "
                + $"succeeded={result.Succeeded} message='{result.Message}'.");
            Console.WriteLine(
                $"DIAGNOSTIC exitCode={(result.Succeeded ? 0 : 1).ToString(CultureInfo.InvariantCulture)}.");
            return result.Succeeded ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("DIAGNOSTIC canceled.");
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"DIAGNOSTIC exception: {exception.GetType().Name}: {exception.Message}");
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

    private static string ResolveDefinitionPath(string definitionPath)
    {
        if (Path.IsPathRooted(definitionPath))
            return definitionPath;

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, definitionPath));
    }

    private static async Task<string?> ResolveSerialAsync(
        string? requested,
        ConnectionSettings settings,
        IAdbRuntime adbRuntime,
        string adbPath,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return requested.Trim();

        if (!string.IsNullOrWhiteSpace(settings.ConnectAddress))
            return settings.ConnectAddress.Trim();

        var listed = await adbRuntime.ListDevicesAsync(adbPath, cancellationToken)
            .ConfigureAwait(false);
        var ready = listed.Devices.FirstOrDefault(device => device.IsReady);
        if (ready is not null)
            return ready.Serial;

        return settings.ConnectAddressHistory.FirstOrDefault(
            address => !string.IsNullOrWhiteSpace(address));
    }

    private static bool TryParse(
        IReadOnlyList<string> args,
        out CliOptions options,
        out string error)
    {
        var taskName = DefaultTask;
        var targetText = string.Empty;
        var serial = (string?)null;
        var definition = DefaultDefinition;
        var logFile = (string?)null;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.Equals(Switch, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryReadValue(args, ref index, "--task-name", out var taskValue)
                && !TryReadValue(args, ref index, "--task", out taskValue))
            {
                if (TryReadValue(args, ref index, "--target-text", out var targetValue)
                    || TryReadValue(args, ref index, "--semantic", out targetValue))
                {
                    targetText = targetValue;
                    continue;
                }

                if (TryReadValue(args, ref index, "--serial", out var serialValue))
                {
                    serial = serialValue;
                    continue;
                }

                if (TryReadValue(args, ref index, "--definition", out var definitionValue))
                {
                    definition = definitionValue;
                    continue;
                }

                if (TryReadValue(args, ref index, "--log-file", out var logFileValue))
                {
                    logFile = logFileValue;
                    continue;
                }

                error = $"unknown argument '{argument}'.";
                options = EmptyOptions();
                return false;
            }

            taskName = taskValue;
        }

        if (string.IsNullOrWhiteSpace(taskName))
        {
            error = "task name cannot be empty.";
            options = EmptyOptions();
            return false;
        }

        if (string.IsNullOrWhiteSpace(targetText))
        {
            error = "--target-text (or --semantic) is required.";
            options = EmptyOptions();
            return false;
        }

        options = new CliOptions(taskName.Trim(), targetText.Trim(), serial, definition, logFile);
        error = string.Empty;
        return true;
    }

    private static CliOptions EmptyOptions() =>
        new(DefaultTask, string.Empty, null, DefaultDefinition, null);

    private static bool TryReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string name,
        out string value)
    {
        if (index < 0 || index >= args.Count
            || !args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            value = string.Empty;
            return false;
        }

        if (index + 1 >= args.Count
            || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = args[index];
        return true;
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        "Usage: UmamusumeAss.exe --diagnostics "
        + "--target-text <skill> [--task-name <json-task>] [--serial <adb-serial>] "
        + "[--definition <path>]");

    private sealed record CliOptions(
        string TaskName,
        string TargetText,
        string? Serial,
        string DefinitionPath,
        string? LogFilePath);

    private sealed class ConsoleLogSink : IGrassTaskLogSink
    {
        public int RecognizedCount { get; private set; }
        public int MatchCount { get; private set; }
        public int ClickCount { get; private set; }

        public void Add(string type, string details, LogEntryKind kind = LogEntryKind.Info)
        {
            if (details.Contains("recognized=", StringComparison.OrdinalIgnoreCase)
                || details.Contains("OCR retry recognized=", StringComparison.OrdinalIgnoreCase))
                RecognizedCount++;
            if (details.Contains("OCR grouped match=", StringComparison.OrdinalIgnoreCase)
                || details.Contains("OCR target=", StringComparison.OrdinalIgnoreCase)
                    && details.Contains("recognized=", StringComparison.OrdinalIgnoreCase))
                MatchCount++;
            if (details.Contains("Clicked OCR", StringComparison.OrdinalIgnoreCase))
                ClickCount++;

            Console.WriteLine($"[{kind}] {type}: {details}");
        }
    }
}
