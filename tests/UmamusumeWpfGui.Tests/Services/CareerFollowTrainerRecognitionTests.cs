using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerFollowTrainerRecognitionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recreation_confirmation_is_not_canceled_as_a_follow_limit_popup(bool resume)
    {
        var pack = await LoadPackAsync();
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var frame = Load(CareerTestResourceResolver.FindUraCapture(root, "recreation_confirmation.png"));
        var observer = new CareerScreenObserver(CapturedFrameRuntime.Create(frame));
        var state = new UraCareerSessionState
        {
            CareerStarted = !resume,
            LastScreenId = resume ? "unknown" : "career_main",
            LastAction = UraPlannedAction.Recreation,
        };

        var observation = await observer.ObserveAsync(
            Connection(), pack, state, false, CancellationToken.None, careerOnly: resume);

        Assert.Equal("recreation_confirmation", observation?.ScreenId);
        Assert.Equal(CareerScreenKind.Turn, observation?.Kind);
    }

    [Theory]
    [InlineData("recreation_confirmation.png")]
    [InlineData("rest_confirmation.png")]
    [InlineData("summer_rest_dialog.png")]
    [InlineData("infirmary_confirm.png")]
    public async Task Follow_title_rejects_other_green_confirmation_headers(string capture)
    {
        var pack = await LoadPackAsync();
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var popup = pack.ScreenProfile.Find("follow_trainer_limit")!;
        var recognition = popup.Recognition;
        var template = Load(pack.VisualResources!.ResolveScreenTemplate(
            popup, recognition.Template!));
        var frame = Load(CareerTestResourceResolver.FindUraCapture(root, capture));
        var match = recognition.MatchColorText
            ? TemplateMatcher.FindColor(frame, template, recognition.Roi,
                recognition.TemplateThreshold, 900, 1600, requireTextContrast: true)
            : TemplateMatcher.Find(frame, template, recognition.Roi,
                recognition.TemplateThreshold, 900, 1600);

        Assert.False(match.Found, $"Follow Trainer matched {capture}: score={match.Score:0.000}.");
    }

    [Theory]
    [InlineData("career_result_close", false, true)]
    [InlineData("follow_trainer_limit", false, true)]
    [InlineData("unknown", true, true)]
    [InlineData("career_main", false, false)]
    public async Task Genuine_follow_popup_is_available_in_settlement_and_on_resume(
        string previousScreen, bool resume, bool expected)
    {
        var pack = await LoadPackAsync();
        var popup = pack.ScreenProfile.Find("follow_trainer_limit")!;
        // Retain the previous screen's metadata so the phase gate can
        // classify settlement history while probing the popup.
        pack = pack with
        {
            ScreenProfile = new UraScreenProfile
            {
                Screens = [popup, pack.ScreenProfile.Find("career_result_close")!],
                VisualResources = pack.ScreenProfile.VisualResources,
            },
        };
        var frame = new GrayImage(900, 1600,
            Enumerable.Repeat((byte)255, 900 * 1600).ToArray(),
            Enumerable.Repeat((byte)255, 900 * 1600 * 4).ToArray());
        Stamp(frame, Load(pack.VisualResources!.ResolveScreenTemplate(
            popup, popup.Recognition.Template!)), 320, 480);
        Stamp(frame, Load(pack.VisualResources.ResolveScreenTemplate(
            popup, popup.Recognition.RequiredTemplate!)), 190, 1020);
        var observer = new CareerScreenObserver(CapturedFrameRuntime.Create(frame));
        var state = new UraCareerSessionState
        {
            CareerStarted = !resume,
            LastScreenId = previousScreen,
        };

        var observation = await observer.ObserveAsync(
            Connection(), pack, state, false, CancellationToken.None, careerOnly: resume);

        Assert.Equal(expected ? "follow_trainer_limit" : null, observation?.ScreenId);
    }

    private static void Stamp(GrayImage frame, GrayImage template, int x, int y)
    {
        for (var row = 0; row < template.Height; row++)
        {
            Array.Copy(template.Pixels, row * template.Width,
                frame.Pixels, (y + row) * frame.Width + x, template.Width);
            Array.Copy(template.RgbaPixels!, row * template.Width * 4,
                frame.RgbaPixels!, ((y + row) * frame.Width + x) * 4, template.Width * 4);
        }
    }

    private static LastVerifiedConnection Connection() => new(
        "adb", "serial", "android", "version", 900, 1600, 900, 1600, DateTimeOffset.UnixEpoch);

    private static GrayImage Load(string path) => GrayImageCodec.FromFile(path)
        ?? throw new FileNotFoundException("Missing test image.", path);

    private static Task<UraScenarioPack> LoadPackAsync() =>
        CareerTestResourceResolver.LoadBuiltInUraPackAsync();

    public class CapturedFrameRuntime : DispatchProxy
    {
        private GrayImage _frame = null!;

        public static IVisualPipelineRuntime Create(GrayImage frame)
        {
            var runtime = Create<IVisualPipelineRuntime, CapturedFrameRuntime>();
            ((CapturedFrameRuntime)(object)runtime)._frame = frame;
            return runtime;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "CaptureGrayAsync" => Task.FromResult<GrayImage?>(_frame),
                "LoadTemplateAsync" => Task.FromResult(GrayImageCodec.FromFile((string)args![0]!)),
                "DelayAsync" => Task.CompletedTask,
                _ => throw new InvalidOperationException($"Unexpected call: {targetMethod?.Name}"),
            };
    }
}
