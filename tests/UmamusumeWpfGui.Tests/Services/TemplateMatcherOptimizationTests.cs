using System.Diagnostics;
using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using Xunit.Abstractions;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class TemplateMatcherOptimizationTests
{
    private readonly ITestOutputHelper _output;

    public TemplateMatcherOptimizationTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(21, 13, 9001)]
    [InlineData(170, 72, 9002)]
    [InlineData(50, 40, 9003)]
    public void Find_preserves_exhaustive_match_result_and_roi_edge(
        int templateWidth,
        int templateHeight,
        int seed)
    {
        var screenWidth = templateWidth + 72;
        var screenHeight = templateHeight + 48;
        var screenPixels = CreateRandomPixels(screenWidth * screenHeight, seed);
        var templatePixels = CreateRandomPixels(templateWidth * templateHeight, seed + 1);
        var screen = new GrayImage(screenWidth, screenHeight, screenPixels);
        var template = new GrayImage(templateWidth, templateHeight, templatePixels);
        var roi = new[] { 7, 5, screenWidth - 14, screenHeight - 10 };
        var expectedX = roi[0] + roi[2] - templateWidth;
        var expectedY = roi[1] + roi[3] - templateHeight;
        CopyTemplate(screen, template, expectedX, expectedY);

        var expected = LegacyFind(screen, template, roi, 0.95);
        var actual = TemplateMatcher.Find(
            screen,
            template,
            roi,
            threshold: 0.95,
            referenceWidth: screenWidth,
            referenceHeight: screenHeight);

        Assert.Equal(expected.Found, actual.Found);
        Assert.Equal(expected.X, actual.X);
        Assert.Equal(expected.Y, actual.Y);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Score, actual.Score, precision: 10);
        Assert.True(actual.Found);
        Assert.Equal(1d, actual.Score, precision: 10);
    }

    [Fact]
    public void Find_preserves_flat_template_fallback_and_miss_classification()
    {
        var template = new GrayImage(8, 5, Enumerable.Repeat((byte)120, 40).ToArray());
        var screenPixels = Enumerable.Repeat((byte)35, 32 * 20).ToArray();
        var screen = new GrayImage(32, 20, screenPixels);
        CopyTemplate(screen, template, 17, 11);

        AssertMatchesLegacy(screen, template, roi: null, threshold: 1d);
        AssertMatchesLegacy(screen, template, roi: [0, 0, 12, 12], threshold: 0.9);
    }

    [Fact]
    public void Find_allocates_only_fixed_per_call_state_not_per_candidate()
    {
        const int screenWidth = 120;
        const int screenHeight = 90;
        var screen = new GrayImage(
            screenWidth,
            screenHeight,
            CreateRandomPixels(screenWidth * screenHeight, 12001));
        var template = new GrayImage(
            36,
            18,
            CreateRandomPixels(36 * 18, 12002));
        var roi = new[] { 5, 5, 108, 78 };

        _ = TemplateMatcher.Find(screen, template, roi, 0.95, screenWidth, screenHeight);
        _ = TemplateMatcher.Find(screen, template, roi, 0.95, screenWidth, screenHeight);
        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = TemplateMatcher.Find(screen, template, roi, 0.95, screenWidth, screenHeight);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated < 8_192,
            $"Matcher allocated {allocated:N0} bytes for one call; candidate scoring should not allocate.");
    }

    [Fact]
    public void Find_preserves_legacy_result_for_representative_button_roi()
    {
        const int screenWidth = 144;
        const int screenHeight = 112;
        const int templateWidth = 80;
        const int templateHeight = 52;
        var screen = new GrayImage(
            screenWidth,
            screenHeight,
            CreateRandomPixels(screenWidth * screenHeight, 13001));
        var template = new GrayImage(
            templateWidth,
            templateHeight,
            CreateRandomPixels(templateWidth * templateHeight, 13002));
        var roi = new[] { 0, 0, screenWidth, screenHeight };

        // Warm both code paths on a small image so JIT cost is excluded.
        var warmScreen = new GrayImage(24, 20, CreateRandomPixels(24 * 20, 13003));
        var warmTemplate = new GrayImage(9, 7, CreateRandomPixels(9 * 7, 13004));
        _ = TemplateMatcher.Find(warmScreen, warmTemplate, null, 0.95, 24, 20);
        _ = LegacyFind(warmScreen, warmTemplate, null, 0.95);

        var optimizedStart = Stopwatch.GetTimestamp();
        var optimized = TemplateMatcher.Find(
            screen,
            template,
            roi,
            threshold: 0.95,
            referenceWidth: screenWidth,
            referenceHeight: screenHeight);
        var optimizedElapsed = Stopwatch.GetElapsedTime(optimizedStart);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var legacyStart = Stopwatch.GetTimestamp();
        var legacy = LegacyFind(screen, template, roi, threshold: 0.95);
        var legacyElapsed = Stopwatch.GetElapsedTime(legacyStart);

        _output.WriteLine(
            $"Original scorer: {legacyElapsed.TotalMilliseconds:0.0}ms; "
            + $"optimized scorer: {optimizedElapsed.TotalMilliseconds:0.0}ms; "
            + $"speedup: {legacyElapsed.TotalMilliseconds / optimizedElapsed.TotalMilliseconds:0.0}x.");

        Assert.Equal(legacy.Found, optimized.Found);
        Assert.Equal(legacy.X, optimized.X);
        Assert.Equal(legacy.Y, optimized.Y);
        Assert.Equal(legacy.Score, optimized.Score, precision: 10);
    }

    [Fact]
    public void Career_home_button_matches_in_the_logged_roi_and_resolution()
    {
        const int screenWidth = 900;
        const int screenHeight = 1600;
        const int expectedX = 635;
        const int expectedY = 1290;
        var screen = new GrayImage(
            screenWidth,
            screenHeight,
            CreateRandomPixels(screenWidth * screenHeight, 14001));
        var template = new GrayImage(
            90,
            60,
            CreateRandomPixels(90 * 60, 14002));
        CopyTemplate(screen, template, expectedX, expectedY);

        var started = Stopwatch.GetTimestamp();
        var match = TemplateMatcher.Find(
            screen,
            template,
            roi: [505, 1240, 330, 180],
            threshold: 0.78,
            referenceWidth: screenWidth,
            referenceHeight: screenHeight);
        var elapsed = Stopwatch.GetElapsedTime(started);

        _output.WriteLine(
            $"Career entry button matched at ({match.CenterX},{match.CenterY}) "
            + $"in {elapsed.TotalMilliseconds:0.0}ms.");
        Assert.True(match.Found, $"Career entry button score was {match.Score:0.000}.");
        Assert.Equal(expectedX + template.Width / 2, match.CenterX);
        Assert.Equal(expectedY + template.Height / 2, match.CenterY);
    }

    [Fact]
    public void Find_preserves_startup_notice_fixture_score()
    {
        var root = FindSolutionRoot();
        var screen = GrayImageCodec.FromFile(Path.Combine(
            root, "testdata", "hachimi", "start_game", "cold_7.png"));
        var template = GrayImageCodec.FromFile(Path.Combine(
            root, "resource", "hachimi", "pipelines", "templates", "start_game", "startnotice_skip.png"));
        Assert.NotNull(screen);
        Assert.NotNull(template);
        var roi = new[] { 68, 444, 764, 369 };

        var legacy = LegacyFind(screen!, template!, roi, threshold: 0.70);
        var actual = TemplateMatcher.Find(screen!, template!, roi, 0.70, 900, 1600);
        _output.WriteLine(
            $"Start notice score: original={legacy.Score:0.000000}, optimized={actual.Score:0.000000}.");

        Assert.Equal(legacy.Found, actual.Found);
        Assert.Equal(legacy.X, actual.X);
        Assert.Equal(legacy.Y, actual.Y);
        Assert.Equal(legacy.Score, actual.Score, precision: 10);
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

    private static void AssertMatchesLegacy(
        GrayImage screen,
        GrayImage template,
        int[]? roi,
        double threshold)
    {
        var expected = LegacyFind(screen, template, roi, threshold);
        var actual = TemplateMatcher.Find(
            screen,
            template,
            roi,
            threshold,
            screen.Width,
            screen.Height);

        Assert.Equal(expected.Found, actual.Found);
        Assert.Equal(expected.X, actual.X);
        Assert.Equal(expected.Y, actual.Y);
        Assert.Equal(expected.Score, actual.Score, precision: 10);
    }

    // Test-only copy of the original exhaustive scorer. Keeping this reference
    // makes semantic drift visible while the production scorer is optimized.
    private static TemplateMatchResult LegacyFind(
        GrayImage screen,
        GrayImage template,
        int[]? roi,
        double threshold)
    {
        var boundsX = roi is { Length: >= 4 } ? roi[0] : 0;
        var boundsY = roi is { Length: >= 4 } ? roi[1] : 0;
        var boundsWidth = roi is { Length: >= 4 } ? roi[2] : screen.Width;
        var boundsHeight = roi is { Length: >= 4 } ? roi[3] : screen.Height;
        var maxX = Math.Min(screen.Width - template.Width, boundsX + boundsWidth - template.Width);
        var maxY = Math.Min(screen.Height - template.Height, boundsY + boundsHeight - template.Height);
        var sampleWidth = template.Width <= 160 ? template.Width : 32;
        var sampleHeight = template.Height <= 80 ? template.Height : 32;
        var candidateStep = template.Width <= 160 ? 1 : 2;
        var bestScore = double.MinValue;
        var bestX = boundsX;
        var bestY = boundsY;

        for (var y = boundsY; y <= maxY; y += candidateStep)
        {
            for (var x = boundsX; x <= maxX; x += candidateStep)
            {
                var score = LegacyCompare(screen, template, x, y, sampleWidth, sampleHeight);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestX = x;
                    bestY = y;
                }
            }
        }

        return new TemplateMatchResult(
            bestScore >= Math.Clamp(threshold, 0, 1),
            Math.Max(0, bestScore),
            bestX,
            bestY,
            template.Width,
            template.Height);
    }

    private static double LegacyCompare(
        GrayImage screen,
        GrayImage template,
        int screenX,
        int screenY,
        int sampleWidth,
        int sampleHeight)
    {
        var templateValues = new double[sampleWidth * sampleHeight];
        var screenValues = new double[templateValues.Length];
        var samples = 0;
        for (var sampleY = 0; sampleY < sampleHeight; sampleY++)
        {
            var templateY = sampleY * template.Height / sampleHeight;
            var screenRow = (screenY + templateY) * screen.Width;
            var templateRow = templateY * template.Width;
            for (var sampleX = 0; sampleX < sampleWidth; sampleX++)
            {
                var templateX = sampleX * template.Width / sampleWidth;
                templateValues[samples] = template.Pixels[templateRow + templateX];
                screenValues[samples] = screen.Pixels[screenRow + screenX + templateX];
                samples++;
            }
        }

        if (samples == 0)
            return 0;

        var templateMean = templateValues.Take(samples).Average();
        var screenMean = screenValues.Take(samples).Average();
        var numerator = 0d;
        var templateVariance = 0d;
        var screenVariance = 0d;
        for (var index = 0; index < samples; index++)
        {
            var templateDelta = templateValues[index] - templateMean;
            var screenDelta = screenValues[index] - screenMean;
            numerator += templateDelta * screenDelta;
            templateVariance += templateDelta * templateDelta;
            screenVariance += screenDelta * screenDelta;
        }

        if (templateVariance < 1 || screenVariance < 1)
        {
            var error = 0d;
            for (var index = 0; index < samples; index++)
                error += Math.Abs(screenValues[index] - templateValues[index]);
            return 1d - error / (samples * 255d);
        }

        return Math.Clamp(
            numerator / Math.Sqrt(templateVariance * screenVariance),
            -1d,
            1d);
    }

    private static byte[] CreateRandomPixels(int length, int seed)
    {
        var random = new Random(seed);
        var pixels = new byte[length];
        random.NextBytes(pixels);
        return pixels;
    }

    private static void CopyTemplate(
        GrayImage screen,
        GrayImage template,
        int destinationX,
        int destinationY)
    {
        for (var y = 0; y < template.Height; y++)
        {
            Array.Copy(
                template.Pixels,
                y * template.Width,
                screen.Pixels,
                (destinationY + y) * screen.Width + destinationX,
                template.Width);
        }
    }
}
