using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Platform;
using Umamusume.AppCore.Services;
using Xunit;
using Xunit.Abstractions;

namespace Umamusume.AppCore.Tests;

public class LiveBlueStacksTests
{
    private readonly ITestOutputHelper _output;

    public LiveBlueStacksTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task LiveBlueStacksEndToEndVerification()
    {
        // Only run when BlueStacks Air is available and UMA_LIVE_TEST=1
        string? runLive = Environment.GetEnvironmentVariable("UMA_LIVE_TEST");
        if (runLive != "1")
        {
            _output.WriteLine("Skipping live BlueStacks test. Set UMA_LIVE_TEST=1 to run.");
            return;
        }

        string adbPath = PlatformPaths.FindAdbExecutable();
        _output.WriteLine($"Using ADB: {adbPath}");
        Assert.True(File.Exists(adbPath), $"ADB not found at {adbPath}");

        var logger = new LogService();
        logger.LogReceived += entry => _output.WriteLine($"[{entry.Kind}] {entry.Message}");

        var connState = new ConnectionStateService();
        await using var automation = new UmaAutomationService(logger, connState);

        // Step 1: Initialize
        _output.WriteLine("Step 1: Initializing UmaAutomationService...");
        await automation.InitializeAsync();
        Assert.True(automation.IsInitialized);

        // Step 2: Connect
        _output.WriteLine("Step 2: Connecting to 127.0.0.1:5555...");
        bool connected = await automation.ConnectAsync(adbPath, "127.0.0.1:5555", "General");
        Assert.True(connected, "Failed to connect to BlueStacks Air.");
        Assert.NotNull(automation.CurrentDevice);
        _output.WriteLine($"Connected to device: Serial={automation.CurrentDevice.Serial}, Display={automation.CurrentDevice.Width}x{automation.CurrentDevice.Height}, Physical={automation.CurrentDevice.PhysicalWidth}x{automation.CurrentDevice.PhysicalHeight}");

        // Step 3: Capture Screenshot
        _output.WriteLine("Step 3: Capturing screenshot...");
        var screenshot = await automation.CaptureScreenshotAsync();
        Assert.NotNull(screenshot);
        Assert.True(screenshot.PngBytes.Length > 0);
        _output.WriteLine($"Captured frame: {screenshot.Width}x{screenshot.Height}, {screenshot.PngBytes.Length} PNG bytes");

        // Save screenshot to artifact/temp directory for inspection
        string testOutputDir = Path.Combine(AppContext.BaseDirectory, "test_output");
        Directory.CreateDirectory(testOutputDir);
        string screenPath = Path.Combine(testOutputDir, "bluestacks_live_screen.png");
        await File.WriteAllBytesAsync(screenPath, screenshot.PngBytes);
        _output.WriteLine($"Saved live screenshot to {screenPath}");

        // Step 4: Run OCR
        _output.WriteLine("Step 4: Running OCR via embedded Tesseract...");
        var ocr = await automation.RecognizeOcrAsync(screenshot, "eng");
        Assert.NotNull(ocr);
        _output.WriteLine($"OCR detected {ocr.Detections.Count} text elements:");
        foreach (var det in ocr.Detections.Take(15))
        {
            _output.WriteLine($"  [{det.Confidence:P0}] \"{det.Text}\" at ({det.Bounds.X},{det.Bounds.Y} {det.Bounds.Width}x{det.Bounds.Height})");
        }

        // Step 5: Start Game and Recognize Home Screen
        _output.WriteLine("Step 5: Testing StartGame pipeline...");
        bool gameResult = await automation.StartGameAndRecognizeHomeAsync();
        _output.WriteLine($"StartGame result: {gameResult}");

        // Capture after-task screenshot
        var postScreen = await automation.CaptureScreenshotAsync();
        if (postScreen != null)
        {
            string postPath = Path.Combine(testOutputDir, "bluestacks_after_start_game.png");
            await File.WriteAllBytesAsync(postPath, postScreen.PngBytes);
            _output.WriteLine($"Saved post-task screenshot to {postPath}");

            var postOcr = await automation.RecognizeOcrAsync(postScreen, "eng");
            if (postOcr != null)
            {
                _output.WriteLine($"Post-task OCR detected {postOcr.Detections.Count} elements:");
                foreach (var det in postOcr.Detections.Take(15))
                {
                    _output.WriteLine($"  [{det.Confidence:P0}] \"{det.Text}\"");
                }
            }
        }

        // Disconnect
        await automation.DisconnectAsync();
        Assert.False(automation.IsConnected);
        _output.WriteLine("Live BlueStacks test completed successfully!");
    }
}
