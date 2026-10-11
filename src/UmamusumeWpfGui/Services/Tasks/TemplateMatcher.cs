using System.IO;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Globalization;
using UmamusumeWpfGui.Models;
using IUmaNativeRecognitionService = Umamusume.CoreBridge.IUmaService;
using RecognitionImage = Umamusume.CoreBridge.RecognitionImage;
using TemplateMatchMethod = Umamusume.CoreBridge.TemplateMatchMethod;

namespace UmamusumeWpfGui.Services.Tasks;

internal static class TemplateMatcher
{
    private static Action<TemplateMatcherTrace>? _traceObserver;
    private static IUmaNativeRecognitionService? _nativeService;

    internal static void ConfigureNativeRecognition(IUmaNativeRecognitionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (service.CoreVersion is null)
            throw new InvalidOperationException("The native recognition service must be initialized first.");
        Volatile.Write(ref _nativeService, service);
    }

    internal static RecognitionImage? LoadTemplateImage(string path,
        Umamusume.CoreBridge.TemplatePreparation preparation) => GetNativeService().LoadTemplateImage(path, preparation);

    internal static double ScoreMaskedRegion(GrayImage screen, GrayImage template, int[] region) =>
        NativeFind(GetNativeService(), screen, template, TemplateMatchMethod.MaskedRegion,
            region, 0, screen.Width, screen.Height).Score;

    internal static bool HasMatchingMarkerColors(GrayImage screen, GrayImage template,
        TemplateMatchResult match, double threshold) =>
        NativeFind(GetNativeService(), screen, template, TemplateMatchMethod.ColoredRegion,
            [match.X, match.Y, match.Width, match.Height], threshold, screen.Width, screen.Height).Found;

    internal static TemplateMatchResult FindExhaustiveColor(GrayImage screen, GrayImage template,
        int[]? roi, double threshold, int referenceWidth, int referenceHeight) =>
        NativeFind(GetNativeService(), screen, template, TemplateMatchMethod.ExhaustiveColor,
            roi, threshold, referenceWidth, referenceHeight);

    internal static double FindScreenshotCrop(GrayImage screen, GrayImage template, int[] region,
        int width, int height) =>
        NativeFind(GetNativeService(), screen, template, TemplateMatchMethod.ScreenshotCrop,
            region, 0, width, height).Score;

    internal static Action<TemplateMatcherTrace>? TraceObserver
    {
        get => Volatile.Read(ref _traceObserver);
        set => Volatile.Write(ref _traceObserver, value);
    }

    public static TemplateMatchResult FindScaled(
        GrayImage screen,
        GrayImage template,
        int[]? roi,
        double threshold,
        int referenceWidth,
        int referenceHeight,
        IReadOnlyList<double> scaleCandidates,
        int candidateStep = 8,
        int sampleWidth = 16,
        int sampleHeight = 16,
        bool fullSearchOnMiss = true)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(scaleCandidates);
        var result = NativeFind(GetNativeService(), screen, template, TemplateMatchMethod.Scaled,
            roi, threshold, referenceWidth, referenceHeight,
            candidateStep: candidateStep, sampleWidth: sampleWidth,
            sampleHeight: sampleHeight, fullSearchOnMiss: fullSearchOnMiss,
            scales: scaleCandidates);
        if (TraceObserver is null)
            return result;
        return TraceResult(
            "FindScaled",
            screen,
            template,
            roi,
            threshold,
            referenceWidth,
            referenceHeight,
            $"scales={string.Join(',', scaleCandidates.Select(static scale => scale.ToString("R", CultureInfo.InvariantCulture)))};candidateStep={candidateStep};sample={sampleWidth}x{sampleHeight};fullSearchOnMiss={fullSearchOnMiss}",
            result);
    }

    public static TemplateMatchResult FindScaledMasked(
        GrayImage screen,
        string transparentTemplatePath,
        int[]? roi,
        double threshold,
        int referenceWidth,
        int referenceHeight,
        IReadOnlyList<double> scaleCandidates)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentException.ThrowIfNullOrWhiteSpace(transparentTemplatePath);
        ArgumentNullException.ThrowIfNull(scaleCandidates);

        var prepared = GrayImageCodec.LoadTemplate(transparentTemplatePath,
            Umamusume.CoreBridge.TemplatePreparation.MaskedHead);
        var tracedTemplate = prepared is null ? null : ToMaskedGrayImage(prepared);
        var result = tracedTemplate is null
            ? new TemplateMatchResult(false, 0, 0, 0, 0, 0)
            : NativeFind(GetNativeService(), screen, tracedTemplate,
                TemplateMatchMethod.ScaledMasked, roi, threshold, referenceWidth,
                referenceHeight, scales: scaleCandidates);
        if (TraceObserver is null) return result;
        return TraceResult(
            "FindScaledMasked",
            screen,
            tracedTemplate,
            roi,
            threshold,
            referenceWidth,
            referenceHeight,
            $"scales={string.Join(',', scaleCandidates.Select(static scale => scale.ToString("R", CultureInfo.InvariantCulture)))}",
            result,
            transparentTemplatePath);
    }

    public static TemplateMatchResult Find(
        GrayImage screen,
        GrayImage template,
        int[]? roi,
        double threshold,
        int referenceWidth,
        int referenceHeight,
        int? candidateStepOverride = null)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(template);
        var result = NativeFind(GetNativeService(), screen, template, TemplateMatchMethod.Gray,
            roi, threshold, referenceWidth, referenceHeight,
            candidateStepOverride: candidateStepOverride ?? 0);
        if (TraceObserver is null)
            return result;
        return TraceResult(
            "Find",
            screen,
            template,
            roi,
            threshold,
            referenceWidth,
            referenceHeight,
            $"candidateStepOverride={candidateStepOverride?.ToString(CultureInfo.InvariantCulture) ?? "default"}",
            result);
    }

    /// <summary>
    /// Color-aware template matching for state buttons whose geometry is the
    /// same across states.  Grayscale correlation is deliberately unsuitable
    /// for the quick-mode Skip control: the green selected button and the
    /// white "Skip Off" button share the same border/text layout.
    /// Transparent template pixels are ignored so the crop can retain its
    /// authored padding.
    /// </summary>
    public static TemplateMatchResult FindColor(
        GrayImage screen,
        GrayImage template,
        int[]? roi,
        double threshold,
        int referenceWidth,
        int referenceHeight,
        bool requireTextContrast = false)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(template);
        var result = NativeFind(GetNativeService(), screen, template, TemplateMatchMethod.Color,
            roi, threshold, referenceWidth, referenceHeight,
            requireTextContrast: requireTextContrast);
        if (TraceObserver is null)
            return result;
        return TraceResult(
            "FindColor",
            screen,
            template,
            roi,
            threshold,
            referenceWidth,
            referenceHeight,
            $"requireTextContrast={requireTextContrast}",
            result);
    }

    /// <summary>
    /// Matches a small button using the button's stroke/text edges instead of
    /// allowing a large flat crop background to dominate the correlation.
    ///
    /// Reset is a particularly important caller: its reference crop includes
    /// a grey page background around the rounded button.  Pearson correlation
    /// over that crop can therefore return a stable high score for unrelated
    /// page content.  The edge mask below is authored from the template and
    /// only scores locations where the template actually has a visible
    /// stroke or glyph.
    /// </summary>
    public static TemplateMatchResult FindButton(
        GrayImage screen,
        GrayImage template,
        int[]? roi,
        double threshold,
        int referenceWidth,
        int referenceHeight)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(template);
        var result = NativeFind(GetNativeService(), screen, template, TemplateMatchMethod.Button,
            roi, threshold, referenceWidth, referenceHeight);
        if (TraceObserver is null)
            return result;
        return TraceResult(
            "FindButton",
            screen,
            template,
            roi,
            threshold,
            referenceWidth,
            referenceHeight,
            "",
            result);
    }

    /// <summary>
    /// Returns whether a click anchored at <paramref name="match">match</paramref>
    /// produced a meaningful visual change in the surrounding skill row.
    /// This is a safety stop for a stale/false button match: a successful ADB
    /// tap alone does not prove that the Reset control actually responded.
    /// </summary>
    public static bool HasMeaningfulChange(
        GrayImage before,
        GrayImage after,
        TemplateMatchResult match)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        return GetNativeService().HasMeaningfulChange(
            ToRecognitionImage(before), ToRecognitionImage(after), ToBridgeResult(match));
    }

    /// <summary>
    /// Requires a change to remain visible in two settled frames. This
    /// prevents the short pressed-button animation from being mistaken for a
    /// successful Reset and then causing another tap on the same stale match.
    /// </summary>
    public static bool HasPersistentChange(
        GrayImage before,
        GrayImage firstAfter,
        GrayImage settledAfter,
        TemplateMatchResult match)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(firstAfter);
        ArgumentNullException.ThrowIfNull(settledAfter);

        return GetNativeService().HasPersistentChange(
            ToRecognitionImage(before), ToRecognitionImage(firstAfter),
            ToRecognitionImage(settledAfter), ToBridgeResult(match));
    }

    private static IUmaNativeRecognitionService GetNativeService()
    {
        return Volatile.Read(ref _nativeService)
            ?? throw new InvalidOperationException(
                "The native recognition service has not been initialized.");
    }

    private static TemplateMatchResult NativeFind(
        IUmaNativeRecognitionService service,
        GrayImage screen,
        GrayImage template,
        TemplateMatchMethod method,
        int[]? roi,
        double threshold,
        int referenceWidth,
        int referenceHeight,
        int candidateStepOverride = 0,
        int candidateStep = 8,
        int sampleWidth = 16,
        int sampleHeight = 16,
        bool fullSearchOnMiss = true,
        bool requireTextContrast = false,
        IReadOnlyList<double>? scales = null)
    {
        var options = new Umamusume.CoreBridge.TemplateMatchOptions(
            method,
            roi is { Length: >= 4 } ? [roi[0], roi[1], roi[2], roi[3]] : null,
            threshold,
            referenceWidth,
            referenceHeight,
            candidateStepOverride,
            candidateStep,
            sampleWidth,
            sampleHeight,
            fullSearchOnMiss,
            requireTextContrast,
            scales);
        var match = service.MatchTemplate(
            ToRecognitionImage(screen), ToRecognitionImage(template), options);
        return new TemplateMatchResult(
            match.Found, match.Score, match.X, match.Y, match.Width, match.Height)
        {
            SourceFrame = screen.SourceFrame,
        };
    }

    private static RecognitionImage ToRecognitionImage(GrayImage image) =>
        new(image.Width, image.Height,
            image.Pixels.Length == 0 ? null : image.Pixels,
            image.RgbaPixels);

    private static GrayImage ToMaskedGrayImage(GrayImage template)
    {
        var rgba = new byte[checked(template.Pixels.Length * 4)];
        for (int index = 0; index < template.Pixels.Length; index++)
        {
            int offset = index * 4;
            byte gray = template.Pixels[index];
            rgba[offset] = gray;
            rgba[offset + 1] = gray;
            rgba[offset + 2] = gray;
            rgba[offset + 3] = template.RgbaPixels![offset + 3];
        }
        return new GrayImage(template.Width, template.Height, template.Pixels, rgba);
    }

    private static Umamusume.CoreBridge.TemplateMatchResult ToBridgeResult(
        TemplateMatchResult match) =>
        new(match.Found, match.Score, match.X, match.Y, match.Width, match.Height);

private static TemplateMatchResult TraceResult(
        string method,
        GrayImage screen,
        GrayImage? template,
        int[]? roi,
        double threshold,
        int referenceWidth,
        int referenceHeight,
        string parameters,
        TemplateMatchResult result,
        string? templatePath = null)
    {
        var observer = TraceObserver;
        if (observer is null)
            return result;

        observer(new TemplateMatcherTrace(
            method,
            HashImage(screen),
            template is null ? HashFile(templatePath) : HashImage(template),
            screen.Width,
            screen.Height,
            template?.Width ?? 0,
            template?.Height ?? 0,
            roi is null ? null : (int[])roi.Clone(),
            threshold,
            referenceWidth,
            referenceHeight,
            parameters,
            result.Found,
            result.Score,
            result.X,
            result.Y,
            result.Width,
            result.Height,
            screen,
            template,
            templatePath));
        return result;
    }

    private static string HashImage(GrayImage image)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> dimensions = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(dimensions, image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(dimensions[4..], image.Height);
        hash.AppendData(dimensions);
        AppendBytes(hash, image.Pixels);
        AppendBytes(hash, image.RgbaPixels);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendBytes(IncrementalHash hash, byte[]? pixels)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, pixels?.Length ?? -1);
        hash.AppendData(length);
        if (pixels is not null)
            hash.AppendData(pixels);
    }

    private static string? HashFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

internal sealed record TemplateMatcherTrace(
    string Method,
    string ScreenSha256,
    string? TemplateSha256,
    int ScreenWidth,
    int ScreenHeight,
    int TemplateWidth,
    int TemplateHeight,
    int[]? Roi,
    double Threshold,
    int ReferenceWidth,
    int ReferenceHeight,
    string Parameters,
    bool Found,
    double Score,
    int X,
    int Y,
    int Width,
    int Height,
    GrayImage ScreenInput,
    GrayImage? TemplateInput,
    string? TemplatePath);
