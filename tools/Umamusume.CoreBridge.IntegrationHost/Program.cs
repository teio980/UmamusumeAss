using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Umamusume.CoreBridge;

namespace Umamusume.CoreBridge.IntegrationHost;

internal static class Program
{
    private static int _validatedNativeOcrCases;

    private static async Task<int> Main(string[] args)
    {
        string scenario = args.Length > 0 ? args[0] : "missing-scenario";
        try
        {
            if (scenario == "readonly-device")
            {
                string details = await ValidateReadOnlyDevice(
                    args.Length > 1 ? args[1] : "",
                    args.Length > 2 ? args[2] : "",
                    args.Length > 3 ? args[3] : null);
                WriteResult(scenario, true, details);
                return 0;
            }

            if (scenario == "career-run")
            {
                var result = await RunCareerTask(args);
                WriteResult(scenario, result.Succeeded, result.Message ?? result.ErrorCode.ToString());
                return result.Succeeded ? 0 : result.ErrorCode == ConnectionErrorCode.Canceled ? 2 : 1;
            }

            if (scenario == "queue-run")
            {
                QueueRunResult result = await RunQueue(args);
                bool queueSucceeded = result.Succeeded && result.FailedTasks == 0 && result.SkippedTasks == 0;
                Console.WriteLine(JsonSerializer.Serialize(new { scenario, success = queueSucceeded, result }));
                return result.ErrorCode == ConnectionErrorCode.Canceled ? 2 : queueSucceeded ? 0 : 1;
            }

            if (scenario is "catalog" or "validate-pipeline")
                return await RunDeveloperCommand(args);

            bool success = await RunScenario(
                scenario,
                args.Length > 1 ? args[1] : "",
                args.Length > 2 ? args[2] : "");
            string scenarioDetails = scenario switch
            {
                "native-ocr" => $"validated {_validatedNativeOcrCases} fixed OCR golden cases",
                "native-career-checkpoint-resume" => success
                    ? "verified a durable Independent Start checkpoint at Home with no game input"
                    : "Independent checkpoint-resume slice returned false",
                _ => success ? "passed" : "scenario returned false",
            };
            WriteResult(scenario, success, scenarioDetails,
                scenario == "native-ocr" ? _validatedNativeOcrCases : null);
            return success ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            WriteResult(scenario, false, "Canceled before the native run was accepted.");
            return 2;
        }
        catch (Exception exception)
        {
            WriteResult(scenario, false, $"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static Task<bool> RunScenario(string scenario, string fakeAdbPath, string ocrGoldenPath) => scenario switch
    {
        "load" => Task.FromResult(ProbeLoad()),
        "default-resource" => ValidateDefaultResourceInitialization(),
        "corrupt-resource" => ExpectCorruptResourceFailure(),
        "valid-resource" => ValidateResourceInitialization(),
        "device-controls" => ValidateDeviceControls(fakeAdbPath),
        "fake-adb-connect" => ValidateFakeAdbConnect(fakeAdbPath),
        "native-game-task" => ValidateNativeGameTask(fakeAdbPath),
        "native-queue-task" => ValidateNativeQueueTask(fakeAdbPath),
        "native-career-checkpoint-resume" => ValidateCareerCheckpointResume(fakeAdbPath),
        "native-ocr" => ValidateNativeOcr(ocrGoldenPath),
        "native-ocr-smoke" => ValidateNativeOcrSmoke(),
        _ => throw new ArgumentException($"Unknown scenario: {scenario}", nameof(scenario)),
    };

    private static async Task<bool> ValidateNativeOcr(string goldenPath)
    {
        string appData = CreateTemporaryDirectory();
        try
        {
            await using var service = new UmaService(new InlineEventDispatcher());
            await service.InitializeAsync(AppContext.BaseDirectory, appData);

            if (!File.Exists(goldenPath))
            {
                throw new FileNotFoundException("OCR golden corpus is required for native-ocr.", goldenPath);
            }

            string fullGoldenPath = Path.GetFullPath(goldenPath);
            var tracePaths = new List<string> { fullGoldenPath };
            if (Path.GetFileName(fullGoldenPath).Equals("ocr-golden.jsonl", StringComparison.OrdinalIgnoreCase))
            {
                string edgePath = Path.Combine(Path.GetDirectoryName(fullGoldenPath)!, "ocr-language-edge-golden.jsonl");
                if (File.Exists(edgePath))
                {
                    tracePaths.Add(edgePath);
                }
            }

            int totalCases = 0;
            foreach (string tracePath in tracePaths)
            {
                int traceCases = 0;
                int expectedCases = -1;
                bool completed = false;
                foreach (string line in await File.ReadAllLinesAsync(tracePath))
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement record = document.RootElement;
                    string recordType = record.TryGetProperty("recordType", out JsonElement type)
                        ? type.GetString() ?? ""
                        : "";
                    if (recordType == "summary")
                    {
                        completed = record.GetProperty("status").GetString() == "completed";
                        expectedCases = record.GetProperty("cases").GetInt32();
                        continue;
                    }

                    if (recordType != "ocr")
                    {
                        continue;
                    }

                    if (!MatchesOcrRecord(service, record, tracePath))
                    {
                        return false;
                    }

                    traceCases++;
                    totalCases++;
                }

                if (!completed || expectedCases != traceCases)
                {
                    return false;
                }
            }

            _validatedNativeOcrCases = totalCases;
            return totalCases >= 14;
        }
        finally
        {
            Directory.Delete(appData, recursive: true);
        }
    }

    private static async Task<bool> ValidateNativeOcrSmoke()
    {
        string appData = CreateTemporaryDirectory();
        try
        {
            await using var service = new UmaService(new InlineEventDispatcher());
            await service.InitializeAsync(AppContext.BaseDirectory, appData);

            const int width = 32;
            const int height = 32;
            byte[] rgba = new byte[width * height * 4];
            for (int index = 3; index < rgba.Length; index += 4)
            {
                rgba[index] = byte.MaxValue;
            }

            var image = new RecognitionImage(width, height, [], rgba);
            TextRecognitionResult result = service.RecognizeText(image, "en-US");
            return result.Width == width && result.Height == height
                && !string.IsNullOrWhiteSpace(result.Language);
        }
        finally
        {
            Directory.Delete(appData, recursive: true);
        }
    }

    private static bool MatchesOcrRecord(UmaService service, JsonElement record, string tracePath)
    {
        string inputPath = Path.Combine(
            Path.GetDirectoryName(tracePath)!,
            record.GetProperty("inputPath").GetString()!);
        using var compressed = File.OpenRead(inputPath);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var uncompressed = new MemoryStream();
        gzip.CopyTo(uncompressed);
        byte[] data = uncompressed.ToArray();
        if (data.Length < 24
            || data[0] != (byte)'U' || data[1] != (byte)'I' || data[2] != (byte)'M' || data[3] != (byte)'G'
            || data[4] != 1)
        {
            return false;
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8, 4));
        int height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12, 4));
        int grayLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16, 4));
        int rgbaLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(20, 4));
        if (width <= 0 || height <= 0 || grayLength < 0 || rgbaLength != checked(width * height * 4)
            || data.Length != 24 + grayLength + rgbaLength)
        {
            return false;
        }

        byte[] gray = data.AsSpan(24, grayLength).ToArray();
        byte[] rgba = data.AsSpan(24 + grayLength, rgbaLength).ToArray();
        string requestedLanguage = record.GetProperty("requestedLanguage").ValueKind == JsonValueKind.String
            ? record.GetProperty("requestedLanguage").GetString()!
            : "";
        var image = new RecognitionImage(width, height, gray, rgba);
        TextRecognitionResult result = service.RecognizeText(image, requestedLanguage);
        JsonElement expectedDetections = record.GetProperty("detections");
        if (result.Width != width
            || result.Height != height
            || result.Language != record.GetProperty("selectedLanguage").GetString()
            || result.Detections.Count != expectedDetections.GetArrayLength())
        {
            return false;
        }

        for (int index = 0; index < result.Detections.Count; index++)
        {
            TextDetection actual = result.Detections[index];
            JsonElement item = expectedDetections[index];
            if (actual.Text != item.GetProperty("text").GetString()
                || actual.Bounds.X != item.GetProperty("x").GetInt32()
                || actual.Bounds.Y != item.GetProperty("y").GetInt32()
                || actual.Bounds.Width != item.GetProperty("width").GetInt32()
                || actual.Bounds.Height != item.GetProperty("height").GetInt32()
                || Math.Abs(actual.Confidence - item.GetProperty("confidence").GetDouble()) > 1e-9)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ProbeLoad()
    {
        if (!NativeLibrary.TryLoad("UmamusumeCore.dll", out IntPtr handle))
        {
            return false;
        }

        NativeLibrary.Free(handle);
        return true;
    }

    private static async Task<bool> ExpectCorruptResourceFailure()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string resourceDirectory = Path.Combine(root, "resource");
            Directory.CreateDirectory(resourceDirectory);
            await File.WriteAllTextAsync(Path.Combine(resourceDirectory, "connection.json"), "not-json");

            await using var service = new UmaService(new InlineEventDispatcher());
            try
            {
                await service.InitializeAsync(root, Path.Combine(root, "data"));
                return false;
            }
            catch (ManagedBridgeException)
            {
                return true;
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<bool> ValidateDefaultResourceInitialization()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            await using var service = new UmaService(new InlineEventDispatcher());
            await service.InitializeAsync(root, Path.Combine(root, "data"));
            return !string.IsNullOrWhiteSpace(service.CoreVersion);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<bool> ValidateResourceInitialization()
    {
        string appData = CreateTemporaryDirectory();
        try
        {
            await using var service = new UmaService(new InlineEventDispatcher());
            await service.InitializeAsync(AppContext.BaseDirectory, appData);
            return !string.IsNullOrWhiteSpace(service.CoreVersion);
        }
        finally
        {
            Directory.Delete(appData, recursive: true);
        }
    }

    private static async Task<bool> ValidateDeviceControls(string fakeAdbPath)
    {
        if (!Path.IsPathFullyQualified(fakeAdbPath) || !File.Exists(fakeAdbPath))
        {
            return false;
        }

        string appData = CreateTemporaryDirectory();
        string? previousResolution = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_RESOLUTION");
        string? previousOrientation = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_ORIENTATION");
        Environment.SetEnvironmentVariable("UMA_FAKE_ADB_RESOLUTION", "900x1600");
        Environment.SetEnvironmentVariable("UMA_FAKE_ADB_ORIENTATION", "0");
        try
        {
            await using var service = new UmaService(new InlineEventDispatcher());
            await service.InitializeAsync(AppContext.BaseDirectory, appData);
            ConnectionTerminalEvent connected = await service.ConnectAsync(fakeAdbPath, "test-serial", "General");
            if (connected is not ConnectionSucceededEvent)
            {
                return false;
            }

            DeviceOperationEvent verified = await service.VerifyGameAsync("com.example.game");
            if (verified is not GameVerifiedEvent)
            {
                return false;
            }

            DeviceOperationEvent captured = await service.CaptureAsync();
            if (captured is not FrameCapturedEvent { FrameLease: { } frame } capture
                || capture.PngBytes.Length == 0)
            {
                return false;
            }

            using (frame)
            {
                DeviceOperationEvent tap = await service.TapAsync(frame, 540, 960);
                DeviceOperationEvent swipe = await service.SwipeAsync(frame, 100, 300, 900, 1500, 250);
                DeviceOperationEvent prepared = await service.PrepareTouchAsync(frame);
                if (tap is not InputSucceededEvent || swipe is not InputSucceededEvent
                    || prepared is not InputSucceededEvent)
                {
                    return false;
                }

                DeviceOperationEvent down = await service.TouchDownAsync(frame, 540, 960);
                DeviceOperationEvent up = await service.TouchUpAsync(frame);
                return down is InputSucceededEvent && up is InputSucceededEvent;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_RESOLUTION", previousResolution);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_ORIENTATION", previousOrientation);
            Directory.Delete(appData, recursive: true);
        }
    }

    private static async Task<GameTaskRunResult> RunCareerTask(string[] args)
    {
        if (args.Length != 4 || !Path.IsPathFullyQualified(args[1]) || !File.Exists(args[1])
            || string.IsNullOrWhiteSpace(args[2]) || !File.Exists(args[3]))
            throw new ArgumentException("Usage: career-run <absolute-adb-path> <serial> <settings-json-path>");

        string settings = await File.ReadAllTextAsync(args[3]);
        using var parsedSettings = JsonDocument.Parse(settings);
        if (parsedSettings.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Career settings must be a JSON object.");

        // This host has no WPF reference: Core owns recognition, strategy,
        // actions and recovery. Ctrl+C requests the same native cancellation
        // used by the application and keeps the handle alive until it drains.
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, cancel) =>
        {
            cancel.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        string appData = HostUserDirectory();
        try
        {
            await using var service = new UmaService(new InlineEventDispatcher());
            service.DiagnosticReceived += diagnostic => Console.Error.WriteLine(
                JsonSerializer.Serialize(new { recordType = "diagnostic", diagnostic }));
            await service.InitializeAsync(FindRepositoryResourceBaseDirectory(), appData, cancellation.Token);
            if (await service.ConnectAsync(args[1], args[2], "General", cancellation.Token)
                is not ConnectionSucceededEvent)
                throw new InvalidOperationException("The Career host could not connect to the device.");
            return await service.RunGameTaskAsync("career-training", settings,
                progress => Console.WriteLine(JsonSerializer.Serialize(new { recordType = "career-progress", progress })),
                cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static async Task<QueueRunResult> RunQueue(string[] args)
    {
        if (args.Length is < 4 or > 6 || !Path.IsPathFullyQualified(args[1]) || !File.Exists(args[1])
            || string.IsNullOrWhiteSpace(args[2]) || !File.Exists(args[3]))
            throw new ArgumentException(
                "Usage: queue-run <absolute-adb-path> <serial> <queue-json-path> [resource-base-directory] [user-directory]");

        using JsonDocument definition = JsonDocument.Parse(await File.ReadAllTextAsync(args[3]));
        JsonElement root = definition.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tasks", out JsonElement tasks)
            || tasks.ValueKind != JsonValueKind.Array || tasks.GetArrayLength() == 0)
            throw new ArgumentException("Queue JSON requires a nonempty tasks array.");

        var requests = new List<QueueTaskRequest>();
        foreach (JsonElement task in tasks.EnumerateArray())
        {
            string? id = task.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Every task requires an id.");
            JsonElement settings = task.TryGetProperty("settings", out JsonElement configured)
                ? configured : default;
            if (settings.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
                throw new ArgumentException("Task settings must be a JSON object.");
            requests.Add(new QueueTaskRequest(id,
                settings.ValueKind == JsonValueKind.Undefined ? "{}" : settings.GetRawText()));
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, cancel) => { cancel.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            await using var service = new UmaService(new InlineEventDispatcher());
            service.ConnectionEventReceived += item => Console.Error.WriteLine(
                JsonSerializer.Serialize(new { recordType = "event", eventType = item.GetType().Name,
                    value = JsonSerializer.SerializeToElement(item, item.GetType()) }));
            service.DiagnosticReceived += diagnostic => Console.Error.WriteLine(
                JsonSerializer.Serialize(new { recordType = "diagnostic", diagnostic }));
            string resourceBase = args.Length > 4 ? Path.GetFullPath(args[4]) : FindRepositoryResourceBaseDirectory();
            string userDirectory = args.Length > 5 ? Path.GetFullPath(args[5]) : HostUserDirectory();
            await service.InitializeAsync(resourceBase, userDirectory, cancellation.Token);
            if (await service.ConnectAsync(args[1], args[2], "General", cancellation.Token)
                is not ConnectionSucceededEvent)
                throw new InvalidOperationException("The queue host could not connect to the device.");

            // The managed host submits configuration only. Core owns the loop,
            // failures, skip decisions, cancellation and the final snapshot.
            QueueRunResult result = await service.RunQueueAsync(requests, cancellation.Token);
            Console.Error.WriteLine(await service.GetRunSnapshotJsonAsync());
            return result;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static string HostUserDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UmamusumeAss", "CoreHost");

    private static async Task<int> RunDeveloperCommand(string[] args)
    {
        string scenario = args[0];
        if (args.Length is < 2 or > 3)
            throw new ArgumentException(
                "Usage: catalog <catalog-type> [resource-base-directory] | validate-pipeline <json-path> [resource-base-directory]");
        await using var service = new UmaService(new InlineEventDispatcher());
        await service.InitializeAsync(args.Length > 2 ? Path.GetFullPath(args[2]) : FindRepositoryResourceBaseDirectory(),
            HostUserDirectory());
        if (scenario == "catalog")
        {
            Console.WriteLine(await service.GetCatalogJsonAsync(args[1]));
            return 0;
        }
        string json = await File.ReadAllTextAsync(args[1]);
        string? error = service.ValidatePipelineJson(json);
        Console.WriteLine(JsonSerializer.Serialize(new { scenario, success = string.IsNullOrEmpty(error), error }));
        return string.IsNullOrEmpty(error) ? 0 : 1;
    }

    private static async Task<string> ValidateReadOnlyDevice(string adbPath, string serial, string? capturePath = null)
    {
        if (!Path.IsPathFullyQualified(adbPath) || !File.Exists(adbPath))
        {
            throw new FileNotFoundException("The configured ADB executable does not exist.", adbPath);
        }

        if (string.IsNullOrWhiteSpace(serial))
        {
            throw new ArgumentException("A device serial is required for read-only validation.", nameof(serial));
        }

        string appData = CreateTemporaryDirectory();
        try
        {
            await using var service = new UmaService(new InlineEventDispatcher());
            await service.InitializeAsync(AppContext.BaseDirectory, appData);
            ConnectionTerminalEvent connection = await service.ConnectAsync(adbPath, serial, "General");
            if (connection is not ConnectionSucceededEvent connected)
            {
                throw new InvalidOperationException($"The device did not complete the connection handshake: {connection}");
            }

            DeviceOperationEvent verification = await service.VerifyGameAsync("com.cygames.umamusume");
            string packageStatus = verification switch
            {
                GameVerifiedEvent => "installed",
                GameVerificationFailedEvent { ErrorCode: ConnectionErrorCode.TargetGameNotInstalled } => "not-installed",
                GameVerificationFailedEvent failed => $"verification-error-{(int)failed.ErrorCode}",
                _ => throw new InvalidOperationException("The native core returned an unexpected package verification event."),
            };

            DeviceOperationEvent capture = await service.CaptureAsync();
            if (capture is not FrameCapturedEvent { FrameLease: { } frame } frameCaptured)
            {
                throw new InvalidOperationException("The device did not return a captured frame.");
            }

            using (frame)
            {
                if (frame.PngBytes.IsEmpty || frame.PngBytes.Length != checked((int)frameCaptured.Frame.ByteCount))
                {
                    throw new InvalidOperationException("The captured PNG bytes do not match frame metadata.");
                }

                if (!string.IsNullOrWhiteSpace(capturePath))
                    await File.WriteAllBytesAsync(Path.GetFullPath(capturePath), frame.PngBytes.ToArray());

                return $"serial={serial}; package={packageStatus}; display={connected.Width}x{connected.Height}; capture={frame.PngBytes.Length} bytes, orientation={frame.Metadata.Orientation}";
            }
        }
        finally
        {
            Directory.Delete(appData, recursive: true);
        }
    }

    private static async Task<bool> ValidateFakeAdbConnect(string fakeAdbPath)
    {
        if (!Path.IsPathFullyQualified(fakeAdbPath) || !File.Exists(fakeAdbPath))
        {
            return false;
        }

        string appData = CreateTemporaryDirectory();
        try
        {
            await using var service = new UmaService(new InlineEventDispatcher());
            await service.InitializeAsync(AppContext.BaseDirectory, appData);
            ConnectionTerminalEvent result = await service.ConnectAsync(
                fakeAdbPath,
                "test-serial",
                "General");
            return result is ConnectionSucceededEvent
            {
                Serial: "test-serial",
                AndroidId: "0123456789abcdef",
                AndroidVersion: "14",
                Width: 1080,
                Height: 1920,
            };
        }
        finally
        {
            Directory.Delete(appData, recursive: true);
        }
    }

    private static async Task<bool> ValidateNativeGameTask(string fakeAdbPath)
    {
        if (!Path.IsPathFullyQualified(fakeAdbPath) || !File.Exists(fakeAdbPath))
        {
            throw new InvalidOperationException($"FakeAdb path is invalid: '{fakeAdbPath}'.");
        }

        string appData = CreateTemporaryDirectory();
        string commandLogPath = Path.Combine(appData, "commands.log");
        string definitionPath = Path.Combine(appData, "task.json");
        string templatePath = Path.Combine(appData, "home.png");
        string? previousScreenshot = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_SCREENSHOT_PATH");
        string? previousInputFail = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_INPUT_FAIL");
        string? previousCommandsFile = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_COMMANDS_FILE");
        string? previousResolution = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_RESOLUTION");
        string? previousOrientation = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_ORIENTATION");
        string? previousDisplayOrientation = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_DISPLAY_ORIENTATION");
        string? previousDevicesSerial = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_DEVICES_SERIAL");
        string? previousDevicesMode = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_DEVICES_MODE");
        string? previousOfflineThenReady = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_OFFLINE_THEN_READY");
        UmaService? service = null;
        try
        {
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_COMMANDS_FILE", commandLogPath);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_SCREENSHOT_PATH", null);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_INPUT_FAIL", null);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_RESOLUTION", "900x1600");
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_ORIENTATION", "0");
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_DISPLAY_ORIENTATION", "0");
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_DEVICES_SERIAL", "test-serial");
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_DEVICES_MODE", null);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_OFFLINE_THEN_READY", null);

            service = new UmaService(new InlineEventDispatcher());
            await service.InitializeAsync(FindRepositoryResourceBaseDirectory(), appData);
            ConnectionTerminalEvent connection = await service.ConnectAsync(fakeAdbPath, "test-serial", "General");
            if (connection is not ConnectionSucceededEvent)
            {
                throw new InvalidOperationException($"FakeAdb connect failed: {connection}.");
            }

            DeviceOperationEvent captured = await service.CaptureAsync();
            if (captured is not FrameCapturedEvent { FrameLease: { } frame } frameCaptured
                || frameCaptured.PngBytes.Length == 0)
            {
                throw new InvalidOperationException($"FakeAdb capture failed: {captured}.");
            }

            using (frame)
            {
                await File.WriteAllBytesAsync(templatePath, frame.PngBytes);
                await WriteGameTaskDefinition(
                    definitionPath, templatePath, frameCaptured.Frame.Width, frameCaptured.Frame.Height,
                    cancellationWait: false);
            }
            int screenshotWidth = frameCaptured.Frame.Width;
            int screenshotHeight = frameCaptured.Frame.Height;

            // Reuse the exact image captured by the connected FakeAdb session so both
            // native WIC decoding and template matching exercise the same full frame.
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_SCREENSHOT_PATH", templatePath);

            var progress = new ConcurrentQueue<GameTaskProgressEvent>();
            var published = new ConcurrentQueue<ConnectionEvent>();
            service.ConnectionEventReceived += published.Enqueue;
            GameTaskRunResult success = await service.RunGameTaskAsync(
                "mail-collection",
                JsonSerializer.Serialize(new { definitionPath }),
                progress.Enqueue);
            if (!success.Succeeded || success.TaskId != "mail-collection"
                || success.TotalUnits <= 0 || success.CompletedUnits != success.TotalUnits
                || progress.IsEmpty || progress.Any(item => item.TaskId != "mail-collection"))
            {
                throw new InvalidOperationException(
                    $"Native mail task did not succeed: result={success}; progress={progress.Count}; " +
                    $"events=[{string.Join(" | ", published.Select(static item => item.ToString()))}].");
            }

            ConnectionEvent[] firstRunTerminals = published
                .Where(static item => item is GameTaskCompletedEvent or GameTaskFailedEvent)
                .ToArray();
            if (firstRunTerminals.Length != 1 || firstRunTerminals[0] is not GameTaskCompletedEvent)
            {
                throw new InvalidOperationException(
                    $"Expected one successful task terminal, observed {firstRunTerminals.Length}: " +
                    string.Join(" | ", published.Select(static item => item.ToString())));
            }

            string successfulCommands = await File.ReadAllTextAsync(commandLogPath);
            var successfulTaps = new List<string>();
            using (var commandReader = new StringReader(successfulCommands))
            {
                string? command;
                while ((command = commandReader.ReadLine()) is not null)
                {
                    if (command.Contains("input\ttap\t", StringComparison.Ordinal))
                    {
                        successfulTaps.Add(command);
                    }
                }
            }
            if (successfulTaps.Count != 2
                || !successfulTaps[0].EndsWith("\t450\t800", StringComparison.Ordinal)
                || !successfulTaps[1].EndsWith("\t45\t65", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Expected matched tap at 450,800 then ClickRect tap at 45,65; observed: " +
                    string.Join(" | ", successfulTaps));
            }

            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_INPUT_FAIL", "1");
            int terminalsBeforeFailure = published.Count(
                static item => item is GameTaskCompletedEvent or GameTaskFailedEvent);
            GameTaskRunResult failure = await service.RunGameTaskAsync(
                "mail-collection", JsonSerializer.Serialize(new { definitionPath }));
            ConnectionEvent[] failureTerminals = published
                .Where(static item => item is GameTaskCompletedEvent or GameTaskFailedEvent)
                .ToArray();
            if (failure.Succeeded || failure.ErrorCode == ConnectionErrorCode.Success
                || failureTerminals.Length != terminalsBeforeFailure + 1
                || failureTerminals[^1] is not GameTaskFailedEvent)
            {
                throw new InvalidOperationException(
                    $"Native input-failure run did not produce one failed terminal: result={failure}; " +
                    $"terminals=[{string.Join(" | ", failureTerminals.Select(static item => item.ToString()))}].");
            }

            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_INPUT_FAIL", null);
            await WriteGameTaskDefinition(
                definitionPath, templatePath, screenshotWidth, screenshotHeight, cancellationWait: true);
            var waitReached = new TaskCompletionSource<GameTaskProgressEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task<GameTaskRunResult> cancellationRun = service.RunGameTaskAsync(
                "mail-collection",
                JsonSerializer.Serialize(new { definitionPath }),
                item =>
                {
                    progress.Enqueue(item);
                    if (item.TaskName == "home"
                        && item.EventType == GameTaskEventType.ActionExecuting)
                    {
                        waitReached.TrySetResult(item);
                    }
                });

            try
            {
                await waitReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException(
                    $"Cancellation scenario did not reach its Wait task; progress={progress.Count}; " +
                    $"events=[{string.Join(" | ", published.Select(static item => item.ToString()))}].",
                    exception);
            }
            int terminalCountBeforeDestroy = published.Count(
                static item => item is GameTaskCompletedEvent or GameTaskFailedEvent);
            await service.DisposeAsync(TimeSpan.FromSeconds(15));
            GameTaskRunResult canceled = await cancellationRun.WaitAsync(TimeSpan.FromSeconds(5));
            int progressCountAfterDispose = progress.Count;
            await Task.Delay(200);
            int progressCountAfterQuietPeriod = progress.Count;
            int terminalCountAfterDestroy = published.Count(
                static item => item is GameTaskCompletedEvent or GameTaskFailedEvent);

            if (canceled.Succeeded || canceled.ErrorCode != ConnectionErrorCode.Canceled
                || terminalCountAfterDestroy != terminalCountBeforeDestroy + 1
                || progressCountAfterQuietPeriod != progressCountAfterDispose)
            {
                throw new InvalidOperationException(
                    $"Native task cancellation failed or leaked callbacks: result={canceled}; " +
                    $"terminals={terminalCountBeforeDestroy}->{terminalCountAfterDestroy}; " +
                    $"progress={progressCountAfterDispose}->{progressCountAfterQuietPeriod}; " +
                    $"events=[{string.Join(" | ", published.Select(static item => item.ToString()))}].");
            }

            return true;
        }
        finally
        {
            if (service is not null)
            {
                await service.DisposeAsync();
            }

            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_SCREENSHOT_PATH", previousScreenshot);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_INPUT_FAIL", previousInputFail);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_COMMANDS_FILE", previousCommandsFile);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_RESOLUTION", previousResolution);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_ORIENTATION", previousOrientation);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_DISPLAY_ORIENTATION", previousDisplayOrientation);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_DEVICES_SERIAL", previousDevicesSerial);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_DEVICES_MODE", previousDevicesMode);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_OFFLINE_THEN_READY", previousOfflineThenReady);
            Directory.Delete(appData, recursive: true);
        }
    }

    private static async Task<bool> ValidateNativeQueueTask(string fakeAdbPath)
    {
        string appData = CreateTemporaryDirectory();
        string commandLogPath = Path.Combine(appData, "commands.log");
        string definitionPath = Path.Combine(appData, "task.json");
        string templatePath = Path.Combine(appData, "home.png");
        string? previousScreenshot = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_SCREENSHOT_PATH");
        string? previousInputFail = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_INPUT_FAIL");
        string? previousCommandsFile = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_COMMANDS_FILE");
        string? previousResolution = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_RESOLUTION");
        string? previousOrientation = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_ORIENTATION");
        string? previousDisplayOrientation = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_DISPLAY_ORIENTATION");
        string? previousDevicesSerial = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_DEVICES_SERIAL");
        string? previousDevicesMode = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_DEVICES_MODE");
        string? previousOfflineThenReady = Environment.GetEnvironmentVariable("UMA_FAKE_ADB_OFFLINE_THEN_READY");
        UmaService? service = null;
        try
        {
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_COMMANDS_FILE", commandLogPath);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_SCREENSHOT_PATH", null);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_INPUT_FAIL", null);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_RESOLUTION", "900x1600");
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_ORIENTATION", "0");
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_DISPLAY_ORIENTATION", "0");
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_DEVICES_SERIAL", "test-serial");
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_DEVICES_MODE", null);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_OFFLINE_THEN_READY", null);

            service = new UmaService(new InlineEventDispatcher());
            await service.InitializeAsync(FindRepositoryResourceBaseDirectory(), appData);
            ConnectionTerminalEvent connection = await service.ConnectAsync(fakeAdbPath, "test-serial", "General");
            if (connection is not ConnectionSucceededEvent)
            {
                throw new InvalidOperationException($"FakeAdb connect failed: {connection}.");
            }

            DeviceOperationEvent captured = await service.CaptureAsync();
            if (captured is not FrameCapturedEvent { FrameLease: { } frame } frameCaptured
                || frameCaptured.PngBytes.Length == 0)
            {
                throw new InvalidOperationException($"FakeAdb capture failed: {captured}.");
            }

            using (frame)
            {
                await File.WriteAllBytesAsync(templatePath, frame.PngBytes);
                await WriteGameTaskDefinition(
                    definitionPath, templatePath, frameCaptured.Frame.Width, frameCaptured.Frame.Height,
                    cancellationWait: false);
            }

            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_SCREENSHOT_PATH", templatePath);

            string? catalogJson = await service.GetCatalogJsonAsync();
            if (string.IsNullOrEmpty(catalogJson))
            {
                throw new InvalidOperationException("GetCatalogJson returned empty result.");
            }

            string? initialSnapshot = await service.GetRunSnapshotJsonAsync();
            if (string.IsNullOrEmpty(initialSnapshot))
            {
                throw new InvalidOperationException("GetRunSnapshotJson returned empty initial snapshot.");
            }

            await service.ClearTasksAsync();

            var taskSettingsJson = JsonSerializer.Serialize(new { definitionPath });
            ulong taskId1 = await service.AppendTaskAsync("mail-collection", taskSettingsJson);
            if (taskId1 == 0)
            {
                throw new InvalidOperationException($"AppendTask 1 failed: id={taskId1}.");
            }

            ulong taskId2 = await service.AppendTaskAsync("mail-collection", taskSettingsJson);
            if (taskId2 <= taskId1)
            {
                throw new InvalidOperationException($"AppendTask 2 failed: id={taskId2}.");
            }

            await service.SetTaskParamsAsync(taskId2, taskSettingsJson);

            var published = new ConcurrentQueue<ConnectionEvent>();
            service.ConnectionEventReceived += published.Enqueue;

            QueueRunResult queueRunResult = await service.StartQueueAsync();
            if (!queueRunResult.Succeeded)
            {
                throw new InvalidOperationException($"StartQueue failed: result={queueRunResult}; events=[{string.Join(" | ", published)}].");
            }
            if (queueRunResult.TotalTasks != 2 || queueRunResult.SucceededTasks != 2
                || queueRunResult.FailedTasks != 0 || queueRunResult.SkippedTasks != 0)
                throw new InvalidOperationException($"Queue returned incorrect task counts: {queueRunResult}; snapshot={await service.GetRunSnapshotJsonAsync()}.");

            string? postSnapshot = await service.GetRunSnapshotJsonAsync();
            if (string.IsNullOrEmpty(postSnapshot))
            {
                throw new InvalidOperationException("GetRunSnapshotJson returned empty post-run snapshot.");
            }

            using (JsonDocument snapshot = JsonDocument.Parse(postSnapshot))
            {
                JsonElement[] taskSnapshots = snapshot.RootElement.GetProperty("tasks").EnumerateArray().ToArray();
                if (taskSnapshots.Length != 2 || taskSnapshots[0].GetProperty("id").GetUInt64() != taskId1
                    || taskSnapshots[1].GetProperty("id").GetUInt64() != taskId2
                    || taskSnapshots.Any(item => item.GetProperty("status").GetString() != "succeeded"))
                    throw new InvalidOperationException($"Queue snapshot did not preserve distinct task instances: {postSnapshot}");
            }

            // A task failure must not replay work through an alternate engine
            // or prevent the next task from completing.
            await service.ClearTasksAsync();
            ulong failedTask = await service.AppendTaskAsync("mail-collection",
                JsonSerializer.Serialize(new { definitionPath = Path.Combine(appData, "missing.json") }));
            ulong continuedTask = await service.AppendTaskAsync("mail-collection", taskSettingsJson);
            int eventOffset = published.Count;
            QueueRunResult continued = await service.StartQueueAsync();
            if (continued.TotalTasks != 2 || continued.SucceededTasks != 1 || continued.FailedTasks != 1
                || continued.SkippedTasks != 0)
                throw new InvalidOperationException($"Ordinary task failure did not continue the queue: {continued}.");
            ConnectionEvent[] continuationEvents = published.Skip(eventOffset).ToArray();
            if (continuationEvents.OfType<TaskFailedEvent>().Count(item => item.TaskId == failedTask) != 1
                || continuationEvents.OfType<TaskCompletedEvent>().Count(item => item.TaskId == continuedTask) != 1
                || continuationEvents.Count(item => item is QueueCompletedEvent or QueueFailedEvent) != 1)
                throw new InvalidOperationException("Queue did not publish exactly one terminal per task and run.");

            // Stop a running wait with remaining work and require the terminal
            // snapshot to mark that work skipped, including duplicate names.
            string waitingPath = Path.Combine(appData, "waiting.json");
            await WriteGameTaskDefinition(waitingPath, templatePath, 900, 1600, cancellationWait: true);
            await service.ClearTasksAsync();
            ulong waitingTask = await service.AppendTaskAsync("mail-collection",
                JsonSerializer.Serialize(new { definitionPath = waitingPath }));
            ulong skippedTask = await service.AppendTaskAsync("mail-collection", taskSettingsJson);
            using var cancellation = new CancellationTokenSource();
            void CancelWhenWaiting(ConnectionEvent item)
            {
                if (item is GameTaskProgressEvent { TaskName: "home", EventType: GameTaskEventType.ActionExecuting })
                    cancellation.Cancel();
            }
            service.ConnectionEventReceived += CancelWhenWaiting;
            try
            {
                QueueRunResult canceled = await service.StartQueueAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
                if (canceled.ErrorCode != ConnectionErrorCode.Canceled || canceled.SkippedTasks != 1)
                    throw new InvalidOperationException($"Queue cancellation did not skip pending work: {canceled}.");
                using JsonDocument stopped = JsonDocument.Parse(await service.GetRunSnapshotJsonAsync());
                JsonElement[] stoppedTasks = stopped.RootElement.GetProperty("tasks").EnumerateArray().ToArray();
                if (stoppedTasks.Length != 2 || stoppedTasks[0].GetProperty("id").GetUInt64() != waitingTask
                    || stoppedTasks[1].GetProperty("id").GetUInt64() != skippedTask
                    || stoppedTasks[1].GetProperty("status").GetString() != "skipped")
                    throw new InvalidOperationException("Cancellation snapshot did not identify the skipped task instance.");
            }
            finally
            {
                service.ConnectionEventReceived -= CancelWhenWaiting;
            }

            await service.ClearTasksAsync();
            return true;
        }
        finally
        {
            if (service is not null)
            {
                await service.DisposeAsync();
            }

            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_SCREENSHOT_PATH", previousScreenshot);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_INPUT_FAIL", previousInputFail);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_COMMANDS_FILE", previousCommandsFile);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_RESOLUTION", previousResolution);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_ORIENTATION", previousOrientation);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_DISPLAY_ORIENTATION", previousDisplayOrientation);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_DEVICES_SERIAL", previousDevicesSerial);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_DEVICES_MODE", previousDevicesMode);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_OFFLINE_THEN_READY", previousOfflineThenReady);
            Directory.Delete(appData, recursive: true);
        }
    }

    private static async Task<bool> ValidateCareerCheckpointResume(string fakeAdbPath)
    {
        if (!Path.IsPathFullyQualified(fakeAdbPath) || !File.Exists(fakeAdbPath))
        {
            throw new InvalidOperationException($"FakeAdb path is invalid: '{fakeAdbPath}'.");
        }

        string resourceRoot = FindRepositoryResourceBaseDirectory();
        string homeScreenshotPath = Path.Combine(
            resourceRoot, "testdata", "hachimi", "ura", "captures", "career_home_training_independently.png");
        if (!File.Exists(homeScreenshotPath))
        {
            throw new FileNotFoundException("Career Home replay screenshot is missing.", homeScreenshotPath);
        }

        string appData = CreateTemporaryDirectory();
        string checkpointDirectory = Path.Combine(appData, "checkpoints");
        string commandLogPath = Path.Combine(appData, "commands.log");
        const string serial = "test-serial";
        const string resourceVersion = "adb-capture-2026-08-13";
        Directory.CreateDirectory(checkpointDirectory);
        string checkpointPath = Path.Combine(checkpointDirectory, "independent-100101.json");
        string manifestPath = Path.Combine(resourceRoot, "resource", "hachimi", "ura", "manifest.json");
        string fingerprint = IndependentSettingsFingerprint();
        var settings = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["activityName"] = "com.cygames.umamusume.GameActivity",
            ["allowOptionalRaces"] = false,
            ["careerMode"] = "independent",
            ["checkpointDirectory"] = checkpointDirectory,
            ["continueExistingCareer"] = true,
            ["deleteExistingCareerData"] = false,
            ["friendSupportCardId"] = null,
            ["independentAgendaSelections"] = Array.Empty<string>(),
            ["independentLineupStrategy"] = "pace",
            ["independentSkillIds"] = Array.Empty<int>(),
            ["independentTrainingFocus"] = "balanced",
            ["legacyAptitudeSparks"] = Array.Empty<string>(),
            ["legacyAttributeSparks"] = Array.Empty<string>(),
            ["legacySelectionMode"] = "auto",
            ["manifestPath"] = manifestPath,
            ["normalEventHandling"] = "default",
            ["normalLineupStrategy"] = "pace",
            ["normalSkillIds"] = Array.Empty<int>(),
            ["normalTrainingRatio"] = new SortedDictionary<string, int>(StringComparer.Ordinal)
            {
                ["guts"] = 0,
                ["power"] = 0,
                ["speed"] = 2,
                ["stamina"] = 1,
                ["wit"] = 0,
            },
            ["packageId"] = "com.cygames.umamusume",
            ["pauseOnUnknownOutcome"] = true,
            ["retryFailedRaceWithAlarmClock"] = false,
            ["runCount"] = 1,
            ["strategyId"] = "default-speed-medium",
            ["supportCardIds"] = Array.Empty<int>(),
            ["supportDeckMode"] = "auto",
            ["supportDeckPreset"] = "custom",
            ["traineeId"] = 100101,
            ["useCachedLegacy"] = true,
            ["useLegacyGuest"] = true,
        };
        string settingsJson = JsonSerializer.Serialize(settings);
        var checkpoint = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["version"] = 2,
            ["stage"] = "HandlePostStartDialog",
            ["agendaIndex"] = 0,
            ["skillIndex"] = 0,
            ["currentSkillId"] = null,
            ["lastConfirmedScreen"] = "independent_start_issued",
            ["completionVerified"] = false,
            ["traineeId"] = 100101,
            ["adbSerial"] = serial,
            ["configurationFingerprint"] = fingerprint,
            ["resourceVersion"] = resourceVersion,
        };
        var environmentNames = new[]
        {
            "UMA_FAKE_ADB_SCREENSHOT_PATH",
            "UMA_FAKE_ADB_COMMANDS_FILE",
            "UMA_FAKE_ADB_CAPTURE_MODE",
            "UMA_FAKE_ADB_INPUT_FAIL",
            "UMA_FAKE_ADB_RESOLUTION",
            "UMA_FAKE_ADB_ORIENTATION",
            "UMA_FAKE_ADB_DISPLAY_ORIENTATION",
            "UMA_FAKE_ADB_DEVICES_SERIAL",
            "UMA_FAKE_ADB_DEVICES_MODE",
            "UMA_FAKE_ADB_OFFLINE_THEN_READY",
        };
        var previousEnvironment = environmentNames.ToDictionary(
            static name => name,
            static name => Environment.GetEnvironmentVariable(name),
            StringComparer.Ordinal);
        UmaService? service = null;
        try
        {
            await WriteDurableJson(checkpointPath, JsonSerializer.Serialize(checkpoint));
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_SCREENSHOT_PATH", homeScreenshotPath);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_COMMANDS_FILE", commandLogPath);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_CAPTURE_MODE", "png");
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_INPUT_FAIL", null);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_RESOLUTION", "900x1600");
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_ORIENTATION", "0");
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_DISPLAY_ORIENTATION", "0");
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_DEVICES_SERIAL", serial);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_DEVICES_MODE", null);
            Environment.SetEnvironmentVariable("UMA_FAKE_ADB_OFFLINE_THEN_READY", null);

            service = new UmaService(new InlineEventDispatcher());
            await service.InitializeAsync(resourceRoot, appData);
            ConnectionTerminalEvent connection = await service.ConnectAsync(fakeAdbPath, serial, "General");
            if (connection is not ConnectionSucceededEvent)
            {
                throw new InvalidOperationException($"FakeAdb connect failed: {connection}.");
            }

            var progress = new ConcurrentQueue<GameTaskProgressEvent>();
            var events = new ConcurrentQueue<ConnectionEvent>();
            service.ConnectionEventReceived += events.Enqueue;
            GameTaskRunResult result = await service.RunGameTaskAsync(
                "career-training", settingsJson, progress.Enqueue);
            if (!result.Succeeded || result.TaskId != "career-training"
                || result.TotalUnits != 1 || result.CompletedUnits != 1
                || progress.IsEmpty || progress.Any(item => item.TaskId != "career-training")
                || !progress.Any(item => item.TaskName == "independent_post_start_home_probe"))
            {
                throw new InvalidOperationException(
                    $"Native Career checkpoint-resume slice failed: result={result}; progress={progress.Count}; " +
                    $"events=[{string.Join(" | ", events.Select(static item => item.ToString()))}].");
            }

            if (!File.Exists(commandLogPath))
            {
                throw new FileNotFoundException("FakeAdb did not record its commands.", commandLogPath);
            }

            string[] commands = await File.ReadAllLinesAsync(commandLogPath);
            if (!commands.Any(static line => line.Contains("screencap", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Native Career did not capture the configured Home screenshot.");
            }

            string[] inputCommands = commands
                .Where(static line => line.Contains("\tinput\t", StringComparison.Ordinal))
                .ToArray();
            if (inputCommands.Length != 0)
            {
                throw new InvalidOperationException(
                    "A durable post-Start checkpoint must only verify Home and issue no game input: " +
                    string.Join(" | ", inputCommands));
            }

            using JsonDocument persisted = JsonDocument.Parse(await File.ReadAllTextAsync(checkpointPath));
            JsonElement root = persisted.RootElement;
            if (root.GetProperty("stage").GetString() != "Completed"
                || !root.GetProperty("completionVerified").GetBoolean()
                || root.GetProperty("lastConfirmedScreen").GetString() != "home"
                || root.GetProperty("adbSerial").GetString() != serial
                || root.GetProperty("traineeId").GetInt32() != 100101
                || root.GetProperty("configurationFingerprint").GetString() != fingerprint
                || root.GetProperty("resourceVersion").GetString() != resourceVersion)
            {
                throw new InvalidOperationException(
                    "Native Career did not durably record the verified Home result with the original checkpoint identity.");
            }

            return true;
        }
        finally
        {
            try
            {
                if (service is not null)
                {
                    await service.DisposeAsync();
                }
            }
            finally
            {
                foreach ((string name, string? value) in previousEnvironment)
                {
                    Environment.SetEnvironmentVariable(name, value);
                }

                Directory.Delete(appData, recursive: true);
            }
        }
    }

    private static string IndependentSettingsFingerprint()
    {
        var settings = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["allowOptionalRaces"] = false,
            ["careerMode"] = "independent",
            ["friendSupportCardId"] = null,
            ["independentAgendaSelections"] = Array.Empty<string>(),
            ["independentLineupStrategy"] = "pace",
            ["independentSkillIds"] = Array.Empty<int>(),
            ["independentTrainingFocus"] = "balanced",
            ["legacyAptitudeSparks"] = Array.Empty<string>(),
            ["legacyAttributeSparks"] = Array.Empty<string>(),
            ["legacySelectionMode"] = "auto",
            ["normalEventHandling"] = "default",
            ["normalLineupStrategy"] = "pace",
            ["normalSkillIds"] = Array.Empty<int>(),
            ["normalTrainingRatio"] = new SortedDictionary<string, int>(StringComparer.Ordinal)
            {
                ["guts"] = 0,
                ["power"] = 0,
                ["speed"] = 2,
                ["stamina"] = 1,
                ["wit"] = 0,
            },
            ["pauseOnUnknownOutcome"] = true,
            ["retryFailedRaceWithAlarmClock"] = false,
            ["runCount"] = 1,
            ["strategyId"] = "default-speed-medium",
            ["supportCardIds"] = Array.Empty<int>(),
            ["supportDeckMode"] = "auto",
            ["supportDeckPreset"] = "custom",
            ["traineeId"] = 100101,
            ["useCachedLegacy"] = true,
            ["useLegacyGuest"] = true,
        };
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings));
        ulong hash = 14695981039346656037UL;
        foreach (byte value in bytes)
        {
            hash ^= value;
            hash = unchecked(hash * 1099511628211UL);
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static async Task WriteDurableJson(string path, string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await using var output = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await output.WriteAsync(bytes);
        await output.FlushAsync();
        output.Flush(flushToDisk: true);
    }

    private static async Task WriteGameTaskDefinition(
        string definitionPath,
        string templatePath,
        int width,
        int height,
        bool cancellationWait)
    {
        Dictionary<string, object> tasks = cancellationWait
            ? new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["home"] = new { action = "Wait", waitMs = 30_000, success = true },
            }
            : new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["home"] = new
                {
                    template = templatePath,
                    algorithm = "MatchTemplate",
                    templThreshold = 0.99,
                    action = "ClickSelf",
                    timeoutMs = 2_000,
                    next = new[] { "done" },
                },
                ["done"] = new
                {
                    action = "ClickRect",
                    specificRect = new[] { 40, 60, 10, 10 },
                    success = true,
                },
            };

        var definition = new
        {
            schemaVersion = 1,
            name = "phase3-native-runtime-integration",
            referenceWidth = width,
            referenceHeight = height,
            tasks,
        };
        await File.WriteAllTextAsync(definitionPath, JsonSerializer.Serialize(definition));
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uma-bridge-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryResourceBaseDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string resourceDirectory = Path.Combine(
                directory.FullName, "resource", "hachimi", "common", "date_changed");
            if (File.Exists(Path.Combine(resourceDirectory, "title.png"))
                && File.Exists(Path.Combine(resourceDirectory, "body.png"))
                && File.Exists(Path.Combine(resourceDirectory, "ok.png"))
                && File.Exists(Path.Combine(resourceDirectory, "err.png"))
                && File.Exists(Path.Combine(resourceDirectory, "titlescreen.png")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find the repository resource base containing date-change recovery templates.");
    }

    private static void WriteResult(string scenario, bool success, string message, int? validatedCases = null) =>
        Console.Write(JsonSerializer.Serialize(new { scenario, success, message, validatedCases }));

    private sealed class InlineEventDispatcher : IEventDispatcher
    {
        public void Post(Action action) => action();
    }
}
