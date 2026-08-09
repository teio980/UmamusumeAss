using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class DeveloperToolsImageMatchTests
{
    [Fact]
    public void Current_page_contains_the_selected_crop_template()
    {
        var screen = new GrayImage(
            8,
            6,
            [
                20, 20, 20, 20, 20, 20, 20, 20,
                20, 20, 0, 255, 0, 20, 20, 20,
                20, 20, 255, 100, 255, 20, 20, 20,
                20, 20, 0, 255, 50, 20, 20, 20,
                20, 20, 20, 20, 20, 20, 20, 20,
                20, 20, 20, 20, 20, 20, 20, 20,
            ]);
        var template = GrayImageCodec.Crop(screen, new Int32Rect(2, 1, 3, 3));

        Assert.NotNull(template);

        var result = TemplateMatcher.Find(
            screen,
            template!,
            roi: null,
            threshold: 0.86,
            referenceWidth: screen.Width,
            referenceHeight: screen.Height);

        Assert.True(result.Found, $"Template score was {result.Score:0.000}.");
        Assert.Equal(2, result.X);
        Assert.Equal(1, result.Y);
    }

    [Fact]
    public async Task Daily_race_matcher_finds_an_opaque_system_reference_inside_a_runner_card()
    {
        const int templateWidth = 20;
        const int templateHeight = 20;
        var templatePixels = new byte[templateWidth * templateHeight * 4];
        for (var y = 0; y < templateHeight; y++)
        {
            for (var x = 0; x < templateWidth; x++)
            {
                var value = (byte)((x * 13 + y * 17) % 256);
                var offset = (y * templateWidth + x) * 4;
                templatePixels[offset] = value;
                templatePixels[offset + 1] = value;
                templatePixels[offset + 2] = value;
                templatePixels[offset + 3] = 255;
            }
        }

        var referencePath = Path.Combine(
            Path.GetTempPath(),
            $"umamusume-system-reference-{Guid.NewGuid():N}.png");
        try
        {
            var bitmap = BitmapSource.Create(
                templateWidth,
                templateHeight,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                templatePixels,
                templateWidth * 4);
            bitmap.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(referencePath))
            {
                encoder.Save(output);
            }

            const int screenWidth = 900;
            const int screenHeight = 1600;
            const int matchX = 28;
            const int matchY = 808;
            var screenPixels = Enumerable.Repeat(
                    (byte)20,
                    screenWidth * screenHeight)
                .ToArray();
            var template = GrayImageCodec.FromFile(referencePath);
            Assert.NotNull(template);
            for (var y = 0; y < template.Height; y++)
            {
                for (var x = 0; x < template.Width; x++)
                {
                    screenPixels[(matchY + y) * screenWidth + matchX + x] =
                        template.Pixels[y * template.Width + x];
                }
            }

            var connection = new LastVerifiedConnection(
                "adb",
                "emulator",
                "android",
                "test",
                screenWidth,
                screenHeight,
                screenWidth,
                screenHeight,
                DateTimeOffset.UtcNow);
            var result = await DailyRaceRunnerSelector.FindBestMatchAsync(
                new GrayImage(screenWidth, screenHeight, screenPixels),
                referencePath,
                connection);

            Assert.NotNull(result);
            Assert.True(result!.Found, $"Reference score was {result.Score:0.000}.");
            Assert.Equal(20, result.X);
            Assert.Equal(800, result.Y);
        }
        finally
        {
            if (File.Exists(referencePath))
                File.Delete(referencePath);
        }
    }

    [Fact]
    public async Task Daily_race_matcher_distinguishes_100601_runner_from_an_unrelated_page()
    {
        var root = FindSolutionRoot();
        var runnerScreenPath = Path.Combine(root, "debug", "live-umamusume.png");
        var unrelatedScreenPath = Path.Combine(root, "debug", "daily_race_current.png");
        var referencePath = Path.Combine(
            root,
            "resource",
            "uma",
            "system_reference",
            "100601.webp");

        // These captures are local diagnostic artifacts and intentionally not
        // checked into source control. The synthetic test above still runs in CI.
        if (!File.Exists(runnerScreenPath) || !File.Exists(unrelatedScreenPath))
            return;

        var runnerScreen = GrayImageCodec.FromFile(runnerScreenPath);
        var unrelatedScreen = GrayImageCodec.FromFile(unrelatedScreenPath);

        Assert.NotNull(runnerScreen);
        Assert.NotNull(unrelatedScreen);
        Assert.True(File.Exists(referencePath), $"Missing reference image: {referencePath}");

        var connection = new LastVerifiedConnection(
            "adb",
            "emulator",
            "android",
            "test",
            runnerScreen!.Width,
            runnerScreen.Height,
            runnerScreen.Width,
            runnerScreen.Height,
            DateTimeOffset.UtcNow);
        var runnerResult = await DailyRaceRunnerSelector.FindBestMatchAsync(
            runnerScreen,
            referencePath,
            connection);
        var unrelatedResult = await DailyRaceRunnerSelector.FindBestMatchAsync(
            unrelatedScreen!,
            referencePath,
            connection);

        Assert.NotNull(runnerResult);
        Assert.NotNull(unrelatedResult);
        Assert.True(
            runnerResult!.Found,
            $"Runner score was {runnerResult.Score:0.000} at ({runnerResult.X}, {runnerResult.Y}).");
        Assert.False(
            unrelatedResult!.Found,
            $"Unrelated page produced a false positive at score {unrelatedResult.Score:0.000}.");
        Assert.True(
            runnerResult.Score >= unrelatedResult.Score + 0.20,
            $"Runner score {runnerResult.Score:0.000} was not sufficiently separated from "
            + $"unrelated score {unrelatedResult.Score:0.000}.");
    }

    [Fact]
    public void Daily_race_sort_direction_templates_are_distinguishable_in_local_capture()
    {
        var root = FindSolutionRoot();
        var screenPath = Path.Combine(root, "debug", "live3-stage2.png");
        if (!File.Exists(screenPath))
            return;

        var screen = GrayImageCodec.FromFile(screenPath);
        var descending = GrayImageCodec.FromFile(Path.Combine(
            root,
            "resource",
            "hachimi",
            "templates",
            "daily_race",
            "runner_sort_desc.png"));
        var ascending = GrayImageCodec.FromFile(Path.Combine(
            root,
            "resource",
            "hachimi",
            "templates",
            "daily_race",
            "runner_sort_asc.png"));
        Assert.NotNull(screen);
        Assert.NotNull(descending);
        Assert.NotNull(ascending);

        var roi = new[] { 700, 1120, 200, 180 };
        var descendingResult = TemplateMatcher.Find(
            screen!, descending!, roi, 0, 900, 1600);
        var ascendingResult = TemplateMatcher.Find(
            screen, ascending!, roi, 0, 900, 1600);
        Console.WriteLine(
            $"Descending score {descendingResult.Score:0.000}; "
            + $"ascending score {ascendingResult.Score:0.000}.");

        Assert.True(
            descendingResult.Score > ascendingResult.Score,
            $"Descending {descendingResult.Score:0.000}; ascending {ascendingResult.Score:0.000}.");
    }

    [Fact]
    public void Daily_race_skip_template_matches_inside_the_live_button_roi()
    {
        var root = FindSolutionRoot();
        var screenPath = Path.Combine(root, "debug", "daily_race_skip_actual.png");
        if (!File.Exists(screenPath))
            return;

        var screen = GrayImageCodec.FromFile(screenPath);
        var template = GrayImageCodec.FromFile(Path.Combine(
            root,
            "resource",
            "hachimi",
            "templates",
            "team_race",
            "whiteskip.png"));
        Assert.NotNull(screen);
        Assert.NotNull(template);

        var result = TemplateMatcher.Find(
            screen!,
            template!,
            [620, 1360, 180, 240],
            0.78,
            900,
            1600);
        Console.WriteLine(
            $"Daily Race Skip score {result.Score:0.000} at ({result.X}, {result.Y}).");

        Assert.True(result.Found, $"Skip score was {result.Score:0.000}.");
        Assert.InRange(result.CenterX, 670, 740);
        Assert.InRange(result.CenterY, 1460, 1560);
    }

    [Fact]
    public void Daily_race_fixed_layout_buttons_are_page_gated_in_live_captures()
    {
        var root = FindSolutionRoot();
        var debug = Path.Combine(root, "debug");
        var photoModePath = Path.Combine(debug, "repeat_after_false_pass.png");
        var cases = new[]
        {
            ("pre-race", Path.Combine(root, "resource", "hachimi", "templates", "daily_race", "pre_race.png"),
                "pre_race_start_button.png", new[] { 400, 1370, 350, 200 }, 0.82),
            ("playback roster", Path.Combine(debug, "daily_race_multirace_after_loading.png"),
                "playback_race_button.png", new[] { 200, 1370, 500, 200 }, 0.82),
            ("result", Path.Combine(debug, "daily_race_multirace_result_screen.png"),
                "result_next_button.png", new[] { 200, 1370, 500, 200 }, 0.82),
            ("Moonlight result", Path.Combine(debug, "moonlight_result_current.png"),
                "result_next_moonlight_button.png", new[] { 200, 1370, 500, 200 }, 0.82),
            ("view results", Path.Combine(debug, "before_resume_preview.png"),
                "view_results_button.png", new[] { 150, 1300, 350, 300 }, 0.82),
            ("view result tap", Path.Combine(debug, "after_view_result_resume_failure.png"),
                "view_result_tap.png", new[] { 250, 1150, 400, 300 }, 0.82),
            ("daily sale shop", Path.Combine(debug, "cleanup_reward.png"),
                "daily_sale_shop_button.png", new[] { 430, 930, 430, 260 }, 0.82),
            ("reward", Path.Combine(debug, "daily_race_multirace_after_result_next.png"),
                "reward_next_button.png", new[] { 400, 1350, 450, 230 }, 0.80),
            ("daily-race return", Path.Combine(debug, "final_daily_race_state.png"),
                "daily_races_header.png", new[] { 0, 0, 450, 120 }, 0.82),
        };
        if (!File.Exists(photoModePath) || cases.Any(item => !File.Exists(item.Item2)))
            return;

        var photoMode = GrayImageCodec.FromFile(photoModePath);
        Assert.NotNull(photoMode);
        foreach (var (name, sourcePath, templateName, roi, threshold) in cases)
        {
            var source = GrayImageCodec.FromFile(sourcePath);
            var template = GrayImageCodec.FromFile(Path.Combine(
                root,
                "resource",
                "hachimi",
                "templates",
                "daily_race",
                templateName));
            Assert.NotNull(source);
            Assert.NotNull(template);

            var expected = TemplateMatcher.Find(source!, template!, roi, threshold, 900, 1600);
            var photoFalsePositive = TemplateMatcher.Find(
                photoMode!, template, roi, threshold, 900, 1600);
            Console.WriteLine(
                $"{name}: expected {expected.Score:0.000}; "
                + $"photo mode {photoFalsePositive.Score:0.000}.");

            Assert.True(expected.Found, $"{name} score was {expected.Score:0.000}.");
            Assert.False(
                photoFalsePositive.Found,
                $"{name} matched Photo mode at {photoFalsePositive.Score:0.000}.");
        }
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CMakePresets.json")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
