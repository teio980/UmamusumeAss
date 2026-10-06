using System.Reflection;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraSmartTrainingGainGlyphTests
{
    [Fact]
    public void Actual_plus_five_rejects_an_extra_digit_without_changing_the_gain_limit()
    {
        var image = Prepare("wit-speed-five.png", [25, 965, 152, 65]);
        Assert.True(UraSmartTrainingGainGlyphValidator.Matches(image, 5));
        Assert.False(UraSmartTrainingGainGlyphValidator.Matches(image, 35));
        var guts = Prepare("adb-guts-fifteen.png", [457, 965, 140, 65]);
        Assert.True(UraSmartTrainingGainGlyphValidator.Matches(guts, 15));
        Assert.False(UraSmartTrainingGainGlyphValidator.Matches(guts, 141));
        Assert.False(UraSmartTrainingGainGlyphValidator.Matches(guts, 5));
    }

    [Fact]
    public void Real_three_digit_glyph_geometry_is_allowed_and_missing_plus_is_rejected()
    {
        // Prepared geometry, not a hard numeric cap: plus and three digits.
        var image = new GrayImage(9, 1, [0, 255, 0, 255, 0, 255, 0, 255, 255]);
        Assert.True(UraSmartTrainingGainGlyphValidator.Matches(image, 141));
        Assert.False(UraSmartTrainingGainGlyphValidator.Matches(image, 14));
        Assert.False(UraSmartTrainingGainGlyphValidator.Matches(new(1, 1, [0]), 5));
        Assert.False(UraSmartTrainingGainGlyphValidator.Matches(new(2, 1, [255, 255]), 0));
    }

    [Fact]
    public async Task Incorrect_windows_and_fallback_values_stay_unknown_and_are_not_cached()
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            CareerTestResourceResolver.FindWorkspaceRoot(), "smart_runtime_20261006/wit-speed-five.png"))!;
        var runtime = DispatchProxy.Create<IVisualPipelineRuntime, UraSmartTrainingPerformanceTests.BatchRuntime>();
        var fake = (UraSmartTrainingPerformanceTests.BatchRuntime)(object)runtime;
        fake.Result = new([new("+35", new(24, 24, 40, 20))], "en-US");
        var calls = 0;
        var reader = new UraSmartTrainingCandidateReader(runtime, (_, _, _, _, _, _, _) =>
        {
            calls++;
            return Task.FromResult<int?>(35);
        });
        var first = await reader.ReadAsync(pack, frame, "wit", CancellationToken.None, optimize: true);
        Assert.Null(first.SpeedGain);
        var before = calls;
        var second = await reader.ReadAsync(pack, frame, "wit", CancellationToken.None,
            optimize: true, reusePreviousRead: true);
        Assert.Null(second.SpeedGain);
        Assert.True(calls > before);
        Assert.False(UraSmartTrainingCandidateReader.MergeStable(first, second).IsSafe);
    }

    private static GrayImage Prepare(string file, int[] roi)
    {
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            CareerTestResourceResolver.FindWorkspaceRoot(), "smart_runtime_20261006/" + file))!;
        var crop = CareerNumericOcrReader.Crop(frame, roi, 900, 1600)!;
        return UraTrainingNumberImagePreprocessor.Prepare(crop, true,
            (r, g, b) => r >= 240 && g is >= 95 and <= 225 && b <= 95 && r >= g + 25)!;
    }
}
