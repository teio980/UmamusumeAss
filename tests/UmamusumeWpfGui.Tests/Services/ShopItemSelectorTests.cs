using System.IO;
using System.Reflection;
using System.Windows;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class ShopItemSelectorTests
{
    [Fact]
    public void Finds_shop_button_on_live_home_screen()
    {
        var root = FindRoot();
        var frame = GrayImageCodec.FromFile(Path.Combine(
            root, "tests", "UmamusumeWpfGui.Tests", "Fixtures", "Shop", "home-shop-button.png"));
        var template = GrayImageCodec.FromFile(Path.Combine(
            root, "resource", "hachimi", "pipelines", "templates", "shop", "shop_home_button.png"));
        Assert.NotNull(frame);
        Assert.NotNull(template);

        var match = TemplateMatcher.Find(
            frame!, template!, [160, 1200, 300, 260], 0.86, 900, 1600);

        Assert.True(match.Found,
            $"Home Shop button score {match.Score:0.000} at ({match.CenterX},{match.CenterY}).");
        Assert.InRange(match.CenterX, 290, 330);
        Assert.InRange(match.CenterY, 1350, 1390);
    }

    [Fact]
    public void Shop_probe_recognizes_daily_sales_screen()
    {
        var root = FindRoot();
        var frame = GrayImageCodec.FromFile(Path.Combine(
            root, "tests", "UmamusumeWpfGui.Tests", "Fixtures", "Shop", "daily-sales-actual-top.png"));
        var template = GrayImageCodec.FromFile(Path.Combine(
            root, "resource", "hachimi", "pipelines", "templates", "shop", "daily_sales_header.png"));
        Assert.NotNull(frame);
        Assert.NotNull(template);

        var match = TemplateMatcher.Find(
            frame!, template!, [580, 60, 300, 110], 0.86, 900, 1600);

        Assert.True(match.Found,
            $"Daily Sales Shop title score {match.Score:0.000}.");
    }

    [Fact]
    public void Shop_probe_rejects_shop_menu_before_daily_sales_opens()
    {
        var root = FindRoot();
        var frame = GrayImageCodec.FromFile(Path.Combine(
            root, "tests", "UmamusumeWpfGui.Tests", "Fixtures", "Shop", "shop-menu-daily-sales.png"));
        var template = GrayImageCodec.FromFile(Path.Combine(
            root, "resource", "hachimi", "pipelines", "templates", "shop", "daily_sales_header.png"));
        Assert.NotNull(frame);
        Assert.NotNull(template);

        var match = TemplateMatcher.Find(
            frame!, template!, [580, 60, 300, 110], 0.86, 900, 1600);

        Assert.False(match.Found, $"Shop menu false positive at score {match.Score:0.000}.");
    }

    [Theory]
    [InlineData("shop-menu-daily-sales.png", 1225)]
    [InlineData("shop-menu-daily-sales-scrolled.png", 1195)]
    public void Finds_daily_sales_button_at_its_current_position(string imageName, int expectedY)
    {
        var root = FindRoot();
        var frame = GrayImageCodec.FromFile(Path.Combine(
            root, "tests", "UmamusumeWpfGui.Tests", "Fixtures", "Shop", imageName));
        var template = GrayImageCodec.FromFile(Path.Combine(
            root, "resource", "hachimi", "pipelines", "templates", "shop", "daily_sales.png"));
        Assert.NotNull(frame);
        Assert.NotNull(template);

        var match = TemplateMatcher.Find(
            frame!, template!, [20, 580, 860, 710], 0.86, 900, 1600);

        Assert.True(match.Found,
            $"{imageName}: best score {match.Score:0.000} at ({match.CenterX},{match.CenterY}).");
        Assert.InRange(match.CenterX, 680, 740);
        Assert.InRange(match.CenterY, expectedY - 15, expectedY + 15);
    }

    [Theory]
    [InlineData("Star Piece", 0)]
    [InlineData("Sakura Bakushin O Star Piece", 0)]
    [InlineData("Agnes Tachyon Star Piece", 0)]
    [InlineData("El Condor Pasa Star Piece", 0)]
    [InlineData("Winning Ticket Star Piece", 0)]
    [InlineData("Alarm Clock", 1)]
    [InlineData("Pleasing Parfait", 2)]
    [InlineData("Dirt Racing Shoes", 3)]
    [InlineData("Sprint Racing Shoes", 3)]
    [InlineData("Support Points", 4)]
    [InlineData("Takamatsunomiya Kinen Winner's Sash", 5)]
    [InlineData("Oka Sho Winner's Sash", 5)]
    public void Classifies_live_shop_labels(string label, int category) =>
        Assert.Equal((ShopItemCategory)category, ShopItemSelector.Classify(label));

    [Theory]
    [InlineData("Cost 5,000")]
    [InlineData("1 left")]
    [InlineData("Select All")]
    public void Ignores_non_item_labels(string label)
    {
        Assert.Null(ShopItemSelector.Classify(label));
    }

    [Fact]
    public void Derives_checkbox_from_name_and_rejects_clipped_rows()
    {
        Assert.Equal((765, 1030), ShopItemSelector.ExpectedCheckbox(
            new ScreenTextRect(190, 975, 310, 26), 900, 1600));
        Assert.Null(ShopItemSelector.ExpectedCheckbox(
            new ScreenTextRect(190, 1100, 300, 26), 900, 1600));
        Assert.Equal((765, 1030), ShopItemSelector.ExpectedCheckbox(
            new ScreenTextRect(380, 1950, 620, 52), 1800, 3200));
    }

    [Fact]
    public void Finds_unchecked_checkbox_beside_live_winners_sash_row()
    {
        var root = FindRoot();
        var screenshot = GrayImageCodec.FromFile(Path.Combine(
            root, "tests", "UmamusumeWpfGui.Tests", "Fixtures", "Shop", "daily-sales-bottom.png"));
        var checkbox = GrayImageCodec.FromFile(Path.Combine(
            root, "resource", "hachimi", "pipelines", "templates", "shop", "item_checkbox.png"));
        Assert.NotNull(screenshot);
        Assert.NotNull(checkbox);

        var match = TemplateMatcher.FindScaled(
            screenshot!, checkbox!, [710, 975, 110, 110], 0.76,
            900, 1600, [0.78, 0.85, 0.92, 1.0]);

        Assert.True(match.Found, $"Best score was {match.Score:0.000} at ({match.CenterX},{match.CenterY}).");
        Assert.InRange(match.CenterX, 745, 785);
        Assert.InRange(match.CenterY, 1006, 1054);
    }

    [Fact]
    public void Finds_disabled_confirmation_after_reset()
    {
        var root = FindRoot();
        var screenshot = GrayImageCodec.FromFile(Path.Combine(
            root, "tests", "UmamusumeWpfGui.Tests", "Fixtures", "Shop", "daily-sales-bottom.png"));
        var disabled = GrayImageCodec.FromFile(Path.Combine(
            root, "resource", "hachimi", "pipelines", "templates", "shop", "confirm_disabled.png"));
        Assert.NotNull(screenshot);
        Assert.NotNull(disabled);

        var match = TemplateMatcher.Find(
            screenshot!, disabled!, [250, 1250, 430, 240], 0.80, 900, 1600);

        Assert.True(match.Found, $"Disabled confirmation score {match.Score:0.000}.");
    }

    [Theory]
    [InlineData("daily-sales-actual-top.png", "Sakura Bakushin O Star Piece", "Agnes Tachyon Star Piece")]
    [InlineData("daily-sales-star-follow-up.png", "El Condor Pasa Star Piece", "Winning Ticket Star Piece")]
    [InlineData("daily-sales-alarm-and-parfait.png", "Alarm Clock", "Pleasing Parfait")]
    [InlineData("daily-sales-middle.png", "Dirt Racing Shoes", "Sprint Racing Shoes")]
    [InlineData("daily-sales-lower.png", "Support Points", "Winner's Sash")]
    public async Task Windows_ocr_reads_live_shop_names_in_list_region(
        string imageName,
        string firstName,
        string secondName)
    {
        var root = FindRoot();
        var frame = GrayImageCodec.FromFile(Path.Combine(
            root, "tests", "UmamusumeWpfGui.Tests", "Fixtures", "Shop", imageName));
        Assert.NotNull(frame);
        var crop = GrayImageCodec.Crop(frame!, new Int32Rect(175, 585, 520, 565));
        Assert.NotNull(crop?.RgbaPixels);
        var result = await new WindowsOcrTextRecognizer().RecognizeAsync(
            new AdbRawScreenshot(crop!.Width, crop.Height, crop.RgbaPixels!), "en-US");
        var labels = result.Detections.Select(item => item.Text).ToArray();

        Assert.Contains(labels, label => label.Contains(firstName, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(labels, label => label.Contains(secondName, StringComparison.OrdinalIgnoreCase));

        var name = result.Detections.First(item =>
            item.Text.Contains(firstName, StringComparison.OrdinalIgnoreCase));
        var globalBounds = name.Bounds with
        {
            X = name.Bounds.X + 175,
            Y = name.Bounds.Y + 585,
        };
        var expected = ShopItemSelector.ExpectedCheckbox(globalBounds, 900, 1600);
        Assert.NotNull(expected);
        var checkbox = GrayImageCodec.FromFile(Path.Combine(
            root, "resource", "hachimi", "pipelines", "templates", "shop", "item_checkbox.png"));
        Assert.NotNull(checkbox);
        var match = TemplateMatcher.FindScaled(
            frame!, checkbox!,
            [expected.Value.X - 55, expected.Value.Y - 55, 110, 110],
            0.76, 900, 1600, [0.78, 0.85, 0.92, 1.0]);
        Assert.True(match.Found,
            $"{imageName}: OCR '{name.Text}' at {globalBounds}, checkbox expected {expected}, "
            + $"best match {match.Score:0.000} at ({match.CenterX},{match.CenterY}).");
    }

    [Theory]
    [InlineData("daily-sales-alarm-and-parfait.png", "Alarm Clock")]
    [InlineData("daily-sales-two-parfaits.png", "Pleasing Parfait")]
    [InlineData("daily-sales-two-supports.png", "Support Points")]
    public async Task Windows_ocr_keeps_repeated_items_as_distinct_rows(
        string imageName,
        string itemName)
    {
        var root = FindRoot();
        var frame = GrayImageCodec.FromFile(Path.Combine(
            root, "tests", "UmamusumeWpfGui.Tests", "Fixtures", "Shop", imageName));
        Assert.NotNull(frame);
        var crop = GrayImageCodec.Crop(frame!, new Int32Rect(175, 585, 520, 565));
        Assert.NotNull(crop?.RgbaPixels);
        var result = await new WindowsOcrTextRecognizer().RecognizeAsync(
            new AdbRawScreenshot(crop!.Width, crop.Height, crop.RgbaPixels!), "en-US");
        var rows = result.Detections
            .Where(item => item.Text.Contains(itemName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Bounds.Y)
            .ToArray();

        Assert.Equal(2, rows.Length);
        Assert.True(rows[1].Bounds.Y - rows[0].Bounds.Y > 120,
            $"{imageName}: repeated {itemName} detections overlap.");
    }

    [Fact]
    public async Task Selects_both_repeated_sash_rows_and_verifies_each_check()
    {
        var (selector, fake) = CreateSelector();
        var result = await selector.SelectAsync(
            CreateConnection(), new HachimiPipelineDefinition(),
            new ShopPurchaseOptions(false, false, false, false, false, false, true),
            null, CancellationToken.None);

        Assert.True(result.HasSelection);
        Assert.Equal(2, result.VerifiedRows);
        Assert.Equal(0, result.SkippedRows);
        Assert.Equal(2, fake.Taps.Count);
        Assert.InRange(fake.Taps[0].CenterY, 805, 865);
        Assert.InRange(fake.Taps[1].CenterY, 1006, 1054);
        Assert.Equal(1, fake.ResetTaps);
    }

    [Fact]
    public async Task No_matching_configured_item_skips_purchase()
    {
        var (selector, fake) = CreateSelector();
        var result = await selector.SelectAsync(
            CreateConnection(), new HachimiPipelineDefinition(),
            new ShopPurchaseOptions(false, false, true, false, false, false, false),
            null, CancellationToken.None);

        Assert.False(result.HasSelection);
        Assert.Empty(fake.Taps);
    }

    [Fact]
    public async Task Overlapping_page_does_not_toggle_previously_selected_rows()
    {
        var (selector, fake) = CreateSelector(simulateOverlap: true);
        var result = await selector.SelectAsync(
            CreateConnection(), new HachimiPipelineDefinition(),
            new ShopPurchaseOptions(false, false, false, false, false, false, true),
            null, CancellationToken.None);

        Assert.True(result.HasSelection);
        Assert.Equal(2, result.VerifiedRows);
        Assert.Equal(2, fake.Taps.Count);
    }

    private static (ShopItemSelector Selector, FakeShopVisualRuntime Fake) CreateSelector(
        bool simulateOverlap = false)
    {
        var root = FindRoot();
        var frame = GrayImageCodec.FromFile(Path.Combine(
            root, "tests", "UmamusumeWpfGui.Tests", "Fixtures", "Shop", "daily-sales-bottom.png"));
        var checkbox = GrayImageCodec.FromFile(Path.Combine(
            root, "resource", "hachimi", "pipelines", "templates", "shop", "item_checkbox.png"));
        var disabled = GrayImageCodec.FromFile(Path.Combine(
            root, "resource", "hachimi", "pipelines", "templates", "shop", "confirm_disabled.png"));
        Assert.NotNull(frame);
        Assert.NotNull(checkbox);
        Assert.NotNull(disabled);
        var runtime = FakeShopVisualRuntime.Create(
            frame!, checkbox!, disabled!, simulateOverlap, out var fake);
        return (new ShopItemSelector(runtime), fake);
    }

    private static LastVerifiedConnection CreateConnection() =>
        new("adb", "emulator", "android", "version", 900, 1600, 900, 1600,
            DateTimeOffset.UnixEpoch);

    public class FakeShopVisualRuntime : DispatchProxy
    {
        private GrayImage _frame = null!;
        private GrayImage _checkbox = null!;
        private GrayImage _disabled = null!;
        private GrayImage _overlapFrame = null!;
        private bool _simulateOverlap;
        private bool _scrolled;
        private readonly HashSet<int> _selectedY = [];

        public List<TemplateMatchResult> Taps { get; } = [];
        public int ResetTaps { get; private set; }

        public static IVisualPipelineRuntime Create(
            GrayImage frame,
            GrayImage checkbox,
            GrayImage disabled,
            bool simulateOverlap,
            out FakeShopVisualRuntime fake)
        {
            var runtime = DispatchProxy.Create<IVisualPipelineRuntime, FakeShopVisualRuntime>();
            fake = (FakeShopVisualRuntime)(object)runtime;
            fake._frame = frame;
            fake._checkbox = checkbox;
            fake._disabled = disabled;
            fake._simulateOverlap = simulateOverlap;
            var changed = (byte[])frame.Pixels.Clone();
            for (var y = 610; y <= 1100; y += 12)
                for (var x = 190; x <= 650; x += 12)
                    changed[y * frame.Width + x] ^= 0x7f;
            fake._overlapFrame = new GrayImage(frame.Width, frame.Height, changed,
                frame.RgbaPixels);
            return runtime;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "CaptureGrayAsync":
                    return Task.FromResult<GrayImage?>(
                        _scrolled && _simulateOverlap ? _overlapFrame : _frame);
                case "LoadTemplateAsync":
                    return Task.FromResult<GrayImage?>(
                        ((string)args![0]!).Contains("confirm_disabled", StringComparison.Ordinal)
                            ? _disabled : _checkbox);
                case "DetectTextAsync" when args is not null && args[0] is GrayImage:
                    return Task.FromResult<ScreenTextRecognitionResult?>(
                        new ScreenTextRecognitionResult(
                        [
                            new ScreenTextDetection("Takamatsunomiya Kinen Winner's Sash",
                                new ScreenTextRect(190, 775, 490, 30)),
                            new ScreenTextDetection("Oka Sho Winner's Sash",
                                new ScreenTextRect(190, 974, 360, 30)),
                        ], "en-US", 900, 1600));
                case "ProbeHsvAsync":
                    var y = (int)args![2]!;
                    var matched = _selectedY.Any(selected => Math.Abs(selected - y) <= 18);
                    return Task.FromResult<HsvColorProbeResult?>(
                        new HsvColorProbeResult(matched, matched ? 0.3 : 0,
                            matched ? 100 : 0, 400, (int)args[1]!, y, 22));
                case "TapMatchAsync":
                    var match = (TemplateMatchResult)args![1]!;
                    Taps.Add(match);
                    _selectedY.Add(match.CenterY);
                    return Task.CompletedTask;
                case "TapAsync":
                    ResetTaps++;
                    return Task.CompletedTask;
                case "SwipeAsync":
                    if (_simulateOverlap && args?[1] is int[] swipe && swipe[1] > swipe[3])
                        _scrolled = true;
                    return Task.CompletedTask;
                case "DelayAsync":
                    return Task.CompletedTask;
                default:
                    throw new InvalidOperationException($"Unexpected call: {targetMethod?.Name}");
            }
        }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "resource", "hachimi", "pipelines", "shop.json"))
                && File.Exists(Path.Combine(directory.FullName, "tests", "UmamusumeWpfGui.Tests",
                    "Fixtures", "Shop", "daily-sales-bottom.png")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Solution root was not found.");
    }
}
