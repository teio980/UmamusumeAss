using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Runs entirely between the recognized Race Day page and race.open_list.
/// The Career screen observer must never see the intermediate Learn dialogs.
/// </summary>
internal sealed class CareerSkillLearningFlow
{
    private const int Width = 900;
    private const int Height = 1600;
    private const int MaxScrolls = 60;
    private const string TemplateRoot = "templates/";
    private readonly IVisualPipelineRuntime _visual;
    private readonly CareerScreenObserver _screenObserver;
    private readonly ICareerFlowActionRunner _actions;

    internal CareerSkillLearningFlow(IVisualPipelineRuntime visual, ICareerFlowActionRunner actions)
    {
        _visual = visual ?? throw new ArgumentNullException(nameof(visual));
        _screenObserver = new CareerScreenObserver(visual);
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    internal async Task<CareerTrainingResult?> RunAsync(CareerFlowContext context)
    {
        var ids = context.NormalSkillIds;
        if (ids is null || ids.Count == 0)
            return null;

        var catalog = IndependentTrainingCatalog.Load();
        var skillsById = catalog.Skills.ToDictionary(skill => skill.SkillId);
        var skillsByName = catalog.Skills
            .GroupBy(skill => skill.SkillName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var id in ids.Distinct())
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (context.State.NormalLearnedSkillIds.Contains(id))
                continue;
            if (!skillsById.TryGetValue(id, out var skill) || !skill.IsSelectable)
            {
                return CareerRuntimeResults.Failure(
                    $"Configured Normal Career skill {id} is unavailable; training stopped before the race.",
                    "race_day");
            }

            var points = await ReadPointsAsync(context).ConfigureAwait(false);
            if (points is null)
            {
                return CareerRuntimeResults.Failure(
                    $"Could not read Race Day Skill Pts for {skill.SkillName}; training stopped before the race.",
                    "race_day");
            }

            var threshold = CareerSkillCostResolver.Estimate(skill, skillsByName);
            if (points < threshold)
            {
                Log(context,
                    $"Saving {points} Skill Pts for {skill.SkillName} (estimated {threshold}); proceeding to race.");
                break;
            }

            var openResult = await _actions.RunAsync(context, "race_day", "skills.open")
                .ConfigureAwait(false);
            if (openResult is not null)
            {
                Log(context, $"Could not open Skills for {skill.SkillName}: {openResult.Message}");
                return openResult;
            }

            if (!await WaitForAsync(context, "career_skill_back.png", [0, 1480, 185, 110])
                    .ConfigureAwait(false))
            {
                return NavigationFailure("Skills page did not appear after opening it.");
            }

            var scan = await ScanForSkillAsync(context, skill.SkillName).ConfigureAwait(false);
            if (scan.Kind == SkillScanKind.Obtained)
            {
                await RememberAsync(context, id).ConfigureAwait(false);
                Log(context, $"{skill.SkillName} is already obtained.");
            }
            else if (scan.Kind == SkillScanKind.Purchasable && scan.Plus is not null)
            {
                var price = await ReadPriceAsync(context, scan.Plus).ConfigureAwait(false);
                if (price is null || price <= 0)
                {
                    if (!await ReturnToRaceDayAsync(context).ConfigureAwait(false))
                        return NavigationFailure("Could not return from Skills to Race Day.");
                    return CareerRuntimeResults.Failure(
                        $"Could not read the displayed price for {skill.SkillName}; training stopped before the race.",
                        "race_day");
                }

                if (price > points)
                {
                    Log(context,
                        $"Saving {points} Skill Pts for {skill.SkillName} (displayed price {price}); proceeding to race.");
                    if (!await ReturnToRaceDayAsync(context).ConfigureAwait(false))
                        return NavigationFailure("Could not return from Skills to Race Day.");
                    break;
                }

                if (!await PurchaseAsync(context, skill, scan.Plus)
                        .ConfigureAwait(false))
                {
                    if (!await ReturnToRaceDayAsync(context).ConfigureAwait(false))
                        return NavigationFailure("Could not return from Skills to Race Day after a failed purchase.");
                    return CareerRuntimeResults.Failure(
                        $"Could not confirm purchase of {skill.SkillName}; training stopped before the race.",
                        "race_day");
                }

                await RememberAsync(context, id).ConfigureAwait(false);
                Log(context, $"Learned {skill.SkillName} for {price.Value} Skill Pts.");
            }
            else
            {
                if (!await ReturnToRaceDayAsync(context).ConfigureAwait(false))
                    return NavigationFailure("Could not return from Skills to Race Day.");
                return CareerRuntimeResults.Failure(
                    $"Could not uniquely find {skill.SkillName} on the Skills list; training stopped before the race.",
                    "race_day");
            }

            if (!await ReturnToRaceDayAsync(context).ConfigureAwait(false))
                return NavigationFailure("Could not return from Skills to Race Day after learning a skill.");
        }

        return null;
    }

    private static async Task RememberAsync(CareerFlowContext context, int skillId)
    {
        if (!context.State.NormalLearnedSkillIds.Contains(skillId))
            context.State.NormalLearnedSkillIds.Add(skillId);
        if (context.RememberNormalSkillAsync is not null)
            await context.RememberNormalSkillAsync(skillId).ConfigureAwait(false);
    }

    private async Task<bool> PurchaseAsync(
        CareerFlowContext context,
        IndependentTrainingSkill skill,
        TemplateMatchResult plus)
    {
        var connection = context.Connection;
        var token = context.CancellationToken;
        await _visual.TapMatchAsync(connection, plus, "career_skill.plus", token)
            .ConfigureAwait(false);
        await _visual.DelayAsync(350, token).ConfigureAwait(false);

        var selected = await ScanForSkillAsync(context, skill.SkillName)
            .ConfigureAwait(false);
        if (selected.Kind != SkillScanKind.Obtained)
            return false;

        if (!await TapTemplateAsync(context, "career_skill_confirm.png", [345, 1310, 210, 80])
                .ConfigureAwait(false))
            return false;
        if (!await WaitForAsync(context, "career_skill_confirmation_title.png", [290, 25, 320, 105])
                .ConfigureAwait(false))
            return false;

        // The dialog lists precisely the skill about to be learned. Never
        // approve a dialog whose listed name does not match the target.
        var dialogText = await _visual.DetectTextAsync(connection, [185, 150, 660, 130],
                Width, Height, "en-US", "career_skill.confirmation_name", token)
            .ConfigureAwait(false);
        if (dialogText is null || !ContainsName(dialogText, skill.SkillName))
            return false;

        if (!await TapTemplateAsync(context, "career_skill_confirmation_learn.png", [455, 1410, 385, 130])
                .ConfigureAwait(false))
            return false;
        if (!await WaitForAsync(context, "career_skill_learned_title.png", [290, 455, 320, 110])
                .ConfigureAwait(false))
            return false;
        if (!await TapTemplateAsync(context, "career_skill_learned_close.png", [250, 975, 390, 130])
                .ConfigureAwait(false))
            return false;
        if (!await WaitForAsync(context, "career_skill_back.png", [0, 1480, 185, 110])
                .ConfigureAwait(false))
            return false;
        return true;
    }

    private async Task<SkillScanResult> ScanForSkillAsync(CareerFlowContext context, string name)
    {
        var connection = context.Connection;
        var token = context.CancellationToken;
        var plusTemplate = await _visual.LoadTemplateAsync(
                TemplateRoot + "career_skill_plus.png",
                context.Pack.ExecutionDefinition.BaseDirectory,
                token)
            .ConfigureAwait(false);
        if (plusTemplate is null)
            return new SkillScanResult(SkillScanKind.Unknown);

        ulong? lastPage = null;
        var unchangedPages = 0;
        for (var page = 0; page <= MaxScrolls; page++)
        {
            token.ThrowIfCancellationRequested();
            var frame = await _visual.CaptureGrayAsync(connection, token).ConfigureAwait(false);
            if (frame is null)
                return new SkillScanResult(SkillScanKind.Unknown);
            var text = await _visual.DetectTextAsync(frame, [155, 590, 475, 690],
                    Width, Height, "en-US", "career_skill.list", token)
                .ConfigureAwait(false);
            if (text is null)
                return new SkillScanResult(SkillScanKind.Unknown);

            var matches = text.Detections.Where(detection =>
                    detection.Bounds.X is >= 140 and <= 650
                    && detection.Bounds.Y is >= 585 and <= 1230
                    && NameSimilarity(name, detection.Text) >= 0.84)
                .ToArray();
            if (matches.Length > 1)
                return new SkillScanResult(SkillScanKind.Unknown);
            if (matches.Length == 1)
            {
                var title = matches[0];
                var rowText = await _visual.DetectTextAsync(frame,
                        [625, Math.Max(585, title.Bounds.Y - 15), 220, 155],
                        Width, Height, "en-US", "career_skill.row_status", token)
                    .ConfigureAwait(false);
                if (rowText?.Detections.Any(d =>
                        d.Text.Contains("Obtained", StringComparison.OrdinalIgnoreCase)) == true)
                    return new SkillScanResult(SkillScanKind.Obtained);

                var roiTop = Math.Clamp(title.Bounds.Y + 20, 595, 1190);
                var plus = TemplateMatcher.FindColor(frame, plusTemplate,
                    [775, roiTop, 85, Math.Min(155, 1280 - roiTop)],
                    0.82, Width, Height);
                if (plus.Found)
                    return new SkillScanResult(SkillScanKind.Purchasable, plus);
                // A title cut off by the viewport may acquire a visible +
                // after the next swipe. Do not tap a guessed row position.
            }

            var signature = ListSignature(frame);
            unchangedPages = signature == lastPage ? unchangedPages + 1 : 0;
            if (unchangedPages >= 2 || page == MaxScrolls)
                return new SkillScanResult(SkillScanKind.NotFound);
            lastPage = signature;
            await _visual.SwipeAsync(connection, [470, 1170, 470, 690, 450],
                Width, Height, "career_skill.scroll", token).ConfigureAwait(false);
            await _visual.DelayAsync(500, token).ConfigureAwait(false);
        }

        return new SkillScanResult(SkillScanKind.NotFound);
    }

    private async Task<int?> ReadPriceAsync(CareerFlowContext context, TemplateMatchResult plus)
    {
        return await ReadNumberAsync(context,
            [685, Math.Max(585, plus.CenterY - 40), 95, 80],
            "career_skill.price").ConfigureAwait(false);
    }

    private Task<int?> ReadPointsAsync(CareerFlowContext context) =>
        ReadNumberAsync(context, [750, 1140, 115, 75],
            "career_skill.points", [760, 1145, 95, 65]);

    private async Task<int?> ReadNumberAsync(CareerFlowContext context,
        int[] numberRoi, string taskName, int[]? focusedRoi = null)
    {
        var result = await _visual.DetectTextAsync(context.Connection, null,
                Width, Height, "en-US", taskName, context.CancellationToken)
            .ConfigureAwait(false);
        var number = ParseNumberInRegion(result, numberRoi);
        if (number is not null)
            return number;

        // The full-screen recognizer can merge the entire stat row into one
        // line. A crop isolates the value without relying on line grouping.
        var cropRoi = focusedRoi ?? numberRoi;
        result = await _visual.DetectTextAsync(context.Connection, cropRoi,
                Width, Height, "en-US", taskName + ".focused",
                context.CancellationToken)
            .ConfigureAwait(false);
        return ParseNumberInRegion(result, cropRoi);
    }

    internal static int? ParseNumberInRegion(
        ScreenTextRecognitionResult? result, int[] roi)
    {
        if (result is null)
            return null;
        var inRegion = result.Detections.Where(detection =>
            detection.Bounds.CenterX >= roi[0]
            && detection.Bounds.CenterX <= roi[0] + roi[2]
            && detection.Bounds.CenterY >= roi[1]
            && detection.Bounds.CenterY <= roi[1] + roi[3]).ToArray();
        if (inRegion.Length > 0)
            return ParseSingleNumber(result with { Detections = inRegion });

        // Windows OCR sometimes gives the whole stat strip one bounding box.
        // Its right edge still ends in the Skill Pts box, and the last token
        // is the point value. Require one unambiguous candidate.
        var mergedValues = result.Detections
            .Where(detection => detection.Bounds.X < roi[0]
                && detection.Bounds.X + detection.Bounds.Width >= roi[0]
                && detection.Bounds.X + detection.Bounds.Width <= roi[0] + roi[2]
                && detection.Bounds.CenterY >= roi[1]
                && detection.Bounds.CenterY <= roi[1] + roi[3])
            .Select(detection => Regex.Match(detection.Text, @"(?<!\d)\d{1,4}\s*$"))
            .Where(match => match.Success)
            .Select(match => int.TryParse(match.Value.Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var value) ? value : -1)
            .Where(value => value >= 0)
            .Distinct()
            .ToArray();
        return mergedValues.Length == 1 ? mergedValues[0] : null;
    }

    internal static int? ParseSingleNumber(ScreenTextRecognitionResult? result)
    {
        if (result is null)
            return null;
        var numbers = result.Detections
            .Select(detection => detection.Text
                .Replace('O', '0').Replace('o', '0').Replace('l', '1').Replace('I', '1'))
            .SelectMany(text => Regex.Matches(text, @"\b\d{1,4}\b")
                .Select(match => match.Value))
            .Select(text => int.TryParse(text, NumberStyles.None,
                CultureInfo.InvariantCulture, out var parsed) ? parsed : -1)
            .Where(value => value >= 0)
            .Distinct()
            .ToArray();
        return numbers.Length == 1 ? numbers[0] : null;
    }

    private async Task<bool> ReturnToRaceDayAsync(CareerFlowContext context)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (await TryTapTemplateAsync(context, "career_skill_learned_title.png",
                    [290, 455, 320, 110], "career_skill_learned_close.png",
                    [250, 975, 390, 130]).ConfigureAwait(false))
                continue;
            if (await TryTapTemplateAsync(context, "career_skill_confirmation_title.png",
                    [290, 25, 320, 105], "career_skill_confirmation_cancel.png",
                    [60, 1410, 385, 130]).ConfigureAwait(false))
                continue;
            if (await TryTapTemplateAsync(context, "career_skill_exit_title.png",
                    [245, 730, 420, 100], "career_skill_exit_ok.png",
                    [455, 970, 385, 145]).ConfigureAwait(false))
                continue;
            if (await TapTemplateAsync(context, "career_skill_back.png",
                    [0, 1480, 185, 110], timeoutMilliseconds: 600).ConfigureAwait(false))
            {
                Log(context, "Clicked Skills Back; waiting for Race Day.");
                continue;
            }
            if (await IsRaceDayAsync(context).ConfigureAwait(false))
            {
                Log(context, "Returned from Skills to Race Day.");
                return true;
            }
            await _visual.DelayAsync(300, context.CancellationToken).ConfigureAwait(false);
        }
        return await IsRaceDayAsync(context).ConfigureAwait(false);
    }

    private async Task<bool> TryTapTemplateAsync(
        CareerFlowContext context,
        string marker, int[] markerRoi,
        string button, int[] buttonRoi)
    {
        var present = await _visual.WaitForColorMatchAsync(
                context.Connection, TemplateRoot + marker, markerRoi,
                0.8, Width, Height, 500, 200,
                "career_skill." + marker,
                context.Pack.ExecutionDefinition.BaseDirectory,
                context.CancellationToken)
            .ConfigureAwait(false);
        return present?.Found == true
            && await TapTemplateAsync(context, button, buttonRoi).ConfigureAwait(false);
    }

    private async Task<bool> IsRaceDayAsync(CareerFlowContext context)
    {
        var observation = await _screenObserver.ObserveAsync(
                context.Connection, context.Pack, context.State,
                careerStartTransitionExpected: false, context.CancellationToken)
            .ConfigureAwait(false);
        return observation?.ScreenId == "race_day";
    }

    private async Task<bool> WaitForAsync(
        CareerFlowContext context, string template, int[] roi)
    {
        var result = await _visual.WaitForColorMatchAsync(
                context.Connection, TemplateRoot + template, roi,
                0.8, Width, Height, 5000, 250,
                "career_skill." + template,
                context.Pack.ExecutionDefinition.BaseDirectory,
                context.CancellationToken)
            .ConfigureAwait(false);
        return result?.Found == true;
    }

    private async Task<bool> TapTemplateAsync(
        CareerFlowContext context, string template, int[] roi,
        int timeoutMilliseconds = 5000)
    {
        var match = await _visual.WaitForColorMatchAsync(
                context.Connection, TemplateRoot + template, roi,
                0.8, Width, Height, timeoutMilliseconds, 250,
                "career_skill." + template,
                context.Pack.ExecutionDefinition.BaseDirectory,
                context.CancellationToken)
            .ConfigureAwait(false);
        if (match?.Found != true)
            return false;
        await _visual.TapMatchAsync(context.Connection, match,
            "career_skill." + template, context.CancellationToken).ConfigureAwait(false);
        await _visual.DelayAsync(350, context.CancellationToken).ConfigureAwait(false);
        return true;
    }

    internal static double NameSimilarity(string expected, string observed)
    {
        var expectedMarker = SkillMarker(expected, allowOcrAliases: false);
        var observedMarker = SkillMarker(observed,
            allowOcrAliases: expectedMarker != '\0');
        if (expectedMarker != observedMarker)
            return 0;
        var left = NormalizeName(expected, allowOcrAliases: false);
        var right = NormalizeName(observed,
            allowOcrAliases: expectedMarker != '\0');
        if (left.Length == 0 || right.Length == 0
            || right.Length > left.Length * 1.5 + 3)
            return 0;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1];
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var substitution = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + substitution);
            }
            previous = current;
        }
        return 1d - (double)previous[right.Length] / Math.Max(left.Length, right.Length);
    }

    private static string NormalizeName(string text, bool allowOcrAliases)
    {
        if (SkillMarker(text, allowOcrAliases) != '\0')
            text = text[..^1];
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static char SkillMarker(string text, bool allowOcrAliases)
    {
        if (string.IsNullOrWhiteSpace(text))
            return '\0';
        var trimmed = text.TrimEnd();
        var last = trimmed[^1];
        if (last is '○' or '◎' or '×')
            return last;
        if (!allowOcrAliases || trimmed.Length < 2 || !char.IsWhiteSpace(trimmed[^2]))
            return '\0';
        return last switch
        {
            'O' or 'o' or '0' => '○',
            'X' or 'x' => '×',
            _ => '\0',
        };
    }

    private static bool ContainsName(ScreenTextRecognitionResult result, string name) =>
        result.Detections.Any(detection => NameSimilarity(name, detection.Text) >= 0.84);

    private static ulong ListSignature(GrayImage frame)
    {
        var pixels = frame.Pixels;
        ulong hash = 14695981039346656037;
        for (var y = 610; y < Math.Min(1270, frame.Height); y += 19)
        {
            for (var x = 45; x < Math.Min(855, frame.Width); x += 23)
            {
                hash ^= pixels[y * frame.Width + x];
                hash *= 1099511628211;
            }
        }
        return hash;
    }

    private static void Log(CareerFlowContext context, string message) =>
        context.LogSink?.Add("Career Training", message, LogEntryKind.Info);

    private static CareerTrainingResult NavigationFailure(string message) =>
        CareerRuntimeResults.Failure(message, "career_skills");

    private enum SkillScanKind { Unknown, NotFound, Obtained, Purchasable }

    private sealed record SkillScanResult(
        SkillScanKind Kind, TemplateMatchResult? Plus = null);
}
