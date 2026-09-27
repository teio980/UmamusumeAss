using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class TemporaryCareerGoalOcrProbeTests
{
    [Fact]
    public async Task Probe()
    {
        using var image = Image.Load<Rgba32>(@"C:\Users\Owner\Documents\Codex\2026-09-27\05-51-16-570-info-task\work\career_current.png");
        image.Mutate(context => context.Crop(new Rectangle(307, 82, 536, 96)));
        var bytes = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(bytes);
        var recognized = await new WindowsOcrTextRecognizer().RecognizeAsync(
            new AdbRawScreenshot(image.Width, image.Height, bytes), "en-US");
        Assert.Fail(string.Join(" | ", recognized.Detections.Select(x => x.Text)));
    }
}
