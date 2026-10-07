using System.Diagnostics;
using System.Reflection;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;
using Xunit.Abstractions;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraSmartTrainingMatcherTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("turn3_training_selection.png")]
    [InlineData("training_selection_partner_ura.png")]
    [InlineData("year2_nhk_training_select.png")]
    [InlineData("training_selection_stamina_summer_failure92.png")]
    [InlineData("training_selection_speed_failure28.png")]
    [InlineData("training_selection_speed_summer_failure95.png")]
    [InlineData("smart_runtime_20261006/speed-1.png")]
    [InlineData("smart_runtime_20261006/stamina-1.png")]
    [InlineData("smart_runtime_20261006/power-1.png")]
    [InlineData("smart_runtime_20261006/guts-1.png")]
    [InlineData("smart_runtime_20261006/wit-1.png")]
    public async Task Exhaustive_smart_matcher_preserves_all_five_original_matches(string capture)
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            CareerTestResourceResolver.FindWorkspaceRoot(), capture))!;
        await CompareAsync(pack, frame);
    }

    internal async Task CompareAsync(UraScenarioPack pack, GrayImage frame)
    {
        // Isolate exhaustive color-matcher equivalence from the independent
        // selected-card marker validation in the smart detector.
        pack.ExecutionDefinition.Tasks.Remove("training_selection_selected_chevron");
        var runtime = new AdbVisualPipelineRuntime(
            DispatchProxy.Create<IAdbRuntime, CareerOcrReuseTests.NeverCallProxy>(), new AsyncDelay(),
            new WindowsOcrTextRecognizer());
        var watch = Stopwatch.StartNew();
        var expected = await new UraTrainingSelectionHeightDetector(runtime)
            .DetectFrameAsync(frame, pack, CancellationToken.None);
        var oldMs = watch.ElapsedMilliseconds;
        watch.Restart();
        var actual = await new UraSmartTrainingHeightDetector(runtime)
            .DetectFrameAsync(frame, pack, CancellationToken.None);
        output.WriteLine($"Original matching={oldMs}ms, smart matching={watch.ElapsedMilliseconds}ms; raised={actual.RaisedType}");
        Assert.Equal(expected.RaisedType, actual.RaisedType);
        Assert.Equal(expected.ScreenChanged, actual.ScreenChanged);
        Assert.Equal(expected.Matches, actual.Matches);
    }

    [Fact]
    public void Color_only_change_is_not_reused_as_an_old_match()
    {
        var template = new GrayImage(1, 1, [80], [255, 0, 0, 255]);
        var first = new GrayImage(2, 1, [80, 80], [255, 0, 0, 255, 0, 255, 0, 255]);
        var second = first with { RgbaPixels = [0, 255, 0, 255, 255, 0, 0, 255] };
        var match = UraSmartTrainingColorMatcher.Find(first, template, null, .99, 2, 1);
        Assert.Equal(0, match.X);
        var changed = UraSmartTrainingColorMatcher.Find(second, template, null, .99, 2, 1);
        Assert.Equal(1, changed.X);
        Assert.True(changed.Found);
    }

    [Fact]
    public void Random_color_alpha_rois_preserve_exhaustive_results_and_first_ties()
    {
        var random = new Random(281);
        for (var i = 0; i < 80; i++)
        {
            var pixels = new byte[20 * 15 * 4];
            var colors = new byte[4 * 3 * 4];
            random.NextBytes(pixels);
            random.NextBytes(colors);
            var frame = new GrayImage(20, 15, new byte[300], pixels);
            var template = new GrayImage(4, 3, new byte[12], colors);
            int[]? roi = i % 2 == 0 ? null : [-2, 1, 18, 12];
            Assert.Equal(TemplateMatcher.FindColor(frame, template, roi, .7, 20, 15),
                UraSmartTrainingColorMatcher.Find(frame, template, roi, .7, 20, 15));
        }
        var blank = new GrayImage(1, 1, [0], [0, 0, 0, 255]);
        Assert.Equal(TemplateMatcher.FindColor(new(4, 4, new byte[16], new byte[64]), blank, null, .7, 4, 4),
            UraSmartTrainingColorMatcher.Find(new(4, 4, new byte[16], new byte[64]), blank, null, .7, 4, 4));
    }

    [Fact]
    public void Cancellation_propagates_before_matching()
    {
        var frame = new GrayImage(1, 1, [0]);
        Assert.Throws<OperationCanceledException>(() => UraSmartTrainingColorMatcher.Find(
            frame, frame, null, .7, 1, 1, new CancellationToken(true)));
    }
}
