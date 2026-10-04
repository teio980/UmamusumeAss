using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Applies configured Normal Career skill goals before the race workflow.
/// Skill-page recognition and interaction live in CareerSkillVisualAdapter.
/// </summary>
internal sealed class CareerSkillLearningFlow
{
    private readonly CareerSkillVisualAdapter _visual;
    private readonly ICareerFlowActionRunner _actions;

    internal CareerSkillLearningFlow(IVisualPipelineRuntime visual, ICareerFlowActionRunner actions)
    {
        _visual = new CareerSkillVisualAdapter(
            visual ?? throw new ArgumentNullException(nameof(visual)));
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

            var points = await _visual.ReadPointsAsync(context).ConfigureAwait(false);
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

            if (!await _visual.WaitForSkillPageAsync(context).ConfigureAwait(false))
                return NavigationFailure("Skills page did not appear after opening it.");

            var scan = await _visual.ScanForSkillAsync(context, skill.SkillName)
                .ConfigureAwait(false);
            if (scan.Kind == CareerSkillScanKind.Obtained)
            {
                await RememberAsync(context, id).ConfigureAwait(false);
                Log(context, $"{skill.SkillName} is already obtained.");
            }
            else if (scan.Kind == CareerSkillScanKind.Purchasable && scan.Plus is not null)
            {
                var price = await _visual.ReadPriceAsync(context, scan.Plus).ConfigureAwait(false);
                if (price is null || price <= 0)
                {
                    if (!await _visual.ReturnToRaceDayAsync(context).ConfigureAwait(false))
                        return NavigationFailure("Could not return from Skills to Race Day.");
                    return CareerRuntimeResults.Failure(
                        $"Could not read the displayed price for {skill.SkillName}; training stopped before the race.",
                        "race_day");
                }

                if (price > points)
                {
                    Log(context,
                        $"Saving {points} Skill Pts for {skill.SkillName} (displayed price {price}); proceeding to race.");
                    if (!await _visual.ReturnToRaceDayAsync(context).ConfigureAwait(false))
                        return NavigationFailure("Could not return from Skills to Race Day.");
                    break;
                }

                if (!await _visual.PurchaseAsync(context, skill.SkillName, scan.Plus)
                        .ConfigureAwait(false))
                {
                    if (!await _visual.ReturnToRaceDayAsync(context).ConfigureAwait(false))
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
                if (!await _visual.ReturnToRaceDayAsync(context).ConfigureAwait(false))
                    return NavigationFailure("Could not return from Skills to Race Day.");
                Log(context,
                    $"Could not uniquely find {skill.SkillName} on the Skills list; returned to Race Day and skipped it.");
                continue;
            }

            if (!await _visual.ReturnToRaceDayAsync(context).ConfigureAwait(false))
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

    private static void Log(CareerFlowContext context, string message) =>
        context.LogSink?.Add("Career Training", message, LogEntryKind.Info);

    private static CareerTrainingResult NavigationFailure(string message) =>
        CareerRuntimeResults.Failure(message, "career_skills");
}
