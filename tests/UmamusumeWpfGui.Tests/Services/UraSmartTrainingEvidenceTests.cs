using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraSmartTrainingEvidenceTests
{
    [Fact]
    public async Task Reference_capture_distinguishes_blank_columns_from_visible_gains()
    {
        var candidate = await ReadReferenceAsync();

        Assert.Equal(17, candidate.SpeedGain);
        Assert.Equal(0, candidate.StaminaGain);
        Assert.Equal(10, candidate.PowerGain);
        Assert.Equal(0, candidate.GutsGain);
        Assert.Equal(0, candidate.WitGain);
        Assert.Equal(6, candidate.SkillPointGain);
        Assert.Equal(0, candidate.FailureRatePercent);
        Assert.True(candidate.IsSafe);
    }

    [Theory]
    [InlineData("speed")]
    [InlineData("failure_rate")]
    public async Task Failed_ocr_remains_unknown_even_with_a_valid_gain_strip(string missingField)
    {
        var candidate = await ReadReferenceAsync(missingField);

        Assert.False(candidate.IsSafe);
        if (missingField == "speed")
            Assert.Null(candidate.SpeedGain);
        else
            Assert.Null(candidate.FailureRatePercent);
    }

    [Fact]
    public void Disagreeing_frames_exclude_the_candidate()
    {
        var first = UraTrainingCandidate.Reliable("speed", 17, 0, 10, 0, 0, 6, 0);
        var second = first with { FailureRatePercent = 6 };

        var candidate = UraSmartTrainingCandidateReader.MergeStable(first, second);

        Assert.Null(candidate.FailureRatePercent);
        Assert.False(candidate.IsSafe);
    }

    private static async Task<UraTrainingCandidate> ReadReferenceAsync(string? missingField = null)
    {
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName,
                   "resource", "hachimi", "ura", "manifest.json")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var frame = GrayImageCodec.FromFile(Path.Combine(directory.FullName,
            "resource", "hachimi", "ura", "testdata", "captures", "runtime_frames",
            "training_selection_ura.png"));
        Assert.NotNull(frame);
        var runtime = DispatchProxy.Create<IVisualPipelineRuntime, AnnotatedOcrRuntime>();
        ((AnnotatedOcrRuntime)(object)runtime).MissingField = missingField;
        return await new UraSmartTrainingCandidateReader(runtime,
            (_, _, _, _, _, _, _) => Task.FromResult<int?>(null)).ReadAsync(
            pack, frame, "speed", CancellationToken.None);
    }

    // These are manually verified OCR annotations. This tests the real
    // screenshot's blank-column evidence, not Windows OCR accuracy.
    public class AnnotatedOcrRuntime : DispatchProxy
    {
        public string? MissingField { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != "DetectTextAsync")
                throw new InvalidOperationException($"Unexpected operation: {targetMethod?.Name}");
            var task = args!.OfType<string>().Last();
            var field = task[(task.LastIndexOf('.') + 1)..];
            if (field == MissingField)
                return Task.FromResult<ScreenTextRecognitionResult?>(null);
            var text = field switch
            {
                "speed" => "+17",
                "power" => "+10",
                "skill_points" => "+6",
                "failure_rate" => "Failure 0%",
                "training_level" => "Speed Lvl 1",
                _ => null,
            };
            IReadOnlyList<ScreenTextDetection> detections = text is null
                ? []
                : [new(text, new ScreenTextRect(0, 0, 40, 30))];
            return Task.FromResult<ScreenTextRecognitionResult?>(
                new(detections, "en-US", 900, 1600));
        }
    }
}
