using System.Runtime.InteropServices;
using System.Text.Json;
using Umamusume.CoreBridge;

namespace Umamusume.CoreBridge.IntegrationHost;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string scenario = args.Length > 0 ? args[0] : "missing-scenario";
        try
        {
            bool success = await RunScenario(scenario, args.Length > 1 ? args[1] : "");
            WriteResult(scenario, success, success ? "passed" : "scenario returned false");
            return success ? 0 : 1;
        }
        catch (Exception exception)
        {
            WriteResult(scenario, false, $"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static Task<bool> RunScenario(string scenario, string fakeAdbPath) => scenario switch
    {
        "load" => Task.FromResult(ProbeLoad()),
        "default-resource" => ValidateDefaultResourceInitialization(),
        "corrupt-resource" => ExpectCorruptResourceFailure(),
        "valid-resource" => ValidateResourceInitialization(),
        "s2-stubs" => Task.FromResult(ValidateS2Stubs()),
        "fake-adb-connect" => ValidateFakeAdbConnect(fakeAdbPath),
        _ => throw new ArgumentException($"Unknown scenario: {scenario}", nameof(scenario)),
    };

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

    private static bool ValidateS2Stubs()
    {
        string appData = CreateTemporaryDirectory();
        try
        {
            var native = new UmaCoreBridgeNative();
            var callbackCount = 0;
            UmaApiCallback callback = (_, _, _) => callbackCount++;
            if (native.SetUserDir(appData) != 0 || native.LoadResource(AppContext.BaseDirectory) != 0)
            {
                return false;
            }

            using SafeUmaHandle handle = native.Create(callback, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return false;
            }

            var destination = new byte[1];
            int invalid = (int)ConnectionErrorCode.InvalidArgument;
            return native.VerifyGame(handle, "invalid.package").ErrorCode == invalid
                && native.Capture(handle).ErrorCode == invalid
                && native.GetFramePngSize(handle, 1, out _) == invalid
                && native.CopyFramePng(handle, 1, destination) == invalid
                && native.ReleaseFrame(handle, 1) == invalid
                && native.Tap(handle, 1, 1, 1).ErrorCode == invalid
                && native.Swipe(handle, 1, 1, 1, 2, 2, 100).ErrorCode == invalid
                && callbackCount == 0;
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

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"uma-bridge-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteResult(string scenario, bool success, string message) =>
        Console.Write(JsonSerializer.Serialize(new { scenario, success, message }));

    private sealed class InlineEventDispatcher : IEventDispatcher
    {
        public void Post(Action action) => action();
    }
}
