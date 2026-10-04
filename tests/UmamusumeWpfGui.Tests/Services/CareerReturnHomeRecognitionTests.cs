using System.IO;
using System.Reflection;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerReturnHomeRecognitionTests
{
    [Theory]
    [InlineData(true, "home", "career_complete")]
    [InlineData(false, "home_unselected", "career_complete")]
    [InlineData(true, "home", "career_complete_close")]
    [InlineData(false, "home_unselected", "career_complete_close")]
    [InlineData(true, "home", "career_story_unlocked")]
    [InlineData(false, "home_unselected", "career_story_unlocked")]
    [InlineData(true, "home", "career_story_unlocked_compact")]
    [InlineData(false, "home_unselected", "career_story_unlocked_compact")]
    [InlineData(true, "home", "career_story_unlocked_to_home")]
    [InlineData(false, "home_unselected", "career_story_unlocked_to_home")]
    public async Task Return_home_observer_distinguishes_selected_and_unselected_tabs(
        bool selected,
        string expectedScreenId,
        string completionScreenId)
    {
        var selectedPixels = Enumerable.Range(0, 144)
            .Select(index => (byte)((index * 73 + index / 12 * 31) % 256))
            .ToArray();
        var selectedImage = new GrayImage(12, 12, selectedPixels);
        var unselectedImage = new GrayImage(12, 12,
            selectedPixels.Select(pixel => (byte)(255 - pixel)).ToArray());
        var runtime = ReturnHomeRuntime.Create(
            selected ? selectedImage : unselectedImage,
            selectedImage,
            unselectedImage);
        var observer = new CareerScreenObserver(runtime);
        var profile = new UraScreenProfile
        {
            ReferenceWidth = 12,
            ReferenceHeight = 12,
            Screens =
            [
                new UraScreenDefinition
                {
                    ScreenId = "home",
                    Flow = "entry",
                    Recognition = new UraScreenRecognition
                    {
                        Template = "home-selected.png",
                        Roi = [0, 0, 12, 12],
                        TemplateThreshold = 0.9,
                        Stable = true,
                    },
                },
                new UraScreenDefinition
                {
                    ScreenId = "home_unselected",
                    Flow = "entry",
                    Recognition = new UraScreenRecognition
                    {
                        Template = "home-unselected.png",
                        Roi = [0, 0, 12, 12],
                        TemplateThreshold = 0.9,
                        Stable = true,
                    },
                },
            ],
        };
        var pack = new UraScenarioPack(
            string.Empty,
            Path.GetTempPath(),
            string.Empty,
            string.Empty,
            null!, null!, null!, null!, null!,
            profile,
            new HachimiPipelineDefinition());
        var connection = new LastVerifiedConnection(
            "adb", "serial", "android", "version", 12, 12, 12, 12,
            DateTimeOffset.UnixEpoch);
        var state = new UraCareerSessionState
        {
            CareerStarted = true,
            LastScreenId = completionScreenId,
        };

        var observation = await observer.ObserveAsync(
            connection, pack, state, false, CancellationToken.None);

        Assert.Equal(expectedScreenId, observation?.ScreenId);
        state.LastScreenId = "home_unselected";
        Assert.Equal(expectedScreenId,
            (await observer.ObserveAsync(connection, pack, state, false,
                CancellationToken.None))?.ScreenId);
        state.LastScreenId = "career_main";
        Assert.Null(await observer.ObserveAsync(
            connection, pack, state, false, CancellationToken.None));
    }

    public class ReturnHomeRuntime : DispatchProxy
    {
        private GrayImage _frame = null!;
        private GrayImage _selected = null!;
        private GrayImage _unselected = null!;

        public static IVisualPipelineRuntime Create(
            GrayImage frame,
            GrayImage selected,
            GrayImage unselected)
        {
            var runtime = DispatchProxy.Create<IVisualPipelineRuntime, ReturnHomeRuntime>();
            var proxy = (ReturnHomeRuntime)(object)runtime;
            proxy._frame = frame;
            proxy._selected = selected;
            proxy._unselected = unselected;
            return runtime;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "CaptureGrayAsync" => Task.FromResult<GrayImage?>(_frame),
                "LoadTemplateAsync" => Task.FromResult<GrayImage?>(
                    ((string)args![0]!).EndsWith("home-unselected.png", StringComparison.Ordinal)
                        ? _unselected
                        : _selected),
                "DelayAsync" => Task.CompletedTask,
                _ => throw new InvalidOperationException($"Unexpected call: {targetMethod?.Name}"),
            };
    }
}
