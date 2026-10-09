using System.Reflection;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;
using Xunit.Abstractions;

namespace UmamusumeWpfGui.Tests.Services;

/// <summary>Uses actual Windows OCR and the existing optional Tesseract, not annotations.</summary>
public sealed class UraSmartTrainingRealOcrTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("speed", 18, 0, 9, 0, 0, 5)]
    [InlineData("stamina", 0, 14, 0, 7, 0, 4)]
    public async Task Latest_failed_run_merges_both_actual_frames_with_all_fields_known(
        string type, int speed, int stamina, int power, int guts, int wit, int skill)
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var runtime = new AdbVisualPipelineRuntime(
            DispatchProxy.Create<IAdbRuntime, CareerOcrReuseTests.NeverCallProxy>(), new AsyncDelay(),
            new WindowsOcrTextRecognizer());
        var reader = new UraSmartTrainingCandidateReader(runtime);
        var detector = new UraSmartTrainingHeightDetector(runtime);
        var results = new List<UraTrainingCandidate>();
        for (var sample = 1; sample <= 2; sample++)
        {
            var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
                CareerTestResourceResolver.FindWorkspaceRoot(),
                $"smart_runtime_20261006/budget-{type}-{sample}.png"))!;
            var selection = await detector.DetectFrameAsync(frame, pack, CancellationToken.None);
            Assert.Equal(type, selection.RaisedType);
            results.Add(await reader.ReadAsync(pack, frame, type, CancellationToken.None,
                selectedLogo: selection.Matches.Single(item => item.TrainingType == type).Match,
                optimize: true, reusePreviousRead: sample == 2));
        }
        var merged = UraSmartTrainingCandidateReader.MergeStable(results[0], results[1]);
        Assert.Equal(new int?[] { speed, stamina, power, guts, wit, skill, 0 },
            new[] { merged.SpeedGain, merged.StaminaGain, merged.PowerGain, merged.GutsGain,
                merged.WitGain, merged.SkillPointGain, merged.FailureRatePercent });
        Assert.True(merged.IsSafe);
    }

    [Theory]
    [InlineData("smart_runtime_20261007/speed-1.png", "speed", 32, 0, 21, 0, 0, 9, 0)]
    [InlineData("smart_runtime_20261007/speed-2.png", "speed", 32, 0, 21, 0, 0, 9, 0)]
    [InlineData("smart_runtime_20261007/stamina-1.png", "stamina", 0, 18, 0, 11, 0, 6, 0)]
    [InlineData("smart_runtime_20261007/stamina-2.png", "stamina", 0, 18, 0, 11, 0, 6, 0)]
    [InlineData("smart_runtime_20261007/guts-1.png", "guts", 6, 0, 6, 11, 0, 5, 0)]
    [InlineData("smart_runtime_20261007/guts-2.png", "guts", 6, 0, 6, 11, 0, 5, 0)]
    [InlineData("smart_runtime_20261007/finale-stamina.png", "stamina", 0, 23, 0, 13, 0, 7, 0)]
    [InlineData("training_selection_ura.png", "speed", 17, 0, 10, 0, 0, 6, 0)]
    [InlineData("training_selection_biwa_summer.png", "speed", 28, 0, 19, 0, 0, 7, 0)]
    [InlineData("ura_train_selection.png", "speed", 28, 0, 12, 0, 0, 5, 0)]
    [InlineData("ura_late_nov_training_select.png", "speed", 12, 0, 12, 0, 0, 5, 0)]
    [InlineData("ura_late_jul_training_select.png", "speed", 19, 0, 21, 0, 0, 8, 0)]
    [InlineData("year2_nhk_training_select.png", "speed", 21, 0, 10, 0, 0, 4, 0)]
    [InlineData("training_selection_stamina_summer_failure92.png", "stamina", 0, 30, 0, 14, 0, 7, 92)]
    [InlineData("training_selection_speed_failure28.png", "speed", 20, 0, 9, 0, 0, 5, 28)]
    [InlineData("training_selection_speed_summer_failure95.png", "speed", 20, 0, 9, 0, 0, 4, 95)]
    [InlineData("smart_runtime_20261006/wit-speed-five.png", "wit", 5, 0, 0, 0, 16, 8, 0)]
    [InlineData("smart_runtime_20261006/adb-guts-fifteen.png", "guts", 13, 0, 8, 15, 0, 6, 0)]
    [InlineData("smart_runtime_20261006/budget-speed-1.png", "speed", 18, 0, 9, 0, 0, 5, 0)]
    [InlineData("smart_runtime_20261006/budget-speed-2.png", "speed", 18, 0, 9, 0, 0, 5, 0)]
    [InlineData("smart_runtime_20261006/budget-stamina-1.png", "stamina", 0, 14, 0, 7, 0, 4, 0)]
    [InlineData("smart_runtime_20261006/budget-stamina-2.png", "stamina", 0, 14, 0, 7, 0, 4, 0)]
    public async Task Smart_batch_matches_real_numbers_and_reuses_an_identical_second_frame(
        string capture, string type, int speed, int stamina, int power, int guts, int wit, int skill, int failure)
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            CareerTestResourceResolver.FindWorkspaceRoot(), capture));
        Assert.NotNull(frame);
        var runtime = new AdbVisualPipelineRuntime(
            DispatchProxy.Create<IAdbRuntime, CareerOcrReuseTests.NeverCallProxy>(), new AsyncDelay(),
            new WindowsOcrTextRecognizer());
        var selection = await new UraTrainingSelectionHeightDetector(runtime)
            .DetectFrameAsync(frame, pack, CancellationToken.None);
        Assert.Equal(type, selection.RaisedType);
        var logo = selection.Matches.Single(item => item.TrainingType == type).Match;
        var reader = new UraSmartTrainingCandidateReader(runtime);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var first = await reader.ReadAsync(pack, frame, type, CancellationToken.None,
            selectedLogo: logo, optimize: true);
        output.WriteLine($"{capture}: first OCR={started.ElapsedMilliseconds}ms, Windows calls={reader.LastWindowsOcrCalls}, fallback calls={reader.LastFallbackCalls}");
        foreach (var item in reader.LastReadings)
            output.WriteLine($"{item.Key}: value={item.Value.Value}, source={item.Value.Source}, raw={item.Value.RawText}");
        Assert.Equal(new int?[] { speed, stamina, power, guts, wit, skill, failure },
            new[] { first.SpeedGain, first.StaminaGain, first.PowerGain, first.GutsGain,
                first.WitGain, first.SkillPointGain, first.FailureRatePercent });
        Assert.True(first.HasReliableCoreValues);
        Assert.True(first.HasReliableFailureRate);
        Assert.InRange(reader.LastWindowsOcrCalls, 0, 1);
        started.Restart();
        // A distinct screenshot buffer prevents reference-identity shortcuts.
        var secondFrame = frame with { Pixels = (byte[])frame.Pixels.Clone(),
            RgbaPixels = (byte[])frame.RgbaPixels!.Clone() };
        var second = await reader.ReadAsync(pack, secondFrame, type, CancellationToken.None,
            selectedLogo: logo, optimize: true, reusePreviousRead: true);
        output.WriteLine($"{capture}: second OCR={started.ElapsedMilliseconds}ms, reused={reader.LastReusedFields}");
        Assert.Equal(first, UraSmartTrainingCandidateReader.MergeStable(first, second));
        Assert.Equal(0, reader.LastWindowsOcrCalls);
        Assert.Equal(0, reader.LastFallbackCalls);
        Assert.Equal(7, reader.LastReusedFields);
    }

    [Theory]
    [InlineData("training_selection_ura.png", 17, 10, 6)]
    [InlineData("training_selection_biwa_summer.png", 28, 19, 7)]
    [InlineData("ura_train_selection.png", 28, 12, 5)]
    [InlineData("ura_late_nov_training_select.png", 12, 12, 5)]
    [InlineData("ura_late_jul_training_select.png", 19, 21, 8)]
    [InlineData("year2_nhk_training_select.png", 21, 10, 4)]
    public async Task Existing_speed_previews_are_read_using_shared_ocr(
        string capture, int speed, int power, int skill)
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            CareerTestResourceResolver.FindWorkspaceRoot(), capture));
        Assert.NotNull(frame);
        var runtime = new AdbVisualPipelineRuntime(
            DispatchProxy.Create<IAdbRuntime, CareerOcrReuseTests.NeverCallProxy>(), new AsyncDelay(),
            new WindowsOcrTextRecognizer());
        var selection = await new UraTrainingSelectionHeightDetector(runtime)
            .DetectFrameAsync(frame, pack, CancellationToken.None);
        Assert.True(selection.Succeeded, selection.Error);
        Assert.Equal("speed", selection.RaisedType);
        var reader = new UraSmartTrainingCandidateReader(runtime);
        var candidate = await reader.ReadAsync(pack, frame, "speed", CancellationToken.None,
            selectedLogo: selection.Matches.Single(item => item.TrainingType == "speed").Match);
        foreach (var field in reader.LastReadings)
            output.WriteLine($"{capture}: {field.Key} raw='{field.Value.RawText}' value={field.Value.Value} source={field.Value.Source}");
        Assert.Equal(speed, candidate.SpeedGain);
        Assert.Equal(power, candidate.PowerGain);
        Assert.Equal(skill, candidate.SkillPointGain);
        Assert.Equal(0, candidate.StaminaGain);
        Assert.Equal(0, candidate.GutsGain);
        Assert.Equal(0, candidate.WitGain);
        Assert.Equal(0, candidate.FailureRatePercent);
        Assert.True(candidate.IsSafe);
        Assert.Null(candidate.TrainingLevel);
    }

    [Theory]
    [InlineData("training_selection_stamina_summer_failure92.png", "stamina", 0, 30, 0, 14, 0, 7, 92)]
    [InlineData("training_selection_speed_failure28.png", "speed", 20, 0, 9, 0, 0, 5, 28)]
    [InlineData("training_selection_speed_summer_failure95.png", "speed", 20, 0, 9, 0, 0, 4, 95)]
    public async Task Temp_previews_reuse_highest_logo_and_shared_numeric_reader(
        string capture, string type, int speed, int stamina, int power, int guts, int wit, int skill, int failure)
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            CareerTestResourceResolver.FindWorkspaceRoot(), capture));
        Assert.NotNull(frame);
        var runtime = new AdbVisualPipelineRuntime(
            DispatchProxy.Create<IAdbRuntime, CareerOcrReuseTests.NeverCallProxy>(), new AsyncDelay(),
            new WindowsOcrTextRecognizer());
        var selection = await new UraTrainingSelectionHeightDetector(runtime)
            .DetectFrameAsync(frame, pack, CancellationToken.None);
        Assert.True(selection.Succeeded, selection.Error);
        Assert.Equal(type, selection.RaisedType);
        var reader = new UraSmartTrainingCandidateReader(runtime);
        var candidate = await reader.ReadAsync(pack, frame, selection.RaisedType!, CancellationToken.None,
            selectedLogo: selection.Matches.Single(item => item.TrainingType == selection.RaisedType).Match);
        foreach (var field in reader.LastReadings)
            output.WriteLine($"{capture}: {field.Key} raw='{field.Value.RawText}' value={field.Value.Value} source={field.Value.Source}");
        Assert.Equal(speed, candidate.SpeedGain);
        Assert.Equal(stamina, candidate.StaminaGain);
        Assert.Equal(power, candidate.PowerGain);
        Assert.Equal(guts, candidate.GutsGain);
        Assert.Equal(wit, candidate.WitGain);
        Assert.Equal(skill, candidate.SkillPointGain);
        Assert.Equal(failure, candidate.FailureRatePercent);
        Assert.True(candidate.HasReliableCoreValues);
        Assert.True(candidate.HasReliableFailureRate);
        Assert.False(candidate.IsSafe);
    }
}
