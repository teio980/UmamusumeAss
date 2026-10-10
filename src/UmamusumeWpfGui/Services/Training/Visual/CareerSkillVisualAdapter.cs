using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

internal enum CareerSkillScanKind
{
    Unknown,
    NotFound,
    Obtained,
    Purchasable,
}

internal sealed record CareerSkillScanResult(
    CareerSkillScanKind Kind,
    TemplateMatchResult? Plus = null);

/// <summary>
/// Skill-page perception and interaction. Every fixed template and screen
/// region comes from the validated Career visual resource package; matches
/// supply dynamic tap coordinates.
/// </summary>
internal sealed class CareerSkillVisualAdapter
{
    private readonly IVisualPipelineRuntime _visual;
    private readonly ConcurrentDictionary<string, Lazy<Task<GrayImage?>>> _templates = new(
        StringComparer.OrdinalIgnoreCase);

    public CareerSkillVisualAdapter(IVisualPipelineRuntime visual)
    {
        _visual = visual ?? throw new ArgumentNullException(nameof(visual));
    }

    public async Task<int?> ReadPointsAsync(CareerFlowContext context)
    {
        var region = RequireRegion(context, "career.skill.points");
        var roi = RequireRoi(region);
        var result = await _visual.DetectTextAsync(
                context.Connection, null,
                Width(context), Height(context), "en-US",
                "career.skill.points", context.CancellationToken)
            .ConfigureAwait(false);
        var number = CareerSkillLearningFlow.ParseNumberInRegion(result, roi);
        if (number is not null)
            return number;

        var focusedRoi = region.FocusedRoi is { Length: >= 4 } focused
            ? (int[])focused.Clone()
            : roi;
        result = await _visual.DetectTextAsync(
                context.Connection, focusedRoi,
                Width(context), Height(context), "en-US",
                "career.skill.points.focused", context.CancellationToken)
            .ConfigureAwait(false);
        return CareerSkillLearningFlow.ParseNumberInRegion(result, focusedRoi);
    }

    public Task<bool> WaitForSkillPageAsync(CareerFlowContext context) =>
        WaitForAssetAsync(context, "career.skill.back", "career.skill.back");

    public async Task<CareerSkillScanResult> ScanForSkillAsync(
        CareerFlowContext context,
        string name)
    {
        var plusTemplate = await LoadAssetAsync(context, "career.skill.plus")
            .ConfigureAwait(false);
        if (plusTemplate is null)
            return new(CareerSkillScanKind.Unknown);

        var listRegion = RequireRegion(context, "career.skill.list");
        var listRoi = RequireRoi(listRegion);
        var maxPages = GetMetadataInt(listRegion, "maxPages");
        var nameRegion = RequireRegion(context, "career.skill.row_name");
        var nameRoi = RequireRoi(nameRegion);
        var xMin = GetMetadataInt(nameRegion, "xMin");
        var xMax = GetMetadataInt(nameRegion, "xMax");
        var yMin = GetMetadataInt(nameRegion, "yMin");
        var yMax = GetMetadataInt(nameRegion, "yMax");
        var minimumSimilarity = GetMetadataDouble(nameRegion, "minSimilarity");
        var rowStatusRegion = RequireRegion(context, "career.skill.row_status");
        var plusRegion = RequireRegion(context, "career.skill.plus_search");
        var maxHeight = GetMetadataInt(plusRegion, "maxHeight");
        var heightFromBottom = GetMetadataInt(plusRegion, "heightFromBottom");
        var minimumHeight = GetMetadataInt(plusRegion, "minimumHeight");
        var yFromFoundPlus = GetMetadataBool(plusRegion, "yFromFoundPlus");

        ulong? lastPage = null;
        var unchangedPages = 0;
        for (var page = 0; page <= maxPages; page++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var frame = await _visual.CaptureGrayAsync(
                    context.Connection, context.CancellationToken)
                .ConfigureAwait(false);
            if (frame is null)
                return new(CareerSkillScanKind.Unknown);
            var text = await _visual.DetectTextAsync(
                    frame, listRoi, Width(context), Height(context),
                    "en-US", "career.skill.list", context.CancellationToken)
                .ConfigureAwait(false);
            if (text is null)
                return new(CareerSkillScanKind.Unknown);

            var matches = text.Detections.Where(detection =>
                    detection.Bounds.X >= xMin
                    && detection.Bounds.X <= xMax
                    && detection.Bounds.Y >= yMin
                    && detection.Bounds.Y <= yMax
                    && detection.Bounds.CenterX >= nameRoi[0]
                    && detection.Bounds.CenterX <= nameRoi[0] + nameRoi[2]
                    && detection.Bounds.CenterY >= nameRoi[1]
                    && detection.Bounds.CenterY <= nameRoi[1] + nameRoi[3]
                    && CareerSkillLearningFlow.NameSimilarity(name, detection.Text)
                        >= minimumSimilarity)
                .ToArray();
            if (matches.Length > 1)
                return new(CareerSkillScanKind.Unknown);
            if (matches.Length == 1)
            {
                var title = matches[0];
                var rowStatusRoi = RequireRoi(rowStatusRegion);
                var rowTopOffset = GetMetadataInt(rowStatusRegion, "rowTopOffset");
                var statusRoi = new[]
                {
                    rowStatusRoi[0],
                    Math.Max(rowStatusRoi[1], title.Bounds.Y + rowTopOffset),
                    GetMetadataInt(rowStatusRegion, "width"),
                    GetMetadataInt(rowStatusRegion, "height"),
                };
                var rowText = await _visual.DetectTextAsync(
                        frame, statusRoi, Width(context), Height(context),
                        "en-US", "career.skill.row_status", context.CancellationToken)
                    .ConfigureAwait(false);
                if (rowText?.Detections.Any(detection =>
                        detection.Text.Contains("Obtained", StringComparison.OrdinalIgnoreCase)) == true)
                {
                    return new(CareerSkillScanKind.Obtained);
                }

                var roiTop = yFromFoundPlus
                    ? Math.Clamp(
                        title.Bounds.Y + GetMetadataInt(plusRegion, "yOffset"),
                        GetMetadataInt(plusRegion, "minimumY"),
                        GetMetadataInt(plusRegion, "maximumY"))
                    : plusRegion.Roi![1];
                var roiHeight = Math.Max(minimumHeight,
                    Math.Min(maxHeight, heightFromBottom - roiTop));
                var plusRoi = new[] { plusRegion.Roi![0], roiTop, plusRegion.Roi[2], roiHeight };
                var plus = TemplateMatcher.FindColor(
                    frame, plusTemplate, plusRoi,
                    AssetThreshold(context, "career.skill.plus"),
                    Width(context), Height(context));
                if (plus.Found)
                    return new(CareerSkillScanKind.Purchasable, plus);
                // A title clipped by the viewport can acquire a visible + on
                // the next page; never tap a guessed row coordinate.
            }

            var signature = ListSignature(
                frame, listRoi, Width(context), Height(context));
            unchangedPages = signature == lastPage ? unchangedPages + 1 : 0;
            if (unchangedPages >= 2 || page == maxPages)
                return new(CareerSkillScanKind.NotFound);
            lastPage = signature;
            var scrollRegion = RequireRegion(context, "career.skill.scroll");
            await _visual.SwipeAsync(
                    context.Connection,
                    GetMetadataIntArray(scrollRegion, "swipe"),
                    Width(context), Height(context),
                    "career.skill.scroll", context.CancellationToken)
                .ConfigureAwait(false);
            await _visual.DelayAsync(
                    GetMetadataInt(scrollRegion, "scrollDelayMs"), context.CancellationToken)
                .ConfigureAwait(false);
        }

        return new(CareerSkillScanKind.NotFound);
    }

    public async Task<int?> ReadPriceAsync(
        CareerFlowContext context,
        TemplateMatchResult plus)
    {
        var region = RequireRegion(context, "career.skill.price");
        var roi = RequireRoi(region);
        var offset = GetMetadataInt(region, "centerYOffset");
        roi[1] = Math.Max(region.Roi![1] + region.Roi[3] / 2, plus.CenterY + offset);
        return await ReadNumberAsync(context, roi, "career.skill.price")
            .ConfigureAwait(false);
    }

    public async Task<bool> PurchaseAsync(
        CareerFlowContext context,
        string skillName,
        TemplateMatchResult plus)
    {
        await _visual.TapMatchAsync(
                context.Connection, plus, "career.skill.plus", context.CancellationToken)
            .ConfigureAwait(false);
        var plusRegion = RequireRegion(context, "career.skill.plus_search");
        await _visual.DelayAsync(
                MetadataIntOr(plusRegion, "tapDelayMs", 350), context.CancellationToken)
            .ConfigureAwait(false);

        var selected = await ScanForSkillAsync(context, skillName).ConfigureAwait(false);
        if (selected.Kind != CareerSkillScanKind.Obtained)
            return PurchaseNotConfirmed(context, skillName, "the selected skill row was not recognized");

        if (!await TapAssetAsync(context, "career.skill.confirm", "career.skill.confirm")
                .ConfigureAwait(false))
        {
            return PurchaseNotConfirmed(context, skillName, "the Confirm button was not recognized");
        }
        if (!await WaitForAssetAsync(
                    context, "career.skill.confirmation.title", "career.skill.confirmation.title")
                .ConfigureAwait(false))
        {
            return PurchaseNotConfirmed(context, skillName, "the purchase confirmation page did not appear");
        }

        var nameRoi = GetRegionRoi(context, "career.skill.confirmation.name");
        var minimumNameSimilarity = GetMetadataDouble(
            RequireRegion(context, "career.skill.confirmation.name"), "minSimilarity");
        var dialogText = await _visual.DetectTextAsync(
                context.Connection, nameRoi, Width(context), Height(context),
                "en-US", "career.skill.confirmation.name", context.CancellationToken)
            .ConfigureAwait(false);
        if (dialogText is null || !dialogText.Detections.Any(detection =>
                CareerSkillLearningFlow.NameSimilarity(skillName, detection.Text)
                    >= minimumNameSimilarity))
        {
            return PurchaseNotConfirmed(context, skillName, "the confirmation did not identify the requested skill");
        }

        if (!await TapAssetAsync(
                    context, "career.skill.confirmation.learn", "career.skill.confirmation.learn")
                .ConfigureAwait(false))
        {
            return PurchaseNotConfirmed(context, skillName, "the Learn button was not recognized");
        }
        if (!await WaitForAssetAsync(
                    context, "career.skill.learned.title", "career.skill.learned.title")
                .ConfigureAwait(false))
        {
            return PurchaseNotConfirmed(context, skillName, "the learning completion dialog did not appear");
        }

        // The completion dialog confirms the purchase. Returning to the
        // race is a separate operation and cannot undo this evidence.
        Log(context, $"Confirmed learning completion for {skillName}.");
        if (!await TapAssetAsync(
                    context, "career.skill.learned.close", "career.skill.learned.close")
                .ConfigureAwait(false))
        {
            Log(context, $"{skillName} was learned; closing its completion dialog will be retried while returning to Race Day.");
        }
        return true;
    }

    private static bool PurchaseNotConfirmed(CareerFlowContext context, string skillName, string reason)
    {
        Log(context, $"Purchase of {skillName} was not confirmed: {reason}.");
        return false;
    }

    private async Task<GrayImage?> CaptureReturnFrameAsync(CareerFlowContext context)
    {
        try
        {
            return await _visual.CaptureGrayAsync(context.Connection, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not DateChangedInterruptionException and not DateChangedRecoveryException)
        {
            return null;
        }
    }

    public async Task<bool> ReturnToRaceDayAsync(CareerFlowContext context)
    {
        var started = Stopwatch.StartNew();
        var raceDayFrames = 0;
        var backSubmitted = false;
        for (var attempt = 0; attempt < 20 && started.Elapsed < TimeSpan.FromSeconds(10); attempt++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var frame = await CaptureReturnFrameAsync(context).ConfigureAwait(false);
            if (frame is null)
            {
                raceDayFrames = 0;
                await _visual.DelayAsync(200, context.CancellationToken).ConfigureAwait(false);
                continue;
            }

            // Check overlays and Back on one fresh frame. Waiting for each absent
            // dialog, or recognizing every Career screen first, delays Back.
            if (await TryTapAssetAsync(
                    context, frame, "career.skill.learned.title", "career.skill.learned.close")
                .ConfigureAwait(false))
            {
                raceDayFrames = 0;
                continue;
            }
            if (await TryTapAssetAsync(
                    context, frame, "career.skill.confirmation.title", "career.skill.confirmation.cancel")
                .ConfigureAwait(false))
            {
                raceDayFrames = 0;
                continue;
            }
            if (await TryTapAssetAsync(
                    context, frame, "career.skill.exit.title", "career.skill.exit.ok")
                .ConfigureAwait(false))
            {
                raceDayFrames = 0;
                continue;
            }
            var back = await MatchAssetAsync(context, frame, "career.skill.back").ConfigureAwait(false);
            if (back?.Found == true)
            {
                raceDayFrames = 0;
                if (!backSubmitted)
                {
                    await TapMatchedAssetAsync(context, back, "career.skill.back").ConfigureAwait(false);
                    backSubmitted = true;
                    Log(context, "Clicked Skills Back; waiting for Race Day.");
                }
                else
                {
                    await _visual.DelayAsync(200, context.CancellationToken).ConfigureAwait(false);
                }
                continue;
            }

            if (await IsRaceDayAsync(context, frame).ConfigureAwait(false))
            {
                if (++raceDayFrames >= 2)
                {
                    Log(context, $"Returned from Skills to Race Day (elapsedMs={started.ElapsedMilliseconds}, captures={attempt + 1}).");
                    return true;
                }
                await _visual.DelayAsync(120, context.CancellationToken).ConfigureAwait(false);
                continue;
            }
            raceDayFrames = 0;

            if (!backSubmitted)
            {
                var fallback = GetRequiredRegionCenter(context, "career.skill.back", "fallbackCenter");
                await _visual.TapAsync(
                        context.Connection, fallback.X, fallback.Y,
                        Width(context), Height(context),
                        "career.skill.back.fallback", context.CancellationToken)
                    .ConfigureAwait(false);
                backSubmitted = true;
                Log(context, "Skills Back template was not found; tapped its declared fallback position.");
                await _visual.DelayAsync(
                        MetadataIntOr(RequireRegion(context, "career.skill.back"), "tapDelayMs", 300),
                        context.CancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _visual.DelayAsync(200, context.CancellationToken).ConfigureAwait(false);
            }
        }
        return false;
    }

    private async Task<int?> ReadNumberAsync(
        CareerFlowContext context,
        int[] roi,
        string taskName)
    {
        var result = await _visual.DetectTextAsync(
                context.Connection, null,
                Width(context), Height(context), "en-US", taskName,
                context.CancellationToken)
            .ConfigureAwait(false);
        var number = CareerSkillLearningFlow.ParseNumberInRegion(result, roi);
        if (number is not null)
            return number;

        result = await _visual.DetectTextAsync(
                context.Connection, roi,
                Width(context), Height(context), "en-US", taskName + ".focused",
                context.CancellationToken)
            .ConfigureAwait(false);
        return CareerSkillLearningFlow.ParseNumberInRegion(result, roi);
    }

    private async Task<bool> TryTapAssetAsync(
        CareerFlowContext context,
        GrayImage frame,
        string markerId,
        string buttonId)
    {
        var marker = await MatchAssetAsync(context, frame, markerId).ConfigureAwait(false);
        if (marker?.Found != true)
            return false;

        var button = await MatchAssetAsync(context, frame, buttonId).ConfigureAwait(false);
        if (button?.Found == true)
            await TapMatchedAssetAsync(context, button, buttonId).ConfigureAwait(false);
        else
            await _visual.DelayAsync(200, context.CancellationToken).ConfigureAwait(false);
        // A visible overlay blocks Back even if its button is still animating.
        return true;
    }

    private async Task<TemplateMatchResult?> MatchAssetAsync(
        CareerFlowContext context, GrayImage frame, string assetId)
    {
        var template = await LoadAssetAsync(context, assetId).ConfigureAwait(false);
        if (template is null)
            return null;
        var region = RequireRegion(context, assetId);
        return TemplateMatcher.FindColor(frame, template, RequireRoi(region),
            region.Threshold ?? AssetThreshold(context, assetId), Width(context), Height(context));
    }

    private async Task TapMatchedAssetAsync(
        CareerFlowContext context, TemplateMatchResult match, string assetId)
    {
        await _visual.TapMatchAsync(
                context.Connection, match, assetId, context.CancellationToken)
            .ConfigureAwait(false);
        await _visual.DelayAsync(
                MetadataIntOr(RequireRegion(context, assetId), "tapDelayMs", 350),
                context.CancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> WaitForAssetAsync(
        CareerFlowContext context,
        string assetId,
        string regionId,
        int? timeoutMilliseconds = null,
        string timeoutMetadataKey = "waitTimeoutMs",
        string pollMetadataKey = "pollIntervalMs")
    {
        var template = await LoadAssetAsync(context, assetId).ConfigureAwait(false);
        if (template is null)
            return false;
        var region = RequireRegion(context, regionId);
        var match = await _visual.WaitForColorMatchAsync(
                context.Connection, AssetPath(context, assetId), RequireRoi(region),
                region.Threshold ?? AssetThreshold(context, assetId),
                Width(context), Height(context),
                timeoutMilliseconds ?? MetadataIntOr(region, timeoutMetadataKey, 5_000),
                MetadataIntOr(region, pollMetadataKey, 250),
                assetId, string.Empty, context.CancellationToken)
            .ConfigureAwait(false);
        return match?.Found == true;
    }

    private async Task<bool> TapAssetAsync(
        CareerFlowContext context,
        string assetId,
        string regionId,
        int? timeoutMilliseconds = null,
        string timeoutMetadataKey = "waitTimeoutMs",
        string pollMetadataKey = "pollIntervalMs")
    {
        var template = await LoadAssetAsync(context, assetId).ConfigureAwait(false);
        if (template is null)
            return false;
        var region = RequireRegion(context, regionId);
        var match = await _visual.WaitForColorMatchAsync(
                context.Connection, AssetPath(context, assetId), RequireRoi(region),
                region.Threshold ?? AssetThreshold(context, assetId),
                Width(context), Height(context),
                timeoutMilliseconds ?? MetadataIntOr(region, timeoutMetadataKey, 5_000),
                MetadataIntOr(region, pollMetadataKey, 250),
                assetId, string.Empty, context.CancellationToken)
            .ConfigureAwait(false);
        if (match?.Found != true)
            return false;
        await _visual.TapMatchAsync(
                context.Connection, match, assetId, context.CancellationToken)
            .ConfigureAwait(false);
        await _visual.DelayAsync(
                MetadataIntOr(region, "tapDelayMs", 350), context.CancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async Task<GrayImage?> LoadAssetAsync(CareerFlowContext context, string assetId)
    {
        var path = AssetPath(context, assetId);
        return await LoadTemplateAsync(context, path).ConfigureAwait(false);
    }

    private async Task<GrayImage?> LoadTemplateAsync(CareerFlowContext context, string path)
    {
        var lazy = _templates.GetOrAdd(path,
            key => new Lazy<Task<GrayImage?>>(
                () => _visual.LoadTemplateAsync(key, string.Empty, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return await lazy.Value.WaitAsync(context.CancellationToken).ConfigureAwait(false);
    }

    private static string AssetPath(CareerFlowContext context, string id) =>
        RequireResources(context).ResolveVisualResource(id);

    private static double AssetThreshold(CareerFlowContext context, string id) =>
        RequireResources(context).TryGetAsset(id, out var asset) && asset?.Threshold is { } threshold
            ? threshold
            : throw new InvalidDataException($"Career skill asset '{id}' has no threshold.");

    private async Task<bool> IsRaceDayAsync(CareerFlowContext context, GrayImage frame)
    {
        var screen = context.Pack.ScreenProfile.Find("race_day");
        if (screen is null)
            return false;
        var resources = RequireResources(context);
        var recognition = screen.Recognition;
        foreach (var path in screen.Templates)
        {
            var template = await LoadTemplateAsync(context, resources.ResolveScreenTemplate(screen, path))
                .ConfigureAwait(false);
            if (template is null)
                continue;
            var match = recognition.MatchAlphaTemplate || recognition.MatchColorText
                ? TemplateMatcher.FindColor(frame, template, recognition.Roi,
                    recognition.TemplateThreshold, Width(context), Height(context),
                    requireTextContrast: recognition.MatchColorText)
                : TemplateMatcher.Find(frame, template, recognition.Roi,
                    recognition.TemplateThreshold, Width(context), Height(context));
            if (!match.Found)
                continue;
            if (string.IsNullOrWhiteSpace(recognition.RequiredTemplate))
                return true;
            var required = await LoadTemplateAsync(context,
                    resources.ResolveScreenTemplate(screen, recognition.RequiredTemplate))
                .ConfigureAwait(false);
            return required is not null && TemplateMatcher.Find(frame, required,
                recognition.RequiredTemplateRoi, recognition.RequiredTemplateThreshold,
                Width(context), Height(context)).Found;
        }
        return false;
    }

    private static CareerVisualRegionDefinition RequireRegion(
        CareerFlowContext context,
        string id) =>
        RequireResources(context).TryGetRegion(id, out var region) && region is not null
            ? region
            : throw new InvalidDataException($"Career visual catalog is missing region '{id}'.");

    private static CareerVisualResourcePackage RequireResources(CareerFlowContext context) =>
        context.Pack?.VisualResources
            ?? throw new InvalidDataException("Career visual resource package is not loaded.");

    private static int[] GetRegionRoi(CareerFlowContext context, string id) =>
        RequireRoi(RequireRegion(context, id));

    private static int[] RequireRoi(CareerVisualRegionDefinition region) =>
        region.Roi is { Length: >= 4 } roi
            ? (int[])roi.Clone()
            : throw new InvalidDataException($"Career visual region '{region.Id}' has no ROI.");

    private static (int X, int Y) GetRequiredRegionCenter(
        CareerFlowContext context,
        string id,
        string metadataName)
    {
        var region = RequireRegion(context, id);
        if (region.Metadata?.TryGetValue(metadataName, out var value) == true
            && value.ValueKind == JsonValueKind.Array)
        {
            var coords = value.EnumerateArray().Select(item => item.GetInt32()).ToArray();
            if (coords.Length == 2)
                return (coords[0], coords[1]);
        }
        throw new InvalidDataException(
            $"Career visual region '{id}' is missing center metadata '{metadataName}'.");
    }

    private static int GetMetadataInt(CareerVisualRegionDefinition region, string key) =>
        region.Metadata?.TryGetValue(key, out var value) == true
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : throw new InvalidDataException($"Career visual region '{region.Id}' is missing integer '{key}'.");

    private static int MetadataIntOr(CareerVisualRegionDefinition region, string key, int fallback) =>
        region.Metadata?.TryGetValue(key, out var value) == true
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : fallback;

    private static double GetMetadataDouble(CareerVisualRegionDefinition region, string key) =>
        region.Metadata?.TryGetValue(key, out var value) == true
        && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : throw new InvalidDataException($"Career visual region '{region.Id}' is missing number '{key}'.");

    private static bool GetMetadataBool(CareerVisualRegionDefinition region, string key) =>
        region.Metadata?.TryGetValue(key, out var value) == true
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new InvalidDataException($"Career visual region '{region.Id}' is missing boolean '{key}'.");

    private static int[] GetMetadataIntArray(CareerVisualRegionDefinition region, string key)
    {
        if (region.Metadata?.TryGetValue(key, out var value) != true
            || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Career visual region '{region.Id}' is missing array '{key}'.");
        }

        var values = value.EnumerateArray().Select(item => item.GetInt32()).ToArray();
        return values.Length == 5
            ? values
            : throw new InvalidDataException($"Career visual region '{region.Id}' array '{key}' must have five values.");
    }

    private static int Width(CareerFlowContext context) =>
        context.Pack?.ScreenProfile.ReferenceWidth
            ?? throw new InvalidDataException("Career screen reference width is missing.");

    private static int Height(CareerFlowContext context) =>
        context.Pack?.ScreenProfile.ReferenceHeight
            ?? throw new InvalidDataException("Career screen reference height is missing.");

    private static ulong ListSignature(
        GrayImage frame,
        int[] referenceRoi,
        int referenceWidth,
        int referenceHeight)
    {
        var pixels = frame.Pixels;
        ulong hash = 14695981039346656037;
        var xStart = Math.Clamp(
            (int)Math.Round(referenceRoi[0] * frame.Width / (double)Math.Max(1, referenceWidth)),
            0,
            frame.Width);
        var yStart = Math.Clamp(
            (int)Math.Round(referenceRoi[1] * frame.Height / (double)Math.Max(1, referenceHeight)),
            0,
            frame.Height);
        var xEnd = Math.Clamp(
            (int)Math.Round((referenceRoi[0] + referenceRoi[2])
                * frame.Width / (double)Math.Max(1, referenceWidth)),
            xStart,
            frame.Width);
        var yEnd = Math.Clamp(
            (int)Math.Round((referenceRoi[1] + referenceRoi[3])
                * frame.Height / (double)Math.Max(1, referenceHeight)),
            yStart,
            frame.Height);
        for (var y = yStart; y < yEnd; y++)
        {
            for (var x = xStart; x < xEnd; x++)
            {
                hash ^= pixels[y * frame.Width + x];
                hash *= 1099511628211;
            }
        }
        return hash;
    }

    private static void Log(CareerFlowContext context, string message) =>
        context.LogSink?.Add("Career Training", message, LogEntryKind.Info);
}
