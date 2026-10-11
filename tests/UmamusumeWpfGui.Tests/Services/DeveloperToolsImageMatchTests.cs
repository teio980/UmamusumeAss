using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class DeveloperToolsImageMatchTests
{
    [Fact]
    public void Support_autofill_confirmation_fixture_matches_the_fixed_ok_button_in_its_roi()
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var screen = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            root, "support_autofill_prompt_ura.png"));
        var template = GrayImageCodec.FromFile(CareerTestResourceResolver.ResolveBuiltInUraVisualResource(
            "templates/support_autofill_confirmation_support_autofill_ok.png"));

        Assert.NotNull(screen);
        Assert.NotNull(template);

        var result = TemplateMatcher.Find(
            screen!,
            template!,
            roi: [430, 940, 440, 220],
            threshold: 0.78,
            referenceWidth: 900,
            referenceHeight: 1600);

        Assert.True(result.Found, $"Auto-Fill confirmation score was {result.Score:0.000}.");
        Assert.InRange(result.CenterX, 600, 700);
        Assert.InRange(result.CenterY, 1000, 1100);
    }

    [Fact]
    public void Rest_confirmation_fixture_matches_the_rest_dialog_template()
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var screen = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            root, "rest_confirmation.png"));
        var template = GrayImageCodec.FromFile(CareerTestResourceResolver.ResolveBuiltInUraVisualResource(
            "templates/runtime_frames/rest_confirmation_dialog.png"));

        Assert.NotNull(screen);
        Assert.NotNull(template);

        var result = TemplateMatcher.Find(
            screen!,
            template!,
            roi: [0, 460, 900, 700],
            threshold: 0.82,
            referenceWidth: 900,
            referenceHeight: 1600);

        Assert.True(result.Found, $"Rest confirmation score was {result.Score:0.000}.");
    }

    [Fact]
    public void Event_choice_green_hoof_template_matches_only_the_first_option_roi()
    {
        var root = CareerTestResourceResolver.FindWorkspaceRoot();
        var screen = GrayImageCodec.FromFile(CareerTestResourceResolver.FindUraCapture(
            root, "support_event_choice_ura.png"));
        var template = GrayImageCodec.FromFile(CareerTestResourceResolver.ResolveBuiltInUraVisualResource(
            "templates/event_choice_green_hoof.png"));

        Assert.NotNull(screen);
        Assert.NotNull(template);

        var result = TemplateMatcher.Find(
            screen!,
            template!,
            roi: [30, 890, 840, 125],
            threshold: 0.78,
            referenceWidth: 900,
            referenceHeight: 1600);

        Assert.True(result.Found, $"Green hoof template score was {result.Score:0.000}.");
        Assert.InRange(result.CenterX, 45, 90);
        Assert.InRange(result.CenterY, 925, 985);
    }

    [Fact]
    public void Independent_add_skills_fixture_matches_the_real_text_not_the_old_blank_coordinate()
    {
        var root = FindSolutionRoot();
        var screen = LoadIndependentFixture(root, "skills-final-confirmation-add.png");
        var template = LoadIndependentTemplate(root, "skills_add.png");

        var result = TemplateMatcher.FindButton(
            screen,
            template,
            roi: [300, 500, 300, 800],
            threshold: 0.80,
            referenceWidth: 900,
            referenceHeight: 1600);

        Console.WriteLine($"Add Skills fixture match: found={result.Found}, score={result.Score:0.000000}, center={result.CenterX},{result.CenterY}");
        Assert.True(result.Found, $"Template score was {result.Score:0.000}.");
        Assert.InRange(result.CenterX, 420, 480);
        Assert.InRange(result.CenterY, 930, 985);
        Assert.False(
            result.CenterX is >= 415 and <= 455 && result.CenterY is >= 1190 and <= 1235,
            "The known blank click around (435,1214) must never be accepted.");
    }

    [Fact]
    public void Scaled_masked_head_match_uses_real_alpha_resource_and_records_normalized_input()
    {
        var root = FindSolutionRoot();
        var templatePath = Path.Combine(
            root,
            "resource",
            "hachimi",
            "career",
            "event",
            "templates",
            "inheritance_event_go.png");
        using var source = Image.Load<Rgba32>(templatePath);
        var minX = source.Width;
        var minY = source.Height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                if (source[x, y].A < 24)
                    continue;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        Assert.True(maxX >= minX && maxY >= minY);
        var headBottom = Math.Min(
            maxY,
            minY + Math.Max(1, (int)Math.Round((maxY - minY + 1) * 0.44)));
        var headMinX = source.Width;
        var headMaxX = -1;
        for (var y = minY; y <= headBottom; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                if (source[x, y].A < 24)
                    continue;
                headMinX = Math.Min(headMinX, x);
                headMaxX = Math.Max(headMaxX, x);
            }
        }

        Assert.True(headMaxX >= headMinX);
        using var normalized = source.Clone(context => context.Crop(new Rectangle(
            headMinX,
            minY,
            headMaxX - headMinX + 1,
            headBottom - minY + 1)));
        normalized.Mutate(context => context.Resize(new ResizeOptions
        {
            Size = new SixLabors.ImageSharp.Size(128, 128),
            Mode = SixLabors.ImageSharp.Processing.ResizeMode.Stretch,
        }));

        var templateRgba = new byte[128 * 128 * 4];
        normalized.CopyPixelDataTo(templateRgba);
        var templateGray = new byte[128 * 128];
        var screenGray = new byte[320 * 320];
        for (var y = 0; y < 320; y++)
        {
            for (var x = 0; x < 320; x++)
                screenGray[y * 320 + x] = (byte)((x * 19 + y * 31) % 251);
        }
        for (var index = 0; index < templateGray.Length; index++)
        {
            var offset = index * 4;
            var gray = (byte)((templateRgba[offset] * 299
                + templateRgba[offset + 1] * 587
                + templateRgba[offset + 2] * 114) / 1000);
            templateGray[index] = gray;
            var x = index % 128;
            var y = index / 128;
            screenGray[(124 + y) * 320 + 100 + x] = gray;
        }

        var screen = new GrayImage(320, 320, screenGray);
        var result = TemplateMatcher.FindScaledMasked(
            screen,
            templatePath,
            roi: [0, 0, 320, 320],
            threshold: 0.99,
            referenceWidth: 320,
            referenceHeight: 320,
            scaleCandidates: [1.0]);

        Assert.True(result.Found, $"Masked head score was {result.Score:0.000000}.");
        Assert.Equal(100, result.X);
        Assert.Equal(124, result.Y);
        Assert.InRange(result.Score, 0.99, 1.0);
    }

    [Fact]
    public void Independent_add_skills_template_is_absent_after_the_picker_opens()
    {
        var root = FindSolutionRoot();
        var screen = LoadIndependentFixture(root, "skills-picker-open.png");
        var template = LoadIndependentTemplate(root, "skills_add.png");

        var result = TemplateMatcher.FindButton(
            screen,
            template,
            roi: [300, 500, 300, 800],
            threshold: 0.80,
            referenceWidth: 900,
            referenceHeight: 1600);

        Assert.False(result.Found, $"Picker page produced a false Add Skills score of {result.Score:0.000}.");
    }

    [Fact]
    public void Independent_picker_fixture_contains_search_icon_in_the_verification_roi()
    {
        var root = FindSolutionRoot();
        var screen = LoadIndependentFixture(root, "skills-picker-open.png");
        var template = LoadIndependentTemplate(root, "skills_search_field.png");

        var result = TemplateMatcher.Find(
            screen,
            template,
            roi: [600, 1318, 60, 60],
            threshold: 0.80,
            referenceWidth: 900,
            referenceHeight: 1600);

        Assert.True(result.Found, $"Search field verification score was {result.Score:0.000}.");
        Assert.InRange(result.CenterX, 625, 655);
        Assert.InRange(result.CenterY, 1340, 1365);
    }

    [Fact]
    public void Independent_picker_fixture_contains_disabled_search_reset_in_the_tight_roi()
    {
        var root = FindSolutionRoot();
        var screen = LoadIndependentFixture(root, "skills-picker-open.png");
        var template = LoadIndependentTemplate(root, "skills_search_reset.png");

        var result = TemplateMatcher.Find(
            screen,
            template,
            roi: [650, 1300, 240, 120],
            threshold: 0.80,
            referenceWidth: 900,
            referenceHeight: 1600);

        Console.WriteLine($"Picker Reset match: found={result.Found}, score={result.Score:0.000000}, center={result.CenterX},{result.CenterY}");
        Assert.True(result.Found, $"Picker Reset score was {result.Score:0.000}.");
        Assert.InRange(result.CenterX, 755, 805);
        Assert.InRange(result.CenterY, 1335, 1380);
    }

    [Fact]
    public void Independent_skills_reset_fixture_reports_the_real_button_not_the_false_high_score()
    {
        var root = FindSolutionRoot();
        var screen = GrayImageCodec.FromFile(Path.Combine(
            root,
            "tests",
            "UmamusumeWpfGui.Tests",
            "Fixtures",
            "Independent",
            "skills-final-confirmation-reset.png"));
        var template = GrayImageCodec.FromFile(LoadIndependentTemplatePath(
            root, "skills_main_reset.png"));

        Assert.NotNull(screen);
        Assert.NotNull(template);

        var result = TemplateMatcher.FindButton(
            screen!,
            template!,
            roi: [400, 600, 360, 500],
            threshold: 0.80,
            referenceWidth: 900,
            referenceHeight: 1600);

        Console.WriteLine($"Reset fixture match: found={result.Found}, score={result.Score:0.000000}, xy={result.X},{result.Y}, size={result.Width}x{result.Height}");

        Assert.True(result.Found, $"Template score was {result.Score:0.000}.");
        Assert.InRange(result.CenterX, 540, 590);
        Assert.InRange(result.CenterY, 810, 845);
        Assert.InRange(result.Score, 0.80, 1.0);
        Assert.False(
            result.X + result.Width / 2 == 697
                && result.Y + result.Height / 2 == 546,
            "The known false match coordinate must never be accepted.");
    }

    [Fact]
    public void Independent_skills_reset_click_guard_rejects_unchanged_frame_and_accepts_row_change()
    {
        var root = FindSolutionRoot();
        var screen = GrayImageCodec.FromFile(Path.Combine(
            root,
            "tests",
            "UmamusumeWpfGui.Tests",
            "Fixtures",
            "Independent",
            "skills-final-confirmation-reset.png"));

        Assert.NotNull(screen);

        var match = new TemplateMatchResult(
            Found: true,
            Score: 0.91,
            X: 460,
            Y: 786,
            Width: 210,
            Height: 85);
        Assert.False(
            TemplateMatcher.HasMeaningfulChange(screen!, screen!, match),
            "A stale click must not be treated as a successful Reset transition.");

        var changedPixels = screen!.Pixels.ToArray();
        // Simulate the selected skill row disappearing after Reset.
        for (var y = 650; y < 760; y++)
        {
            for (var x = 40; x < 450; x++)
                changedPixels[y * screen.Width + x] = 240;
        }

        var changed = new GrayImage(screen.Width, screen.Height, changedPixels);
        Assert.True(
            TemplateMatcher.HasMeaningfulChange(screen, changed, match),
            "Removing the selected row must count as a successful Reset transition.");

        Assert.True(
            TemplateMatcher.HasPersistentChange(screen, changed, changed, match),
            "A row removal that remains in two settled frames is a valid Reset transition.");

        var pressed = screen.Pixels.ToArray();
        for (var y = match.Y; y < match.Y + match.Height; y++)
        {
            for (var x = match.X; x < match.X + match.Width; x++)
                pressed[y * screen.Width + x] = (byte)Math.Max(0, pressed[y * screen.Width + x] - 40);
        }

        var pressedFrame = new GrayImage(screen.Width, screen.Height, pressed);
        Assert.False(
            TemplateMatcher.HasPersistentChange(screen, pressedFrame, screen, match),
            "A transient pressed frame must not count as a persistent Reset transition.");
    }

    [Fact]
    public void Independent_skills_reset_no_reset_fixture_rejects_the_header_decoration()
    {
        var root = FindSolutionRoot();
        var screen = GrayImageCodec.FromFile(Path.Combine(
            root,
            "tests",
            "UmamusumeWpfGui.Tests",
            "Fixtures",
            "Independent",
            "skills-final-confirmation-no-reset.png"));
        var template = GrayImageCodec.FromFile(LoadIndependentTemplatePath(
            root, "skills_main_reset.png"));

        Assert.NotNull(screen);
        Assert.NotNull(template);

        var result = TemplateMatcher.FindButton(
            screen!,
            template!,
            roi: [400, 600, 360, 500],
            threshold: 0.80,
            referenceWidth: 900,
            referenceHeight: 1600);

        Console.WriteLine($"No-reset fixture match: found={result.Found}, score={result.Score:0.000000}, xy={result.X},{result.Y}");
        Assert.False(result.Found, "A no-Reset page must not produce a clickable match.");
        Assert.False(
            result.CenterX is >= 790 and <= 820 && result.CenterY is >= 710 and <= 740,
            "The known header decoration must never become the Reset click.");
    }

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
    public void Current_page_contains_a_scaled_system_reference_template()
    {
        const int templateWidth = 8;
        const int templateHeight = 8;
        const int screenWidth = 32;
        const int screenHeight = 24;
        const int matchX = 10;
        const int matchY = 8;
        const int targetWidth = 4;
        const int targetHeight = 4;

        var templatePixels = new byte[templateWidth * templateHeight];
        for (var y = 0; y < templateHeight; y++)
        {
            for (var x = 0; x < templateWidth; x++)
                templatePixels[y * templateWidth + x] = (byte)((x * 23 + y * 31) % 256);
        }

        var screenPixels = Enumerable.Repeat(
                (byte)20,
                screenWidth * screenHeight)
            .ToArray();
        for (var y = 0; y < targetHeight; y++)
        {
            for (var x = 0; x < targetWidth; x++)
            {
                var templateX = x * templateWidth / targetWidth;
                var templateY = y * templateHeight / targetHeight;
                screenPixels[(matchY + y) * screenWidth + matchX + x] =
                    templatePixels[templateY * templateWidth + templateX];
            }
        }

        var result = TemplateMatcher.FindScaled(
            new GrayImage(screenWidth, screenHeight, screenPixels),
            new GrayImage(templateWidth, templateHeight, templatePixels),
            roi: null,
            threshold: 0.86,
            referenceWidth: screenWidth,
            referenceHeight: screenHeight,
            scaleCandidates: [0.50]);

        Assert.True(result.Found, $"Scaled template score was {result.Score:0.000}.");
        Assert.Equal(matchX, result.X);
        Assert.Equal(matchY, result.Y);
        Assert.Equal(targetWidth, result.Width);
        Assert.Equal(targetHeight, result.Height);
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
            "pipelines",
            "templates",
            "daily_race",
            "runner_sort_desc.png"));
        var ascending = GrayImageCodec.FromFile(Path.Combine(
            root,
            "resource",
            "hachimi",
            "pipelines",
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
            "pipelines",
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
            ("pre-race", Path.Combine(root, "resource", "hachimi", "pipelines", "templates", "daily_race", "pre_race.png"),
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
                "pipelines",
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

    private static GrayImage LoadIndependentFixture(string root, string name)
    {
        var image = GrayImageCodec.FromFile(Path.Combine(
            root,
            "tests",
            "UmamusumeWpfGui.Tests",
            "Fixtures",
            "Independent",
            name));
        return image ?? throw new InvalidOperationException($"Fixture '{name}' could not be loaded.");
    }

    private static GrayImage LoadIndependentTemplate(string root, string name)
    {
        var image = GrayImageCodec.FromFile(LoadIndependentTemplatePath(root, name));
        return image ?? throw new InvalidOperationException($"Template '{name}' could not be loaded.");
    }

    private static string LoadIndependentTemplatePath(string root, string name) =>
        CareerTestResourceResolver.ResolveBuiltInUraVisualResource(
            $"templates/independent/{name}");

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

    private static void SaveOpaqueGrayscalePng(
        string path,
        int width,
        int height,
        byte[] pixels)
    {
        var bgra = new byte[checked(width * height * 4)];
        for (var index = 0; index < pixels.Length; index++)
        {
            var offset = index * 4;
            bgra[offset] = pixels[index];
            bgra[offset + 1] = pixels[index];
            bgra[offset + 2] = pixels[index];
            bgra[offset + 3] = 255;
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            bgra,
            width * 4);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
