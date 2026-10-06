using System.Diagnostics;
using System.Globalization;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

internal delegate Task<int?> CareerNumericOcrFallback(
    GrayImage frame, int[] roi, int referenceWidth, int referenceHeight,
    int maximum, CancellationToken fallbackToken, CancellationToken cancellationToken);

internal sealed record UraTrainingOcrFieldEvidence(string RawText, int? Value, string Source);

internal sealed class UraSmartTrainingCandidateReader
{
    private sealed record GainPixelPolicy(
        int WarmRedMin,
        int WarmGreenMin,
        int WarmGreenMax,
        int WarmBlueMax,
        int WarmRedDelta,
        double StripRatio,
        double BlankRatio,
        int MinimumWarmPixels)
    {
        public static GainPixelPolicy From(UraScreenTextRegion? region)
        {
            var metadata = region?.Metadata;
            return new(
                ReadInt(metadata, "warmRedMin", 240),
                ReadInt(metadata, "warmGreenMin", 95),
                ReadInt(metadata, "warmGreenMax", 225),
                ReadInt(metadata, "warmBlueMax", 95),
                ReadInt(metadata, "warmRedDelta", 25),
                ReadDouble(metadata, "stripRatio", 0.002),
                ReadDouble(metadata, "blankRatio", 0.0005),
                ReadInt(metadata, "minimumWarmPixels", 30));
        }

        private static int ReadInt(
            Dictionary<string, System.Text.Json.JsonElement>? metadata,
            string key,
            int fallback) =>
            metadata is not null
            && metadata.TryGetValue(key, out var value)
            && value.TryGetInt32(out var parsed)
                ? parsed
                : fallback;

        private static double ReadDouble(
            Dictionary<string, System.Text.Json.JsonElement>? metadata,
            string key,
            double fallback) =>
            metadata is not null
            && metadata.TryGetValue(key, out var value)
            && value.TryGetDouble(out var parsed)
                ? parsed
                : fallback;
    }

    private static readonly string[] CandidateFields =
        ["speed", "stamina", "power", "guts", "wit", "skill_points", "failure_rate"];
    private static readonly string[] CoreFields =
        ["speed", "stamina", "power", "guts", "wit", "skill_points"];
    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly CareerNumericOcrFallback _fallback;
    private sealed record CachedField(GrayImage Image, bool Blank, UraTrainingOcrFieldEvidence Evidence,
        double Confidence);
    private Dictionary<string, CachedField> _previousFields = new();
    private string? _previousType;

    public int LastWindowsOcrCalls { get; private set; }
    public int LastFallbackCalls { get; private set; }
    public int LastReusedFields { get; private set; }

    public IReadOnlyDictionary<string, UraTrainingOcrFieldEvidence> LastReadings { get; private set; } =
        new Dictionary<string, UraTrainingOcrFieldEvidence>();

    public UraSmartTrainingCandidateReader(
        IVisualPipelineRuntime visualRuntime, CareerNumericOcrFallback? fallback = null)
    {
        _visualRuntime = visualRuntime ?? throw new ArgumentNullException(nameof(visualRuntime));
        _fallback = fallback ?? CareerNumericOcrReader.TryReadAsync;
    }

    public async Task<UraTrainingCandidate> ReadAsync(
        UraScenarioPack pack, GrayImage frame, string trainingType,
        CancellationToken cancellationToken, CancellationToken? fallbackBudgetToken = null,
        CareerNumericOcrBudget? sharedFallbackBudget = null,
        TemplateMatchResult? selectedLogo = null,
        bool optimize = false, bool reusePreviousRead = false)
    {
        var evidence = new Dictionary<string, UraTrainingOcrFieldEvidence>(StringComparer.OrdinalIgnoreCase);
        LastReadings = evidence;
        LastWindowsOcrCalls = LastFallbackCalls = LastReusedFields = 0;
        var screen = pack.ScreenProfile.Find("training_selection");
        if (screen is null)
            return Unknown(trainingType);

        var budget = sharedFallbackBudget ?? new CareerNumericOcrBudget();
        var fallbackToken = fallbackBudgetToken ?? CancellationToken.None;
        var values = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        var confidences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var gainRegion = screen.FindOcrRegion("training.gain_strip");
        var gainPolicy = GainPixelPolicy.From(gainRegion);
        var gainStripVisible = HasGainStripEvidence(frame, gainRegion?.ToRoi(),
            pack.ScreenProfile.ReferenceWidth, pack.ScreenProfile.ReferenceHeight, gainPolicy);
        var prepared = new Dictionary<string, GrayImage>();
        var blanks = new HashSet<string>();
        var reused = new Dictionary<string, CachedField>();
        var nextCache = new Dictionary<string, CachedField>();
        IReadOnlyDictionary<string, ScreenTextRecognitionResult?>? batch = null;
        if (optimize)
        {
            foreach (var field in CandidateFields)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (screen.FindOcrRegion("training." + field)?.ToRoi() is not { } roi)
                    continue;
                if (field == "failure_rate")
                    roi = ResolveFailureRateRoi(pack, frame, trainingType, roi, selectedLogo);
                var image = PrepareFieldImage(frame, roi, pack.ScreenProfile.ReferenceWidth,
                    pack.ScreenProfile.ReferenceHeight, field != "failure_rate", gainPolicy);
                if (image is null)
                    continue;
                prepared[field] = image;
                // Blank columns still require the authored orange-strip evidence and an
                // empty prepared crop. OCR failure alone must never be treated as zero.
                var blank = field != "failure_rate" && gainStripVisible
                    && IsVisiblyBlank(frame, roi, pack.ScreenProfile.ReferenceWidth,
                        pack.ScreenProfile.ReferenceHeight, gainPolicy)
                    && image.Pixels.All(pixel => pixel == 255);
                if (blank)
                    blanks.Add(field);
                if (reusePreviousRead && _previousType == trainingType
                    && _previousFields.TryGetValue(field, out var cached)
                    && cached.Evidence.Value is not null && cached.Blank == blank
                    && SamePixels(image, cached.Image))
                    reused[field] = cached;
            }
            var pending = prepared.Where(item => !blanks.Contains(item.Key)
                && !reused.ContainsKey(item.Key)).ToArray();
            if (pending.Length > 0)
            {
                LastWindowsOcrCalls++;
                batch = await UraSmartTrainingOcrBatch.ReadAsync(_visualRuntime, pending,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        foreach (var field in CandidateFields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values[field] = null;
            confidences[field] = 0;
            var region = screen.FindOcrRegion("training." + field);
            if (region?.ToRoi() is not { } roi)
            {
                evidence[field] = new("", null, "missing-roi");
                continue;
            }

            if (field == "failure_rate")
                roi = ResolveFailureRateRoi(pack, frame, trainingType, roi, selectedLogo);

            if (reused.TryGetValue(field, out var cached))
            {
                values[field] = cached.Evidence.Value;
                confidences[field] = cached.Confidence;
                evidence[field] = cached.Evidence with { Source = "stable-pixels/" + cached.Evidence.Source };
                nextCache[field] = cached;
                LastReusedFields++;
                continue;
            }
            if (blanks.Contains(field))
            {
                values[field] = 0;
                confidences[field] = 0.82;
                evidence[field] = new("", 0, "visual-blank");
                nextCache[field] = new(prepared[field], true, evidence[field], 0.82);
                continue;
            }

            ScreenTextRecognitionResult? recognized = null;
            var fieldImage = optimize ? prepared.GetValueOrDefault(field)
                : PrepareFieldImage(frame, roi, pack.ScreenProfile.ReferenceWidth,
                    pack.ScreenProfile.ReferenceHeight, field != "failure_rate", gainPolicy);
            try
            {
                if (optimize)
                    recognized = batch?.GetValueOrDefault(field);
                else if (fieldImage is not null)
                {
                    // Reuse countdown enlargement. Orange glyphs need a clean background,
                    // otherwise character art and the white outline confuse numeric OCR.
                    var enlarged = CareerNumericOcrReader.Upscale(fieldImage, 2);
                    LastWindowsOcrCalls++;
                    recognized = await _visualRuntime.DetectTextAsync(enlarged,
                        [0, 0, enlarged.Width, enlarged.Height], enlarged.Width, enlarged.Height,
                        "en-US", "training_selection." + field, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                and not DateChangedInterruptionException and not DateChangedRecoveryException)
            {
                // Numeric fallback uses this same frame, without taking another screenshot.
            }

            var detections = recognized?.Detections.Where(item => !string.IsNullOrWhiteSpace(item.Text))
                .ToArray() ?? [];
            var rawText = string.Join(" | ", detections.Select(item => item.Text));
            var maximum = field == "failure_rate" ? 100 : 999;
            values[field] = CareerOcrNumberParser.ParseTrainingNumber(
                detections.Select(item => item.Text), maximum);
            var source = "unknown";
            if (values[field] is not null)
            {
                confidences[field] = detections.Min(item => Math.Clamp(item.Confidence, 0, 1));
                source = "windows-ocr";
            }
            else if (recognized is not null && detections.Length == 0 && gainStripVisible
                && field != "failure_rate" && IsVisiblyBlank(frame, roi,
                    pack.ScreenProfile.ReferenceWidth, pack.ScreenProfile.ReferenceHeight, gainPolicy))
            {
                // OCR failure alone never becomes zero; authored visual evidence is required.
                values[field] = 0;
                confidences[field] = 0.82;
                source = "visual-blank";
            }
            else if (fieldImage is not null && !fallbackToken.IsCancellationRequested)
            {
                try
                {
                    var fallbackValue = await budget.RunAsync(token =>
                    {
                        LastFallbackCalls++;
                        return _fallback(fieldImage, [0, 0, fieldImage.Width, fieldImage.Height],
                            fieldImage.Width, fieldImage.Height, maximum, token, cancellationToken);
                    },
                        cancellationToken, fallbackToken)
                        .ConfigureAwait(false);
                    if (fallbackValue is >= 0 && fallbackValue <= maximum)
                    {
                        values[field] = fallbackValue;
                        confidences[field] = 0.82;
                        source = "tesseract-fallback";
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Candidate fallback budget expired. Core values stay unknown.
                }
                catch (Exception exception) when (exception is not OperationCanceledException
                    and not DateChangedInterruptionException and not DateChangedRecoveryException)
                {
                    // The existing optional OCR executable can be unavailable.
                }
            }
            evidence[field] = new(rawText, values[field], source);
            if (optimize && fieldImage is not null)
                nextCache[field] = new(fieldImage, false, evidence[field], confidences[field]);
        }

        _previousType = optimize ? trainingType : null;
        _previousFields = nextCache;

        var coreConfidence = CoreFields.All(field => values[field] is not null)
            ? CoreFields.Min(field => confidences[field]) : 0;
        return new(trainingType, values["speed"], values["stamina"], values["power"],
            values["guts"], values["wit"], values["skill_points"], values["failure_rate"],
            CoreConfidence: coreConfidence, FailureRateConfidence: confidences["failure_rate"]);
    }

    public static UraTrainingCandidate MergeStable(
        UraTrainingCandidate first,
        UraTrainingCandidate second)
    {
        static int? Same(int? left, int? right) => left is not null && left == right ? left : null;
        static double StableConfidence(int? value, int? other, double firstConfidence, double secondConfidence) =>
            value is not null && value == other
                ? Math.Min(firstConfidence, secondConfidence)
                : 0;

        var speed = Same(first.SpeedGain, second.SpeedGain);
        var stamina = Same(first.StaminaGain, second.StaminaGain);
        var power = Same(first.PowerGain, second.PowerGain);
        var guts = Same(first.GutsGain, second.GutsGain);
        var wit = Same(first.WitGain, second.WitGain);
        var skill = Same(first.SkillPointGain, second.SkillPointGain);
        var failure = Same(first.FailureRatePercent, second.FailureRatePercent);
        var core = new[] { speed, stamina, power, guts, wit, skill };
        var coreConfidence = core.All(item => item is not null)
            ? StableConfidence(speed, second.SpeedGain, first.CoreConfidence, second.CoreConfidence)
            : 0;
        var failureConfidence = StableConfidence(
            failure, second.FailureRatePercent,
            first.FailureRateConfidence, second.FailureRateConfidence);
        var support = Same(first.UnbondedSupportCount, second.UnbondedSupportCount);
        var hints = Same(first.HintCount, second.HintCount);
        var level = Same(first.TrainingLevel, second.TrainingLevel);
        var auxiliary = support is not null || hints is not null || level is not null
            ? Math.Min(first.AuxiliaryConfidence, second.AuxiliaryConfidence)
            : 0;
        return new(first.TrainingType, speed, stamina, power, guts, wit, skill,
            failure, level, support, hints, coreConfidence, failureConfidence, auxiliary);
    }

    private static UraTrainingCandidate Unknown(string trainingType) =>
        new(trainingType, null, null, null, null, null, null, null);

    private static bool SamePixels(GrayImage first, GrayImage second) =>
        first.Width == second.Width && first.Height == second.Height
        && first.Pixels.AsSpan().SequenceEqual(second.Pixels);

    internal static int[] ResolveFailureRateRoi(UraScenarioPack pack, GrayImage frame,
        string trainingType, int[] roi, TemplateMatchResult? selectedLogo)
    {
        var referenceWidth = pack.ScreenProfile.ReferenceWidth;
        // The failure bubble follows the raised button. Its width and vertical
        // numeric region are shared across all five types; no selected templates.
        var x = selectedLogo is { Found: true } match
            ? (int)Math.Round(match.CenterX * referenceWidth / (double)frame.Width - roi[2] / 2d)
            : roi[0] + pack.ExecutionDefinition.GetTask($"training_selection_training_{trainingType}").Roi![0]
                - pack.ExecutionDefinition.GetTask("training_selection_training_speed").Roi![0];
        return [Math.Clamp(x, 0, referenceWidth - roi[2]), roi[1], roi[2], roi[3]];
    }

    private static GrayImage? PrepareFieldImage(GrayImage frame, int[] roi,
        int referenceWidth, int referenceHeight, bool gain, GainPixelPolicy policy)
    {
        var crop = CareerNumericOcrReader.Crop(frame, roi, referenceWidth, referenceHeight);
        return crop is null ? null : UraTrainingNumberImagePreprocessor.Prepare(crop, gain,
            (r, g, b) => IsWarmGainPixel(r, g, b, policy));
    }

    private static bool HasGainStripEvidence(
        GrayImage frame,
        int[]? roi,
        int referenceWidth,
        int referenceHeight,
        GainPixelPolicy policy)
    {
        if (frame.RgbaPixels is not { } rgba || roi is not { Length: >= 4 })
            return false;
        var scaled = ScaleRoi(roi, referenceWidth, referenceHeight, frame.Width, frame.Height);
        var warm = 0;
        var sampled = 0;
        for (var y = scaled[1]; y < scaled[1] + scaled[3]; y += 2)
        {
            for (var x = scaled[0]; x < scaled[0] + scaled[2]; x += 2)
            {
                var index = checked((y * frame.Width + x) * 4);
                if (index + 2 >= rgba.Length)
                    continue;
                var red = rgba[index];
                var green = rgba[index + 1];
                var blue = rgba[index + 2];
                sampled++;
                if (IsWarmGainPixel(red, green, blue, policy))
                    warm++;
            }
        }

        return sampled > 0
            && warm >= policy.MinimumWarmPixels
            && warm / (double)sampled >= policy.StripRatio;
    }

    private static bool IsVisiblyBlank(
        GrayImage frame,
        int[] roi,
        int referenceWidth,
        int referenceHeight,
        GainPixelPolicy policy)
    {
        if (frame.RgbaPixels is not { } rgba)
            return false;
        var scaled = ScaleRoi(roi, referenceWidth, referenceHeight, frame.Width, frame.Height);
        var warm = 0;
        var sampled = 0;
        for (var y = scaled[1]; y < scaled[1] + scaled[3]; y += 2)
        {
            for (var x = scaled[0]; x < scaled[0] + scaled[2]; x += 2)
            {
                var index = checked((y * frame.Width + x) * 4);
                if (index + 2 >= rgba.Length)
                    continue;
                var red = rgba[index];
                var green = rgba[index + 1];
                var blue = rgba[index + 2];
                sampled++;
                if (IsWarmGainPixel(red, green, blue, policy))
                    warm++;
            }
        }

        return sampled > 0 && warm / (double)sampled < policy.BlankRatio;
    }

    private static bool IsWarmGainPixel(
        byte red,
        byte green,
        byte blue,
        GainPixelPolicy policy) =>
        red >= policy.WarmRedMin
        && green >= policy.WarmGreenMin
        && green <= policy.WarmGreenMax
        && blue <= policy.WarmBlueMax
        && red >= green + policy.WarmRedDelta;

    private static int[] ScaleRoi(
        int[] roi,
        int referenceWidth,
        int referenceHeight,
        int width,
        int height) =>
    [
        Math.Clamp((int)Math.Round(roi[0] * width / (double)referenceWidth), 0, width),
        Math.Clamp((int)Math.Round(roi[1] * height / (double)referenceHeight), 0, height),
        Math.Clamp((int)Math.Round(roi[2] * width / (double)referenceWidth), 0, width),
        Math.Clamp((int)Math.Round(roi[3] * height / (double)referenceHeight), 0, height),
    ];
}

/// <summary>
/// Scans the five picker cards by clicking only the first, reversible logo
/// tap. The existing two-stage training task remains the sole confirmation
/// path, so preview never consumes a turn.
/// </summary>
internal sealed class UraSmartTrainingSelectionFlow
{
    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly CareerFlowDispatcher _dispatcher;
    private readonly UraTrainingSelectionHeightDetector _heightDetector;
    private readonly UraSmartTrainingCandidateReader _candidateReader;
    private readonly UraSmartTrainingDiagnostics _diagnostics = new();
    private readonly Func<IHachimiTaskLogSink?>? _taskLogSink;

    public UraSmartTrainingSelectionFlow(
        IVisualPipelineRuntime visualRuntime,
        CareerFlowDispatcher dispatcher,
        UraTrainingSelectionHeightDetector heightDetector,
        Func<IHachimiTaskLogSink?>? taskLogSink = null)
    {
        _visualRuntime = visualRuntime ?? throw new ArgumentNullException(nameof(visualRuntime));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _heightDetector = heightDetector ?? throw new ArgumentNullException(nameof(heightDetector));
        _candidateReader = new UraSmartTrainingCandidateReader(visualRuntime);
        _taskLogSink = taskLogSink;
    }

    public async Task<CareerTrainingResult?> HandleAsync(
        CareerFlowContext context,
        UraSmartTrainingStrategy strategy)
    {
        if (context.State.TrainingClickIssuedType is not null)
        {
            context.LogSink?.Add(
                "URA Strategy",
                "Smart preview is waiting for the confirmed training result; no second confirmation will be sent.");
            return null;
        }
        if (context.State.TrainingTurnCommitPending
            || context.State.TrainingTurnCommitType is not null)
        {
            return CareerRuntimeResults.Failure(
                "A previous smart training confirmation has an unknown result; picker scanning is paused safely.",
                "training_selection");
        }

        var started = Stopwatch.GetTimestamp();
        var sampler = new UraSmartTrainingPreviewSampler(_visualRuntime, _heightDetector.DetectFrameAsync);
        var current = await sampler.CaptureAsync(
                context.Connection, context.Pack, context.CancellationToken)
            .ConfigureAwait(false);
        var initial = current.Selection;
        if (initial.ScreenChanged)
            return null;
        if (!initial.Succeeded)
            return CareerRuntimeResults.Failure(
                $"Smart training preview could not identify the five training cards: {initial.Error}",
                "training_selection");

        var candidates = new List<UraTrainingCandidate>(UraTrainingTypeCatalog.SupportedTypes.Count);
        foreach (var trainingType in UraTrainingTypeCatalog.SupportedTypes)
        {
            var candidateStarted = Stopwatch.GetTimestamp();
            var capturesBefore = sampler.CaptureCount;
            current = await sampler.SelectAsync(context.Connection, context.Pack, trainingType,
                    current, context.CancellationToken)
                .ConfigureAwait(false);
            var settled = current.Selection;
            if (settled.ScreenChanged)
                return null;
            if (!settled.Succeeded
                || !settled.RaisedType!.Equals(trainingType, StringComparison.OrdinalIgnoreCase))
            {
                context.LogSink?.Add(
                    "URA Strategy",
                    $"Preview selection for '{trainingType}' was not verified; candidate remains unknown.",
                    LogEntryKind.Failure);
                candidates.Add(new UraTrainingCandidate(
                    trainingType, null, null, null, null, null, null, null));
                continue;
            }

            var firstFrame = current.Frame;
            if (firstFrame is null)
            {
                candidates.Add(new UraTrainingCandidate(
                    trainingType, null, null, null, null, null, null, null));
                continue;
            }

            var fallbackBudget = new CareerNumericOcrBudget();
            var first = await ReadCandidateFrameAsync(
                    context, current, trainingType, fallbackBudget, false)
                .ConfigureAwait(false);
            var firstEvidence = _candidateReader.LastReadings;
            var windowsCalls = _candidateReader.LastWindowsOcrCalls;
            var fallbackCalls = _candidateReader.LastFallbackCalls;
            await _visualRuntime.DelayAsync(120, context.CancellationToken)
                .ConfigureAwait(false);
            current = await sampler.CaptureAsync(
                    context.Connection, context.Pack, context.CancellationToken)
                .ConfigureAwait(false);
            var secondFrame = current.Frame;
            if (current.Selection.ScreenChanged)
                return null;
            var second = secondFrame is null
                ? new UraTrainingCandidate(trainingType, null, null, null, null, null, null, null)
                : await ReadCandidateFrameAsync(
                    context, current, trainingType, fallbackBudget, true)
                    .ConfigureAwait(false);
            var secondVerified = secondFrame is not null && current.Selection.Succeeded
                && string.Equals(current.Selection.RaisedType, trainingType, StringComparison.OrdinalIgnoreCase);
            var secondEvidence = secondVerified ? _candidateReader.LastReadings : null;
            if (secondVerified)
            {
                windowsCalls += _candidateReader.LastWindowsOcrCalls;
                fallbackCalls += _candidateReader.LastFallbackCalls;
            }
            var merged = UraSmartTrainingCandidateReader.MergeStable(first, second);
            candidates.Add(merged);
            if (!merged.HasReliableCoreValues || !merged.HasReliableFailureRate)
                await _diagnostics.SaveAsync(context, strategy, trainingType, firstFrame, secondFrame,
                    first, second, merged, firstEvidence,
                    secondEvidence).ConfigureAwait(false);
            context.LogSink?.Add("URA Strategy",
                $"Smart preview timing {trainingType}: elapsedMs={Stopwatch.GetElapsedTime(candidateStarted).TotalMilliseconds:0}, "
                + $"captures={sampler.CaptureCount - capturesBefore}, windowsOcr={windowsCalls}, "
                + $"fallbackOcr={fallbackCalls}, stableReuse={(secondVerified ? _candidateReader.LastReusedFields : 0)}.");
        }

        context.LogSink?.Add("URA Strategy",
            $"Smart scan timing: elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}, "
            + $"captures={sampler.CaptureCount}, previewTaps={sampler.PreviewTapCount}.");

        var decision = strategy.SelectCandidate(
            context.Scenario, context.State, candidates);
        UraSmartTrainingScanLog.Write(_taskLogSink?.Invoke(), context.State, decision);
        foreach (var item in decision.ScoredCandidates)
        {
            var suffix = item.Score.ExclusionReason is { } reason
                ? $"excluded={reason}"
                : $"distance={item.Score.Distance}, base={item.Score.BaseScore.ToString("0.00", CultureInfo.InvariantCulture)}, "
                    + $"bond={item.Score.BondBonus.ToString("0.00", CultureInfo.InvariantCulture)}, "
                    + $"hint={item.Score.HintBonus.ToString("0.00", CultureInfo.InvariantCulture)}, "
                    + $"witLowEnergy={item.Score.LowEnergyWitBonus.ToString("0.00", CultureInfo.InvariantCulture)}, "
                    + $"score={item.Score.TotalScore.ToString("0.00", CultureInfo.InvariantCulture)}";
            context.LogSink?.Add(
                "URA Strategy",
                $"Candidate {item.Candidate.TrainingType}: "
                + $"speed={item.Candidate.SpeedGain?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, "
                + $"stamina={item.Candidate.StaminaGain?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, "
                + $"power={item.Candidate.PowerGain?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, "
                + $"guts={item.Candidate.GutsGain?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, "
                + $"wit={item.Candidate.WitGain?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, "
                + $"skill={item.Candidate.SkillPointGain?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, "
                + $"failure={item.Candidate.FailureRatePercent?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}%, {suffix}.");
        }

        if (decision.Candidate is null)
        {
            context.LogSink?.Add(
                "URA Strategy",
                "All smart training candidates were unknown or unsafe; rest fallback is armed, and no training confirmation was sent.",
                LogEntryKind.Failure);
            var back = await _dispatcher.RunSmartTrainingFallbackBackAsync(context)
                .ConfigureAwait(false);
            return back;
        }

        var chosen = decision.Candidate.TrainingType.Trim().ToLowerInvariant();
        current = await sampler.CaptureAsync(context.Connection, context.Pack, context.CancellationToken)
            .ConfigureAwait(false);
        var finalSelection = current.Selection;
        if (finalSelection.ScreenChanged)
            return null;
        if (!finalSelection.Succeeded)
        {
            return CareerRuntimeResults.Failure(
                "The smart training picker could not identify its current selection before confirmation.",
                "training_selection");
        }
        if (!finalSelection.RaisedType!.Equals(chosen, StringComparison.OrdinalIgnoreCase))
        {
            current = await sampler.SelectAsync(context.Connection, context.Pack, chosen,
                    current, context.CancellationToken)
                .ConfigureAwait(false);
            finalSelection = current.Selection;
        }
        if (!finalSelection.Succeeded
            || !finalSelection.RaisedType!.Equals(chosen, StringComparison.OrdinalIgnoreCase))
        {
            context.LogSink?.Add(
                "URA Strategy",
                $"Winning training '{chosen}' was not raised at confirmation time; no turn was consumed.",
                LogEntryKind.Failure);
            return CareerRuntimeResults.Failure(
                "The smart training winner was not visibly selected before confirmation.",
                "training_selection");
        }
        context.State.PendingTrainingType = chosen;
        context.State.LastAction = UraPlannedAction.Training;
        CareerTurnFlow.ArmPendingGoalProbe(context.State);
        context.State.SmartTrainingPreviewReady = false;
        var result = await _dispatcher.RunConfirmedSmartTrainingSelectionAsync(
                context, chosen)
            .ConfigureAwait(false);
        context.LogSink?.Add("URA Strategy",
            $"Smart decision timing: elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}, "
            + $"smartCaptures={sampler.CaptureCount}, previewTaps={sampler.PreviewTapCount}, winner={chosen}.");
        return result;
    }

    private async Task<UraTrainingCandidate> ReadCandidateFrameAsync(
        CareerFlowContext context, UraSmartTrainingPreviewFrame preview, string trainingType,
        CareerNumericOcrBudget budget, bool reusePreviousRead)
    {
        var selection = preview.Selection;
        if (preview.Frame is not { } frame || !selection.Succeeded
            || !string.Equals(selection.RaisedType, trainingType,
                StringComparison.OrdinalIgnoreCase))
            return new(trainingType, null, null, null, null, null, null, null);
        var selected = selection.Matches.Single(item => item.TrainingType == selection.RaisedType).Match;
        return await _candidateReader.ReadAsync(context.Pack, frame, trainingType,
            context.CancellationToken, sharedFallbackBudget: budget, selectedLogo: selected,
            optimize: true, reusePreviousRead: reusePreviousRead)
            .ConfigureAwait(false);
    }
}
