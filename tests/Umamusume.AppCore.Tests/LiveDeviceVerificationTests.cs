using System;
using System.IO;
using System.Threading.Tasks;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Platform;
using Umamusume.AppCore.Services;
using Umamusume.CoreBridge;
using Xunit;

namespace Umamusume.AppCore.Tests;

public sealed class LiveDeviceVerificationTests
{
    private const string AdbPath = "/Applications/BlueStacks.app/Contents/MacOS/hd-adb";
    private const string Serial = "127.0.0.1:5555";
    private const string TargetPackage = "com.cygames.umamusume";

    [Fact]
    public async Task LiveBlueStacksConnectCaptureOcrAndVerify()
    {
        if (!File.Exists(AdbPath))
        {
            return; // Skip if BlueStacks is not installed
        }

        var logger = new LogService();
        var connectionState = new ConnectionStateService();
        var service = new UmaAutomationService(logger, connectionState);

        await service.InitializeAsync();
        Assert.True(service.IsInitialized, "Core engine should be initialized");

        bool connected = await service.ConnectAsync(AdbPath, Serial, "BlueStacks");
        Assert.True(connected, "Should successfully connect to BlueStacks Air at 127.0.0.1:5555");
        Assert.True(service.IsConnected);
        Assert.NotNull(service.CurrentDevice);
        Assert.Equal(Serial, service.CurrentDevice.Serial);
        Assert.True(service.CurrentDevice.Width > 0);
        Assert.True(service.CurrentDevice.Height > 0);

        // Capture screenshot
        var screenshot = await service.CaptureScreenshotAsync();
        Assert.NotNull(screenshot);
        Assert.True(screenshot.Width > 0);
        Assert.True(screenshot.Height > 0);
        Assert.True(screenshot.RgbaPixels != null && screenshot.RgbaPixels.Length > 0);

        // Run OCR on the captured screenshot
        var ocrResult = await service.RecognizeOcrAsync(screenshot, "eng");
        Assert.NotNull(ocrResult);
        Assert.Equal(screenshot.Width, ocrResult.Width);
        Assert.Equal(screenshot.Height, ocrResult.Height);

        // Disconnect
        await service.DisconnectAsync();
        Assert.False(service.IsConnected);
    }

    [Fact]
    public async Task LiveBlueStacksStartGameAndRecognizeHome()
    {
        if (!File.Exists(AdbPath))
        {
            return; // Skip if BlueStacks is not installed
        }

        var logger = new LogService();
        var logs = new System.Collections.Generic.List<string>();
        logger.LogReceived += entry => {
            string line = $"[{entry.Kind}] {entry.Message}";
            logs.Add(line);
            Console.WriteLine(line);
        };
        var connectionState = new ConnectionStateService();
        var service = new UmaAutomationService(logger, connectionState);

        await service.InitializeAsync();
        Assert.True(service.IsInitialized);

        bool connected = await service.ConnectAsync(AdbPath, Serial, "BlueStacks");
        Assert.True(connected, "Should successfully connect to BlueStacks Air at 127.0.0.1:5555");
        Assert.True(service.IsConnected);

        // Execute StartGame and verify Home screen detection (allow up to 180s for network & downloads)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        bool succeeded = await service.StartGameAndRecognizeHomeAsync(cts.Token);
        
        string allLogs = string.Join("\n", logs);
        Assert.True(succeeded, $"Start game and recognize home should succeed. Current status: {service.CurrentStatus}\nLogs:\n{allLogs}");
        Assert.False(service.IsBusy);

        await service.DisconnectAsync();
        Assert.False(service.IsConnected);
    }

    [Fact]
    public async Task LiveBlueStacksCancellationAndDisconnect()
    {
        if (!File.Exists(AdbPath))
        {
            return; // Skip if BlueStacks is not installed
        }

        var logger = new LogService();
        var connectionState = new ConnectionStateService();
        var service = new UmaAutomationService(logger, connectionState);

        await service.InitializeAsync();
        bool connected = await service.ConnectAsync(AdbPath, Serial, "BlueStacks");
        Assert.True(connected);

        // Verify pre-canceled operation handling
        using var canceledCts = new CancellationTokenSource();
        canceledCts.Cancel();

        bool startResult = await service.StartGameAndRecognizeHomeAsync(canceledCts.Token);
        Assert.False(startResult);
        Assert.False(service.IsBusy);

        // Verify clean disconnect
        await service.DisconnectAsync();
        Assert.False(service.IsConnected);
        Assert.Null(service.CurrentDevice);

        // Verify reconnect
        bool reconnected = await service.ConnectAsync(AdbPath, Serial, "BlueStacks");
        Assert.True(reconnected);
        Assert.True(service.IsConnected);
        Assert.NotNull(service.CurrentDevice);

        await service.DisconnectAsync();
        Assert.False(service.IsConnected);
    }
}
