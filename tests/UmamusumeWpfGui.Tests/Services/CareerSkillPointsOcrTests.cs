using System.Reflection;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;
using Xunit.Abstractions;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerSkillPointsOcrTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("career_skill_race_day_sample.png", 74)]
    [InlineData("career_skill_race_day_662_sample.png", 662)]
    [InlineData("career_skill_race_day_490_sample.png", 490)]
    public async Task Race_day_points_use_the_production_ocr_path(string capture, int expected)
    {
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            CareerTestResourceResolver.FindWorkspaceRoot(), capture));
        Assert.NotNull(frame);
        var adb = DispatchProxy.Create<IAdbRuntime, ScreenshotReplay>();
        ((ScreenshotReplay)(object)adb).Frame = frame;
        var recognizer = new RecordingRecognizer(output);
        var visual = new AdbVisualPipelineRuntime(adb, new AsyncDelay(), recognizer);
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var connection = new LastVerifiedConnection("adb", "serial", "android", "version",
            frame.Width, frame.Height, frame.Width, frame.Height, DateTimeOffset.UnixEpoch);
        var context = new CareerFlowContext(connection, pack, true, null!, null!, "pace",
            new UraCareerSessionState(), new CareerObservation("race_day", 1), null,
            CancellationToken.None);

        Assert.Equal(expected, await new CareerSkillVisualAdapter(visual).ReadPointsAsync(context));
        if (expected == 490)
            Assert.Contains((111, 81), recognizer.Sizes);
    }

    public class ScreenshotReplay : DispatchProxy
    {
        public GrayImage Frame { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "DecodeRawScreenshotAsync"
                ? Task.FromResult(new AdbRuntimeQueryResult<AdbRawScreenshot>(
                    new(Frame.Width, Frame.Height, Frame.RgbaPixels!), []))
                : throw new InvalidOperationException($"Unexpected ADB operation: {targetMethod?.Name}");
    }

    private sealed class RecordingRecognizer(ITestOutputHelper output) : IScreenTextRecognizer
    {
        private readonly WindowsOcrTextRecognizer _recognizer = new();
        public List<(int Width, int Height)> Sizes { get; } = [];

        public async Task<ScreenTextRecognitionResult> RecognizeAsync(AdbRawScreenshot screenshot,
            string? language, CancellationToken cancellationToken = default)
        {
            Sizes.Add((screenshot.Width, screenshot.Height));
            var result = await _recognizer.RecognizeAsync(screenshot, language, cancellationToken);
            output.WriteLine($"OCR {screenshot.Width}x{screenshot.Height}: "
                + string.Join(" | ", result.Detections.Select(d => $"{d.Text}@{d.Bounds}")));
            return result;
        }
    }
}
