using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Umamusume.CoreBridge;

internal sealed unsafe partial class UmaCoreBridgeNative : IUmaNativeApi
{
    private const string LibraryName = "UmamusumeCore";

    static UmaCoreBridgeNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(UmaCoreBridgeNative).Assembly, ResolveNativeLibrary);
    }

    private static IntPtr ResolveNativeLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName is "UmamusumeCore" or "UmamusumeCore.dll" or "UmamusumeCore.dylib" or "libUmamusumeCore.dylib")
        {
            string[] candidateFiles = OperatingSystem.IsWindows()
                ? ["UmamusumeCore.dll"]
                : OperatingSystem.IsMacOS()
                    ? ["UmamusumeCore.dylib", "libUmamusumeCore.dylib"]
                    : ["libUmamusumeCore.so", "UmamusumeCore.so"];

            string? envPath = Environment.GetEnvironmentVariable("UMA_CORE_LIB_PATH");
            if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            {
                if (NativeLibrary.TryLoad(envPath, out IntPtr hEnv)) return hEnv;
            }

            string baseDir = AppContext.BaseDirectory;
            foreach (var candidate in candidateFiles)
            {
                string probe = Path.Combine(baseDir, candidate);
                if (File.Exists(probe) && NativeLibrary.TryLoad(probe, out IntPtr hProbe))
                    return hProbe;

                string frameworkProbe = Path.Combine(baseDir, "..", "Frameworks", candidate);
                if (File.Exists(frameworkProbe) && NativeLibrary.TryLoad(frameworkProbe, out IntPtr hFw))
                    return hFw;
            }

            for (DirectoryInfo? dir = new DirectoryInfo(baseDir); dir != null; dir = dir.Parent)
            {
                foreach (var candidate in candidateFiles)
                {
                    string probe = Path.Combine(dir.FullName, candidate);
                    if (File.Exists(probe) && NativeLibrary.TryLoad(probe, out IntPtr hProbe))
                        return hProbe;
                    string sub = Path.Combine(dir.FullName, "src", "UmaAssistantCore", candidate);
                    if (File.Exists(sub) && NativeLibrary.TryLoad(sub, out IntPtr hSub))
                        return hSub;
                    string cmake1 = Path.Combine(dir.FullName, "build", "test-cmake", "src", "UmaAssistantCore", candidate);
                    if (File.Exists(cmake1) && NativeLibrary.TryLoad(cmake1, out IntPtr hCmake1))
                        return hCmake1;
                    string cmake2 = Path.Combine(dir.FullName, "build", "osx-arm64-debug", "src", "UmaAssistantCore", candidate);
                    if (File.Exists(cmake2) && NativeLibrary.TryLoad(cmake2, out IntPtr hCmake2))
                        return hCmake2;
                    string cmake3 = Path.Combine(dir.FullName, "build", "osx-arm64", "src", "UmaAssistantCore", candidate);
                    if (File.Exists(cmake3) && NativeLibrary.TryLoad(cmake3, out IntPtr hCmake3))
                        return hCmake3;
                    string winStaging = Path.Combine(dir.FullName, "build", "native-staging", candidate);
                    if (File.Exists(winStaging) && NativeLibrary.TryLoad(winStaging, out IntPtr hWin))
                        return hWin;
                }
            }

            foreach (var candidate in candidateFiles)
            {
                if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out IntPtr handle))
                    return handle;
            }
        }
        return IntPtr.Zero;
    }

    internal static RecognitionImage? PrepareNumericGlyphs(RecognitionImage source, bool warmGain,
        RecognitionColorRule rule)
    {
        ValidateImage(source);
        using var pinned = new PinnedImage(source);
        var view = pinned.View; var nativeRule = ToColorRule(rule); IntPtr pointer = IntPtr.Zero;
        int result = PrepareNumericGlyphsNative(&view, warmGain ? 1 : 0, &nativeRule, &pointer);
        using var image = pointer == IntPtr.Zero ? null : new SafeDecodedImageHandle(pointer);
        RequireRecognitionSuccess(result, "UmaPrepareNumericGlyphs");
        if (image is null) return null;
        return RecognitionImageDecoder.CopyToManaged(result, image)
            ?? throw new ManagedBridgeException("Native glyph image is malformed.");
    }

    internal static RecognitionImage ComposeGray(RecognitionImage[] sources, RecognitionImageRect[] placements,
        int width, int height)
    {
        ArgumentNullException.ThrowIfNull(sources); ArgumentNullException.ThrowIfNull(placements);
        if (sources.Length != placements.Length || sources.Length > 2048)
            throw new ArgumentException("Image and placement counts must agree and fit the native limit.");
        var pinned = new List<PinnedImage>(sources.Length);
        try
        {
            var views = new UmaImageViewNative[sources.Length];
            var rectangles = new UmaImageRectNative[sources.Length];
            for (int i = 0; i < sources.Length; i++)
            {
                ValidateImage(sources[i]); var owner = new PinnedImage(sources[i]); pinned.Add(owner);
                views[i] = owner.View; rectangles[i] = ToImageRect(placements[i]);
            }
            IntPtr pointer = IntPtr.Zero; int result;
            fixed (UmaImageViewNative* images = views)
            fixed (UmaImageRectNative* regions = rectangles)
                result = ComposeGrayImagesNative(images, regions, (ulong)views.Length, width, height, &pointer);
            using var image = pointer == IntPtr.Zero ? null : new SafeDecodedImageHandle(pointer);
            RequireRecognitionSuccess(result, "UmaComposeGrayImages");
            return RecognitionImageDecoder.CopyToManaged(result, image)
                ?? throw new ManagedBridgeException("Native composed image is malformed.");
        }
        finally { foreach (var owner in pinned) owner.Dispose(); }
    }

    internal static int CountInkGroups(RecognitionImage source, int threshold)
    {
        ValidateImage(source); using var pinned = new PinnedImage(source); var view = pinned.View; int groups = 0;
        RequireRecognitionSuccess(CountInkGroupsNative(&view, threshold, &groups), "UmaCountInkGroups");
        return groups;
    }

    internal static RecognitionImageDifference SampleDifference(RecognitionImage before, RecognitionImage after,
        RecognitionImagePoint[] points)
    {
        ArgumentNullException.ThrowIfNull(points); ValidateImage(before); ValidateImage(after);
        using var a = new PinnedImage(before); using var b = new PinnedImage(after); var av = a.View; var bv = b.View;
        var locations = points.Select(point => new UmaImagePointNative { X = point.X, Y = point.Y }).ToArray();
        ulong sum = 0;
        fixed (UmaImagePointNative* coordinates = locations)
            RequireRecognitionSuccess(SampleImageDifferenceNative(&av, &bv, coordinates, (ulong)points.Length, &sum),
                "UmaSampleImageDifference");
        return new(sum, (ulong)points.Length);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UmaImagePointNative { internal int X, Y; }

    [LibraryImport(LibraryName, EntryPoint = "UmaPrepareNumericGlyphs")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int PrepareNumericGlyphsNative(UmaImageViewNative* source, int warmGain,
        UmaColorRuleNative* rule, IntPtr* image);
    [LibraryImport(LibraryName, EntryPoint = "UmaComposeGrayImages")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int ComposeGrayImagesNative(UmaImageViewNative* sources, UmaImageRectNative* placements,
        ulong count, int width, int height, IntPtr* image);
    [LibraryImport(LibraryName, EntryPoint = "UmaCountInkGroups")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int CountInkGroupsNative(UmaImageViewNative* source, int threshold, int* groups);
    [LibraryImport(LibraryName, EntryPoint = "UmaSampleImageDifference")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int SampleImageDifferenceNative(UmaImageViewNative* before, UmaImageViewNative* after,
        UmaImagePointNative* points, ulong count, ulong* difference);

    public int RefreshResourceSnapshot(SafeUmaHandle handle) => RefreshResourceSnapshotNative(handle);

    public RecognitionImage? LoadTemplateImage(SafeUmaHandle handle, string path, TemplatePreparation preparation)
    {
        using var utf8 = new UTF8Pinned(path);
        IntPtr nativeImage = IntPtr.Zero;
        int result = LoadTemplateImageNative(handle, utf8.Pointer, (int)preparation, &nativeImage);
        SafeDecodedImageHandle? image = nativeImage == IntPtr.Zero ? null : new SafeDecodedImageHandle(nativeImage);
        if (result != (int)ConnectionErrorCode.Success && result != (int)ConnectionErrorCode.RecognitionFailed)
        {
            image?.Dispose();
            RequireRecognitionSuccess(result, "UmaLoadTemplateImage");
        }
        return RecognitionImageDecoder.CopyToManaged(result, image);
    }

    internal static int BinarizeColor(RecognitionImage source, RecognitionColorRule rule,
        out SafeDecodedImageHandle? image)
    {
        ValidateImage(source);
        using var pinned = new PinnedImage(source);
        var view = pinned.View;
        var nativeRule = ToColorRule(rule);
        IntPtr nativeImage = IntPtr.Zero;
        int result = BinarizeColorNative(&view, &nativeRule, &nativeImage);
        image = nativeImage == IntPtr.Zero ? null : new SafeDecodedImageHandle(nativeImage);
        return result;
    }

    internal static IReadOnlyList<RecognitionColorBlob> FindColorBlobs(RecognitionImage source,
        RecognitionColorRule rule, RecognitionImageRect region)
    {
        ValidateImage(source);
        using var pinned = new PinnedImage(source);
        var view = pinned.View;
        var nativeRule = ToColorRule(rule);
        var nativeRegion = ToImageRect(region);
        ulong count = 0;
        RequireRecognitionSuccess(FindColorBlobsNative(&view, &nativeRule, &nativeRegion, null, 0, &count),
            "UmaFindColorBlobs");
        if (count == 0) return [];
        if (count > (ulong)source.Width * (ulong)source.Height || count > int.MaxValue)
            throw new ManagedBridgeException("Native color blob count exceeds the image size.");
        var blobs = new UmaColorBlobNative[(int)count];
        fixed (UmaColorBlobNative* destination = blobs)
            RequireRecognitionSuccess(FindColorBlobsNative(&view, &nativeRule, &nativeRegion,
                destination, count, &count), "UmaFindColorBlobs");
        return blobs.Select(blob => new RecognitionColorBlob(blob.Area,
            new RecognitionImageRect(blob.X, blob.Y, blob.Width, blob.Height))).ToArray();
    }

    internal static RecognitionColorCount CountColor(RecognitionImage source, RecognitionColorRule rule,
        RecognitionImageRect region, RecognitionImageRect? exclude)
    {
        ValidateImage(source);
        using var pinned = new PinnedImage(source);
        var view = pinned.View;
        var nativeRule = ToColorRule(rule);
        var nativeRegion = ToImageRect(region);
        var nativeExclude = exclude is { } rectangle ? ToImageRect(rectangle) : default;
        ulong matching = 0, sampled = 0;
        RequireRecognitionSuccess(CountColorNative(&view, &nativeRule, &nativeRegion,
            exclude is null ? null : &nativeExclude, &matching, &sampled), "UmaCountColor");
        return new RecognitionColorCount(matching, sampled);
    }

    private static UmaColorRuleNative ToColorRule(RecognitionColorRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new UmaColorRuleNative
        {
            MinimumRed = rule.MinimumRed, MinimumGreen = rule.MinimumGreen, MinimumBlue = rule.MinimumBlue,
            MaximumRed = rule.MaximumRed, MaximumGreen = rule.MaximumGreen, MaximumBlue = rule.MaximumBlue,
            MinimumRedGreen = rule.MinimumRedGreen, MaximumRedGreen = rule.MaximumRedGreen,
            MinimumGreenBlue = rule.MinimumGreenBlue, MaximumGreenBlue = rule.MaximumGreenBlue,
            UseHsv = rule.UseHsv ? 1 : 0, SampleStep = rule.SampleStep,
            HueMin = rule.HueMin, HueMax = rule.HueMax,
            SaturationMin = rule.SaturationMin, SaturationMax = rule.SaturationMax,
            ValueMin = rule.ValueMin, ValueMax = rule.ValueMax,
        };
    }
    private static UmaImageRectNative ToImageRect(RecognitionImageRect rectangle) => new()
        { X = rectangle.X, Y = rectangle.Y, Width = rectangle.Width, Height = rectangle.Height };

    [StructLayout(LayoutKind.Sequential)]
    private struct UmaColorRuleNative
    {
        internal int MinimumRed, MinimumGreen, MinimumBlue, MaximumRed, MaximumGreen, MaximumBlue;
        internal int MinimumRedGreen, MaximumRedGreen, MinimumGreenBlue, MaximumGreenBlue;
        internal int UseHsv, SampleStep;
        internal double HueMin, HueMax, SaturationMin, SaturationMax, ValueMin, ValueMax;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct UmaImageRectNative { internal int X, Y, Width, Height; }
    [StructLayout(LayoutKind.Sequential)]
    private struct UmaColorBlobNative { internal int Area, X, Y, Width, Height; }

    internal static int TransformImage(RecognitionImage source, RecognitionImageTransform options,
        out SafeDecodedImageHandle? image)
    {
        ValidateImage(source);
        using var pinned = new PinnedImage(source);
        UmaImageViewNative view = pinned.View;
        var transform = new UmaImageTransformOptionsNative
        {
            CropX = options.CropX, CropY = options.CropY,
            CropWidth = options.CropWidth, CropHeight = options.CropHeight,
            TargetWidth = options.TargetWidth, TargetHeight = options.TargetHeight,
            MeanGray = options.MeanGray ? 1 : 0,
              Nearest = options.Nearest ? 1 : 0,
        };
        IntPtr nativeImage = IntPtr.Zero;
        int result = TransformImageNative(&view, &transform, &nativeImage);
        image = nativeImage == IntPtr.Zero ? null : new SafeDecodedImageHandle(nativeImage);
        return result;
    }

    internal static int DecodeImageFile(string path, out SafeDecodedImageHandle? image)
    {
        using var utf8Path = new UTF8Pinned(path);
        IntPtr nativeImage = IntPtr.Zero;
        int result = DecodeImageFileNative(utf8Path.Pointer, &nativeImage);
        image = nativeImage == IntPtr.Zero ? null : new SafeDecodedImageHandle(nativeImage);
        return result;
    }

    internal static int DecodeImageBytes(
        ReadOnlySpan<byte> encodedBytes,
        out SafeDecodedImageHandle? image)
    {
        IntPtr nativeImage = IntPtr.Zero;
        int result;
        fixed (byte* bytes = encodedBytes)
            result = DecodeImageBytesNative(bytes, (ulong)encodedBytes.Length, &nativeImage);
        image = nativeImage == IntPtr.Zero ? null : new SafeDecodedImageHandle(nativeImage);
        return result;
    }

    internal static int CreateImageFromRgba(
        int width,
        int height,
        ReadOnlySpan<byte> rgba,
        out SafeDecodedImageHandle? image)
    {
        IntPtr nativeImage = IntPtr.Zero;
        int result;
        fixed (byte* bytes = rgba)
            result = CreateImageFromRgbaNative(width, height, bytes, (ulong)rgba.Length, &nativeImage);
        image = nativeImage == IntPtr.Zero ? null : new SafeDecodedImageHandle(nativeImage);
        return result;
    }

    internal static int GetDecodedImageInfo(
        SafeDecodedImageHandle image,
        out UmaDecodedImageInfoNative info)
    {
        UmaDecodedImageInfoNative nativeInfo = default;
        int result = GetDecodedImageInfoNative(image, &nativeInfo);
        info = nativeInfo;
        return result;
    }

    internal static int CopyDecodedImage(
        SafeDecodedImageHandle image,
        byte[] gray,
        byte[] rgba)
    {
        fixed (byte* grayPointer = gray)
        fixed (byte* rgbaPointer = rgba)
            return CopyDecodedImageNative(image, grayPointer, (ulong)gray.Length,
                rgbaPointer, (ulong)rgba.Length);
    }

    public uint GetApiVersion() => GetApiVersionNative();

    public string GetVersion()
    {
        string? version = Marshal.PtrToStringUTF8(GetVersionNative());
        return string.IsNullOrWhiteSpace(version)
            ? throw new InvalidOperationException("The native core returned an empty version.")
            : version;
    }

    public SafeUmaHandle Create(UmaApiCallback callback, IntPtr customArg)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return new SafeUmaHandle(CreateNative(callback, customArg), DestroyNative);
    }

    public int SetUserDir(string path)
    {
        using var utf8Path = new UTF8Pinned(path);
        return SetUserDirNative(utf8Path.Pointer);
    }

    public int LoadResource(string path)
    {
        using var utf8Path = new UTF8Pinned(path);
        return LoadResourceNative(utf8Path.Pointer);
    }

    public int SetInstanceOptions(SafeUmaHandle handle, string utf8Json)
    {
        using var json = new UTF8Pinned(utf8Json);
        return SetInstanceOptionsNative(handle, json.Pointer);
    }

    public UmaStartResult Connect(SafeUmaHandle handle, string adbPath, string serial, string profile)
    {
        using var utf8AdbPath = new UTF8Pinned(adbPath);
        using var utf8Serial = new UTF8Pinned(serial);
        using var utf8Profile = new UTF8Pinned(profile);
        return ConnectNative(handle, utf8AdbPath.Pointer, utf8Serial.Pointer, utf8Profile.Pointer);
    }

    public int CancelConnect(SafeUmaHandle handle, ulong operationId) =>
        CancelConnectNative(handle, operationId);

    public int CancelOperation(SafeUmaHandle handle, ulong operationId) =>
        CancelOperationNative(handle, operationId);

    public UmaStartResult VerifyGame(SafeUmaHandle handle, string packageId)
    {
        using var utf8PackageId = new UTF8Pinned(packageId);
        return VerifyGameNative(handle, utf8PackageId.Pointer);
    }

    public UmaStartResult Capture(SafeUmaHandle handle) => CaptureNative(handle);

    public int GetFramePngSize(SafeUmaHandle handle, ulong frameId, out ulong size)
    {
        ulong nativeSize = 0;
        int result = GetFramePngSizeNative(handle, frameId, &nativeSize);
        size = nativeSize;
        return result;
    }

    public int CopyFramePng(SafeUmaHandle handle, ulong frameId, Span<byte> destination)
    {
        fixed (byte* destinationPointer = destination)
        {
            return CopyFramePngNative(handle, frameId, destinationPointer, (ulong)destination.Length);
        }
    }

    public int ReleaseFrame(SafeUmaHandle handle, ulong frameId) =>
        ReleaseFrameNative(handle, frameId);

    public UmaStartResult Tap(SafeUmaHandle handle, ulong frameId, int x, int y) =>
        TapNative(handle, frameId, x, y);

    public UmaStartResult Swipe(
        SafeUmaHandle handle,
        ulong frameId,
        int x1,
        int y1,
        int x2,
        int y2,
        int durationMs,
        bool extraSwipe) =>
        extraSwipe
            ? SwipeWithOptionsNative(handle, frameId, x1, y1, x2, y2, durationMs, 1)
            : SwipeNative(handle, frameId, x1, y1, x2, y2, durationMs);

    public UmaStartResult Touch(SafeUmaHandle handle, ulong frameId, int x, int y, int phase) =>
        TouchNative(handle, frameId, x, y, phase);

    public UmaStartResult PrepareTouch(SafeUmaHandle handle, ulong frameId) =>
        PrepareTouchNative(handle, frameId);

    public UmaStartResult KeyEvent(SafeUmaHandle handle, ulong frameId, string keyCode)
    {
        using var utf8KeyCode = new UTF8Pinned(keyCode);
        return KeyEventNative(handle, frameId, utf8KeyCode.Pointer);
    }

    public UmaStartResult InputText(SafeUmaHandle handle, ulong frameId, string text)
    {
        using var utf8Text = new UTF8Pinned(text);
        return InputTextNative(handle, frameId, utf8Text.Pointer);
    }

    public UmaStartResult PackageOperation(
        SafeUmaHandle handle,
        string packageId,
        string? activity,
        PackageOperationKind operation)
    {
        using var utf8PackageId = new UTF8Pinned(packageId);
        using var utf8Activity = new UTF8Pinned(activity ?? string.Empty);
        return PackageOperationNative(
            handle, utf8PackageId.Pointer, utf8Activity.Pointer, (int)operation);
    }

    public TemplateMatchResult MatchTemplate(
        SafeUmaHandle handle,
        RecognitionImage screen,
        RecognitionImage templateImage,
        TemplateMatchOptions options)
    {
        ValidateImage(screen);
        ValidateImage(templateImage);
        ArgumentNullException.ThrowIfNull(options);
        int[]? roi = options.Roi is { Length: >= 4 } ? options.Roi : null;
        double[] scales = options.Scales?.ToArray() ?? [];
        using var screenImage = new PinnedImage(screen);
        using var pinnedTemplate = new PinnedImage(templateImage);
        fixed (double* scalePointer = scales)
        {
            var nativeOptions = new UmaTemplateMatchOptionsNative
            {
                Method = (uint)options.Method,
                HasRoi = roi is null ? 0 : 1,
                RoiX = roi is null ? 0 : roi[0],
                RoiY = roi is null ? 0 : roi[1],
                RoiWidth = roi is null ? 0 : roi[2],
                RoiHeight = roi is null ? 0 : roi[3],
                Threshold = options.Threshold,
                ReferenceWidth = options.ReferenceWidth,
                ReferenceHeight = options.ReferenceHeight,
                CandidateStepOverride = options.CandidateStepOverride,
                CandidateStep = options.CandidateStep,
                SampleWidth = options.SampleWidth,
                SampleHeight = options.SampleHeight,
                FullSearchOnMiss = options.FullSearchOnMiss ? 1 : 0,
                RequireTextContrast = options.RequireTextContrast ? 1 : 0,
                Scales = scalePointer,
                ScaleCount = (ulong)scales.Length,
            };
            UmaTemplateMatchResultNative nativeResult = default;
            UmaImageViewNative screenView = screenImage.View;
            UmaImageViewNative templateView = pinnedTemplate.View;
            int error = MatchTemplateNative(
                handle, &screenView, &templateView, &nativeOptions, &nativeResult);
            RequireRecognitionSuccess(error, "UmaMatchTemplate");
            return new TemplateMatchResult(
                nativeResult.Found != 0,
                nativeResult.Score,
                nativeResult.X,
                nativeResult.Y,
                nativeResult.Width,
                nativeResult.Height);
        }
    }

    public TextRecognitionResult RecognizeText(
        SafeUmaHandle handle,
        RecognitionImage image,
        string language)
    {
        ValidateImage(image);
        ArgumentNullException.ThrowIfNull(language);
        using var utf8Language = new UTF8Pinned(language);
        using var pinnedImage = new PinnedImage(image);
        UmaImageViewNative imageView = pinnedImage.View;
        long estimatedCapacity = checked((long)image.Width * image.Height / 2 + 64 * 1024);
        int initialCapacity = (int)Math.Clamp(estimatedCapacity, 256 * 1024, 16 * 1024 * 1024);
        byte[] buffer = new byte[initialCapacity];
        ulong required = 0;
        int result;
        fixed (byte* destination = buffer)
        {
            result = RecognizeTextNative(
                handle, &imageView, utf8Language.Pointer, destination, (ulong)buffer.Length, &required);
        }
        if (result == (int)ConnectionErrorCode.BufferTooSmall)
        {
            if (required is < 1 or > 16 * 1024 * 1024)
                throw new ManagedBridgeException("UmaRecognizeText returned an invalid result length.");
            buffer = new byte[checked((int)required)];
            fixed (byte* destination = buffer)
            {
                result = RecognizeTextNative(
                    handle, &imageView, utf8Language.Pointer, destination, (ulong)buffer.Length, &required);
            }
        }
        RequireRecognitionSuccess(result, "UmaRecognizeText");
        if (required < 1 || required > (ulong)buffer.Length || buffer[checked((int)required - 1)] != 0)
            throw new ManagedBridgeException("UmaRecognizeText returned an invalid JSON buffer.");

        using JsonDocument document = JsonDocument.Parse(buffer.AsMemory(0, checked((int)required - 1)));
        JsonElement root = document.RootElement;
        string selectedLanguage = root.GetProperty("language").GetString()
            ?? throw new InvalidOperationException("The native OCR result has no language.");
        int width = root.GetProperty("width").GetInt32();
        int height = root.GetProperty("height").GetInt32();
        var detections = new List<TextDetection>();
        foreach (JsonElement item in root.GetProperty("detections").EnumerateArray())
        {
            JsonElement bounds = item.GetProperty("bounds");
            detections.Add(new TextDetection(
                item.GetProperty("text").GetString() ?? string.Empty,
                new TextRecognitionRect(
                    bounds.GetProperty("x").GetInt32(),
                    bounds.GetProperty("y").GetInt32(),
                    bounds.GetProperty("width").GetInt32(),
                    bounds.GetProperty("height").GetInt32()),
                item.GetProperty("confidence").GetDouble()));
        }
        return new TextRecognitionResult(detections, selectedLanguage, width, height);
    }

    public bool HasMeaningfulChange(
        SafeUmaHandle handle,
        RecognitionImage before,
        RecognitionImage after,
        TemplateMatchResult match)
    {
        ValidateImage(before);
        ValidateImage(after);
        using var beforeImage = new PinnedImage(before);
        using var afterImage = new PinnedImage(after);
        UmaImageViewNative beforeView = beforeImage.View;
        UmaImageViewNative afterView = afterImage.View;
        UmaTemplateMatchResultNative nativeMatch = ToNative(match);
        int changed = 0;
        int error = CompareImageChangeNative(
            handle, &beforeView, &afterView, &nativeMatch, &changed);
        RequireRecognitionSuccess(error, "UmaCompareImageChange");
        return changed != 0;
    }

    public bool HasPersistentChange(
        SafeUmaHandle handle,
        RecognitionImage before,
        RecognitionImage firstAfter,
        RecognitionImage settledAfter,
        TemplateMatchResult match)
    {
        ValidateImage(before);
        ValidateImage(firstAfter);
        ValidateImage(settledAfter);
        using var beforeImage = new PinnedImage(before);
        using var firstImage = new PinnedImage(firstAfter);
        using var settledImage = new PinnedImage(settledAfter);
        UmaImageViewNative beforeView = beforeImage.View;
        UmaImageViewNative firstView = firstImage.View;
        UmaImageViewNative settledView = settledImage.View;
        UmaTemplateMatchResultNative nativeMatch = ToNative(match);
        int changed = 0;
        int error = ComparePersistentImageChangeNative(
            handle, &beforeView, &firstView, &settledView, &nativeMatch, &changed);
        RequireRecognitionSuccess(error, "UmaComparePersistentImageChange");
        return changed != 0;
    }

    [LibraryImport(LibraryName, EntryPoint = "UmaGetVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial IntPtr GetVersionNative();

    [LibraryImport(LibraryName, EntryPoint = "UmaGetApiVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint GetApiVersionNative();

    [LibraryImport(LibraryName, EntryPoint = "UmaCreate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial IntPtr CreateNative(
        [MarshalAs(UnmanagedType.FunctionPtr)] UmaApiCallback callback,
        IntPtr customArg);

    [LibraryImport(LibraryName, EntryPoint = "UmaDestroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial void DestroyNative(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "UmaSetUserDir")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int SetUserDirNative(byte* path);

    [LibraryImport(LibraryName, EntryPoint = "UmaLoadResource")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int LoadResourceNative(byte* path);

    [LibraryImport(LibraryName, EntryPoint = "UmaSetInstanceOptions")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int SetInstanceOptionsNative(SafeUmaHandle handle, byte* utf8Json);

    [LibraryImport(LibraryName, EntryPoint = "UmaConnectAsync")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaStartResult ConnectNative(
        SafeUmaHandle handle,
        byte* adbPath,
        byte* serial,
        byte* profile);

    [LibraryImport(LibraryName, EntryPoint = "UmaCancelConnect")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int CancelConnectNative(SafeUmaHandle handle, ulong operationId);

    [LibraryImport(LibraryName, EntryPoint = "UmaCancelOperation")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int CancelOperationNative(SafeUmaHandle handle, ulong operationId);

    [LibraryImport(LibraryName, EntryPoint = "UmaVerifyGameAsync")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaStartResult VerifyGameNative(SafeUmaHandle handle, byte* packageId);

    [LibraryImport(LibraryName, EntryPoint = "UmaCaptureAsync")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaStartResult CaptureNative(SafeUmaHandle handle);

    [LibraryImport(LibraryName, EntryPoint = "UmaGetFramePngSize")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int GetFramePngSizeNative(SafeUmaHandle handle, ulong frameId, ulong* size);

    [LibraryImport(LibraryName, EntryPoint = "UmaCopyFramePng")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int CopyFramePngNative(
        SafeUmaHandle handle,
        ulong frameId,
        byte* destination,
        ulong capacity);

    [LibraryImport(LibraryName, EntryPoint = "UmaReleaseFrame")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int ReleaseFrameNative(SafeUmaHandle handle, ulong frameId);

    [LibraryImport(LibraryName, EntryPoint = "UmaTapAsync")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaStartResult TapNative(SafeUmaHandle handle, ulong frameId, int x, int y);

    [LibraryImport(LibraryName, EntryPoint = "UmaSwipeWithOptionsAsync")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaStartResult SwipeWithOptionsNative(
        SafeUmaHandle handle,
        ulong frameId,
        int x1,
        int y1,
        int x2,
        int y2,
        int durationMs,
        int extraSwipe);

    [LibraryImport(LibraryName, EntryPoint = "UmaSwipeAsync")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaStartResult SwipeNative(
        SafeUmaHandle handle,
        ulong frameId,
        int x1,
        int y1,
        int x2,
        int y2,
        int durationMs);

    [LibraryImport(LibraryName, EntryPoint = "UmaTouchAsync")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaStartResult TouchNative(
        SafeUmaHandle handle,
        ulong frameId,
        int x,
        int y,
        int phase);

    [LibraryImport(LibraryName, EntryPoint = "UmaPrepareTouchAsync")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaStartResult PrepareTouchNative(
        SafeUmaHandle handle,
        ulong frameId);

    [LibraryImport(LibraryName, EntryPoint = "UmaKeyEventAsync")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaStartResult KeyEventNative(
        SafeUmaHandle handle,
        ulong frameId,
        byte* utf8KeyCode);

    [LibraryImport(LibraryName, EntryPoint = "UmaInputTextAsync")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaStartResult InputTextNative(
        SafeUmaHandle handle,
        ulong frameId,
        byte* utf8Text);

    [LibraryImport(LibraryName, EntryPoint = "UmaPackageOperationAsync")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaStartResult PackageOperationNative(
        SafeUmaHandle handle,
        byte* utf8PackageId,
        byte* utf8Activity,
        int operation);

    [LibraryImport(LibraryName, EntryPoint = "UmaMatchTemplate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int MatchTemplateNative(
        SafeUmaHandle handle,
        UmaImageViewNative* screen,
        UmaImageViewNative* templateImage,
        UmaTemplateMatchOptionsNative* options,
        UmaTemplateMatchResultNative* result);

    [LibraryImport(LibraryName, EntryPoint = "UmaRecognizeText")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int RecognizeTextNative(
        SafeUmaHandle handle,
        UmaImageViewNative* image,
        byte* language,
        byte* jsonDestination,
        ulong capacity,
        ulong* requiredLength);

    [LibraryImport(LibraryName, EntryPoint = "UmaCompareImageChange")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int CompareImageChangeNative(
        SafeUmaHandle handle,
        UmaImageViewNative* before,
        UmaImageViewNative* after,
        UmaTemplateMatchResultNative* match,
        int* changed);

    [LibraryImport(LibraryName, EntryPoint = "UmaComparePersistentImageChange")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int ComparePersistentImageChangeNative(
        SafeUmaHandle handle,
        UmaImageViewNative* before,
        UmaImageViewNative* firstAfter,
        UmaImageViewNative* settledAfter,
        UmaTemplateMatchResultNative* match,
        int* changed);

    [LibraryImport(LibraryName, EntryPoint = "UmaLoadTemplateImage")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int LoadTemplateImageNative(SafeUmaHandle handle, byte* utf8Path, int preparation, IntPtr* decodedImage);

    [LibraryImport(LibraryName, EntryPoint = "UmaBinarizeColor")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int BinarizeColorNative(UmaImageViewNative* source, UmaColorRuleNative* rule, IntPtr* decodedImage);
    [LibraryImport(LibraryName, EntryPoint = "UmaFindColorBlobs")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int FindColorBlobsNative(UmaImageViewNative* source, UmaColorRuleNative* rule,
        UmaImageRectNative* region, UmaColorBlobNative* destination, ulong capacity, ulong* requiredCount);
    [LibraryImport(LibraryName, EntryPoint = "UmaCountColor")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int CountColorNative(UmaImageViewNative* source, UmaColorRuleNative* rule,
        UmaImageRectNative* region, UmaImageRectNative* exclude, ulong* matching, ulong* sampled);

    [LibraryImport(LibraryName, EntryPoint = "UmaRefreshResourceSnapshot")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int RefreshResourceSnapshotNative(SafeUmaHandle handle);

    [LibraryImport(LibraryName, EntryPoint = "UmaTransformImage")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int TransformImageNative(UmaImageViewNative* source, UmaImageTransformOptionsNative* options, IntPtr* decodedImage);

    [LibraryImport(LibraryName, EntryPoint = "UmaDecodeImageFile")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int DecodeImageFileNative(byte* utf8Path, IntPtr* decodedImage);

    [LibraryImport(LibraryName, EntryPoint = "UmaDecodeImageBytes")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int DecodeImageBytesNative(
        byte* encodedBytes,
        ulong encodedLength,
        IntPtr* decodedImage);

    [LibraryImport(LibraryName, EntryPoint = "UmaCreateImageFromRgba")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int CreateImageFromRgbaNative(
        int width,
        int height,
        byte* rgbaBytes,
        ulong rgbaLength,
        IntPtr* decodedImage);

    [LibraryImport(LibraryName, EntryPoint = "UmaGetDecodedImageInfo")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int GetDecodedImageInfoNative(
        SafeDecodedImageHandle decodedImage,
        UmaDecodedImageInfoNative* info);

    [LibraryImport(LibraryName, EntryPoint = "UmaCopyDecodedImage")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int CopyDecodedImageNative(
        SafeDecodedImageHandle decodedImage,
        byte* grayDestination,
        ulong grayCapacity,
        byte* rgbaDestination,
        ulong rgbaCapacity);

    [LibraryImport(LibraryName, EntryPoint = "UmaReleaseDecodedImage")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial void ReleaseDecodedImageNative(IntPtr decodedImage);

    private static void ValidateImage(RecognitionImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0
            || image.Width > 10000 || image.Height > 10000)
            throw new ArgumentOutOfRangeException(nameof(image), "Recognition image dimensions are invalid.");
        long pixels = checked((long)image.Width * image.Height);
        bool hasGray = image.GrayPixels is { Length: > 0 };
        if (pixels > 100_000_000
            || (hasGray && image.GrayPixels!.Length != pixels)
            || (!hasGray && image.RgbaPixels is null)
            || (image.RgbaPixels is not null && image.RgbaPixels.Length != pixels * 4))
            throw new ArgumentException("Recognition image planes do not match their dimensions.", nameof(image));
    }

    private static UmaTemplateMatchResultNative ToNative(TemplateMatchResult match) =>
        new()
        {
            Found = match.Found ? 1 : 0,
            Score = match.Score,
            X = match.X,
            Y = match.Y,
            Width = match.Width,
            Height = match.Height,
        };

    private static void RequireRecognitionSuccess(int errorCode, string operation)
    {
        if (errorCode != (int)ConnectionErrorCode.Success)
            throw new ManagedBridgeException($"{operation} failed with native error code {errorCode}.");
    }

    private sealed unsafe class PinnedImage : IDisposable
    {
        private GCHandle _gray;
        private GCHandle _rgba;

        internal PinnedImage(RecognitionImage image)
        {
            try
            {
                if (image.GrayPixels is { Length: > 0 } gray)
                    _gray = GCHandle.Alloc(gray, GCHandleType.Pinned);
                if (image.RgbaPixels is { } rgba)
                    _rgba = GCHandle.Alloc(rgba, GCHandleType.Pinned);
                View = new UmaImageViewNative
                {
                    Width = image.Width,
                    Height = image.Height,
                    GrayPixels = _gray.IsAllocated ? (byte*)_gray.AddrOfPinnedObject() : null,
                    GrayLength = _gray.IsAllocated ? (ulong)image.GrayPixels!.Length : 0,
                    RgbaPixels = _rgba.IsAllocated ? (byte*)_rgba.AddrOfPinnedObject() : null,
                    RgbaLength = _rgba.IsAllocated ? (ulong)image.RgbaPixels!.Length : 0,
                };
            }
            catch
            {
                if (_rgba.IsAllocated) _rgba.Free();
                if (_gray.IsAllocated) _gray.Free();
                throw;
            }
        }

        internal UmaImageViewNative View { get; }

        public void Dispose()
        {
            if (_rgba.IsAllocated) _rgba.Free();
            if (_gray.IsAllocated) _gray.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct UmaImageViewNative
    {
        internal int Width;
        internal int Height;
        internal byte* GrayPixels;
        internal ulong GrayLength;
        internal byte* RgbaPixels;
        internal ulong RgbaLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UmaDecodedImageInfoNative
    {
        internal int Width;
        internal int Height;
        internal ulong GrayLength;
        internal ulong RgbaLength;
        internal int Preparation;
        internal int Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UmaImageTransformOptionsNative
    {
        internal int CropX, CropY, CropWidth, CropHeight, TargetWidth, TargetHeight, MeanGray, Nearest;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct UmaTemplateMatchOptionsNative
    {
        internal uint Method;
        internal int HasRoi;
        internal int RoiX;
        internal int RoiY;
        internal int RoiWidth;
        internal int RoiHeight;
        internal double Threshold;
        internal int ReferenceWidth;
        internal int ReferenceHeight;
        internal int CandidateStepOverride;
        internal int CandidateStep;
        internal int SampleWidth;
        internal int SampleHeight;
        internal int FullSearchOnMiss;
        internal int RequireTextContrast;
        internal double* Scales;
        internal ulong ScaleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UmaTemplateMatchResultNative
    {
        internal int Found;
        internal double Score;
        internal int X;
        internal int Y;
        internal int Width;
        internal int Height;
    }

    public int ValidatePipelineJson(string json, out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(json);
        errorMessage = null;
        ulong requiredLength = 0;
        int code = ValidatePipelineJsonNative(json, null, 0, &requiredLength);
        if (code == 0) return 0;
        if (requiredLength > 0)
        {
            byte[] buffer = new byte[requiredLength];
            fixed (byte* ptr = buffer)
            {
                ValidatePipelineJsonNative(json, ptr, requiredLength, &requiredLength);
            }
            errorMessage = System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0');
        }
        return code;
    }

    public UmaStartResult RunPipelineAsync(SafeUmaHandle handle, string pipelineJson, string? entryTask)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(pipelineJson);
        return RunPipelineAsyncNative(handle, pipelineJson, entryTask);
    }

    public UmaStartResult RunGameTaskAsync(SafeUmaHandle handle, string taskId, string settingsJson)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsJson);
        return RunGameTaskAsyncNative(handle, taskId, settingsJson);
    }

    public UmaAppendTaskResult AppendTask(SafeUmaHandle handle, string taskId, string settingsJson)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return AppendTaskNative(handle, taskId, settingsJson ?? "{}");
    }

    public int SetTaskParams(SafeUmaHandle handle, ulong taskId, string settingsJson)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return SetTaskParamsNative(handle, taskId, settingsJson ?? "{}");
    }

    public int ClearTasks(SafeUmaHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return ClearTasksNative(handle);
    }

    public UmaStartResult StartQueueAsync(SafeUmaHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return StartAsyncNative(handle);
    }

    public int GetRunSnapshotJson(SafeUmaHandle handle, byte[]? destination, ulong capacity, out ulong requiredLength)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ulong req = 0;
        int result;
        if (destination is null || capacity == 0)
        {
            result = GetRunSnapshotJsonNative(handle, null, 0, &req);
        }
        else
        {
            fixed (byte* ptr = destination)
            {
                result = GetRunSnapshotJsonNative(handle, ptr, capacity, &req);
            }
        }
        requiredLength = req;
        return result;
    }

    public int GetCatalogJson(SafeUmaHandle handle, string catalogType, byte[]? destination, ulong capacity, out ulong requiredLength)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ulong req = 0;
        int result;
        if (destination is null || capacity == 0)
        {
            result = GetCatalogJsonNative(handle, catalogType, null, 0, &req);
        }
        else
        {
            fixed (byte* ptr = destination)
            {
                result = GetCatalogJsonNative(handle, catalogType, ptr, capacity, &req);
            }
        }
        requiredLength = req;
        return result;
    }

    [LibraryImport(LibraryName, EntryPoint = "UmaValidatePipelineJson", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int ValidatePipelineJsonNative(
        string json,
        byte* errorDestination,
        ulong capacity,
        ulong* requiredLength);

    [LibraryImport(LibraryName, EntryPoint = "UmaRunPipelineAsync", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaStartResult RunPipelineAsyncNative(
        SafeUmaHandle handle,
        string pipelineJson,
        string? entryTask);

    [LibraryImport(LibraryName, EntryPoint = "UmaRunGameTaskAsync", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaStartResult RunGameTaskAsyncNative(
        SafeUmaHandle handle,
        string taskId,
        string settingsJson);

    [LibraryImport(LibraryName, EntryPoint = "UmaAppendTask", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaAppendTaskResult AppendTaskNative(
        SafeUmaHandle handle,
        string taskId,
        string settingsJson);

    [LibraryImport(LibraryName, EntryPoint = "UmaSetTaskParams", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int SetTaskParamsNative(
        SafeUmaHandle handle,
        ulong taskId,
        string settingsJson);

    [LibraryImport(LibraryName, EntryPoint = "UmaClearTasks")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int ClearTasksNative(SafeUmaHandle handle);

    [LibraryImport(LibraryName, EntryPoint = "UmaStartAsync")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial UmaStartResult StartAsyncNative(SafeUmaHandle handle);

    [LibraryImport(LibraryName, EntryPoint = "UmaGetRunSnapshotJson")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int GetRunSnapshotJsonNative(
        SafeUmaHandle handle,
        byte* destination,
        ulong capacity,
        ulong* requiredLength);

    [LibraryImport(LibraryName, EntryPoint = "UmaGetCatalogJson", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int GetCatalogJsonNative(
        SafeUmaHandle handle,
        string catalogType,
        byte* destination,
        ulong capacity,
        ulong* requiredLength);
}
