using System.Reflection;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;
using Xunit.Abstractions;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraWitTrainingReadTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("turn3_training_selection.png", "speed")]
    [InlineData("training_selection_partner_ura.png", "speed")]
    [InlineData("year2_nhk_training_select.png", "speed")]
    [InlineData("training_selection_stamina_summer_failure92.png", "stamina")]
    [InlineData("training_selection_speed_failure28.png", "speed")]
    [InlineData("training_selection_speed_summer_failure95.png", "speed")]
    public async Task Selected_card_verification_preserves_other_training_screen_variants(
        string capture, string expectedType)
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            CareerTestResourceResolver.FindWorkspaceRoot(), capture))!;
        var runtime = CareerTrainingClickGuardTests.TrainingFrameRuntime.Create(frame);
        var selection = await new UraSmartTrainingHeightDetector(runtime)
            .DetectFrameAsync(frame, pack, CancellationToken.None);
        output.WriteLine($"{capture}: raised={selection.RaisedType}; error={selection.Error}");
        Assert.Equal(expectedType, selection.RaisedType);
    }

    [Fact]
    public async Task Wit_with_a_duel_badge_reads_both_selected_frames_and_merges_all_gains()
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var runtime = new AdbVisualPipelineRuntime(
            DispatchProxy.Create<IAdbRuntime, CareerOcrReuseTests.NeverCallProxy>(), new AsyncDelay(),
            new WindowsOcrTextRecognizer());
        var detector = new UraSmartTrainingHeightDetector(runtime);
        var reader = new UraSmartTrainingCandidateReader(runtime);
        var connection = new LastVerifiedConnection("offline", "offline", "offline", "android",
            900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);
        var candidates = new List<UraTrainingCandidate>();
        for (var sample = 1; sample <= 2; sample++)
        {
            var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
                CareerTestResourceResolver.FindWorkspaceRoot(), $"smart_runtime_20261006/wit-duel-{sample}.png"))!;
            var selection = await detector.DetectFrameAsync(frame, pack, CancellationToken.None);
            output.WriteLine($"Selection: raised={selection.RaisedType}; error={selection.Error}");
            Assert.Equal("wit", selection.RaisedType);
            var tap = UraSmartTrainingConfirmationTap.Create(pack, connection, selection, "wit");
            Assert.NotNull(tap);
            Assert.InRange(tap.CenterX, 745, 787);
            candidates.Add(await reader.ReadAsync(pack, frame, "wit", CancellationToken.None,
                selectedLogo: selection.Matches.Single(item => item.TrainingType == "wit").Match,
                optimize: true, reusePreviousRead: sample == 2));
            foreach (var field in reader.LastReadings)
                output.WriteLine($"sample {sample}, {field.Key}: {field.Value}");
        }
        var candidate = UraSmartTrainingCandidateReader.MergeStable(candidates[0], candidates[1]);
        Assert.Equal(new int?[] { 4, 0, 0, 0, 13, 6, 0 },
            new[] { candidate.SpeedGain, candidate.StaminaGain, candidate.PowerGain, candidate.GutsGain,
                candidate.WitGain, candidate.SkillPointGain, candidate.FailureRatePercent });
        Assert.True(candidate.IsSafe);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_duplicate_selected_marker_keeps_the_picker_unverified(bool duplicate)
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            CareerTestResourceResolver.FindWorkspaceRoot(), "smart_runtime_20261006/wit-duel-1.png"))!;
        for (var y = 1340; y < 1485; y++)
        {
            if (duplicate)
            {
                Array.Copy(frame.Pixels, y * frame.Width + 724, frame.Pixels, y * frame.Width + 62, 86);
                Array.Copy(frame.RgbaPixels!, (y * frame.Width + 724) * 4,
                    frame.RgbaPixels!, (y * frame.Width + 62) * 4, 86 * 4);
            }
            else
            {
                Array.Fill(frame.Pixels, (byte)255, y * frame.Width + 675, 180);
                Array.Fill(frame.RgbaPixels!, (byte)255, (y * frame.Width + 675) * 4, 180 * 4);
            }
        }
        var runtime = new AdbVisualPipelineRuntime(
            DispatchProxy.Create<IAdbRuntime, CareerOcrReuseTests.NeverCallProxy>(), new AsyncDelay(),
            new WindowsOcrTextRecognizer());
        var selection = await new UraSmartTrainingHeightDetector(runtime)
            .DetectFrameAsync(frame, pack, CancellationToken.None);

        Assert.Null(selection.RaisedType);
        Assert.False(selection.ScreenChanged);
    }
}
