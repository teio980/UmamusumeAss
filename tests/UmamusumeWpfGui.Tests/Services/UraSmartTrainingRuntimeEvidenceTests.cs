using System.Diagnostics;
using System.Reflection;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;
using Xunit.Abstractions;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraSmartTrainingRuntimeEvidenceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("speed", 19, 0, 9, 0, 0, 6)]
    [InlineData("stamina", 0, 13, 0, 7, 0, 4)]
    [InlineData("power", 0, 10, 13, 0, 0, 5)]
    [InlineData("guts", 12, 0, 11, 15, 0, 7)]
    [InlineData("wit", 4, 0, 0, 0, 17, 8)]
    public async Task Actual_slow_run_frames_have_readable_failure_rates(
        string type, int speed, int stamina, int power, int guts, int wit, int skill)
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var runtime = new AdbVisualPipelineRuntime(
            DispatchProxy.Create<IAdbRuntime, CareerOcrReuseTests.NeverCallProxy>(), new AsyncDelay(),
            new WindowsOcrTextRecognizer());
        var detector = new UraSmartTrainingHeightDetector(runtime);
        var reader = new UraSmartTrainingCandidateReader(runtime);
        for (var sample = 1; sample <= 2; sample++)
        {
            var frame = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
                CareerTestResourceResolver.FindWorkspaceRoot(), $"smart_runtime_20261006/{type}-{sample}.png"))!;
            var selection = await detector.DetectFrameAsync(frame, pack, CancellationToken.None);
            output.WriteLine($"{type}-{sample}: raised={selection.RaisedType}; error={selection.Error}");
            // Sparkles obscure stamina/guts sample 2. Independent marker/row
            // evidence must recover the actual card on these captured frames.
            Assert.Equal(type, selection.RaisedType);
            var logo = selection.Matches.Single(item => item.TrainingType == type).Match;
            output.WriteLine($"Selected logo: {logo}");
            var watch = Stopwatch.StartNew();
            var result = await reader.ReadAsync(pack, frame, type, CancellationToken.None,
                selectedLogo: logo, optimize: true, reusePreviousRead: sample == 2);
            output.WriteLine($"OCR elapsed={watch.ElapsedMilliseconds}ms, fallback calls={reader.LastFallbackCalls}, reuse={reader.LastReusedFields}");
            foreach (var item in reader.LastReadings)
                output.WriteLine($"{item.Key}: {item.Value}");
            Assert.Equal(new int?[] { speed, stamina, power, guts, wit, skill, 0 },
                new[] { result.SpeedGain, result.StaminaGain, result.PowerGain, result.GutsGain,
                    result.WitGain, result.SkillPointGain, result.FailureRatePercent });
            Assert.True(result.HasReliableCoreValues);
            Assert.True(result.HasReliableFailureRate);
        }
    }
}
