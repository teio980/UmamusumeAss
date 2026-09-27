namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Conservative Race Day threshold. OtherTiers is a list of related tiers,
/// not a chain: at most one known lower tier is added to the target cost.
/// </summary>
internal static class CareerSkillCostResolver
{
    internal static int Estimate(
        IndependentTrainingSkill skill,
        IReadOnlyDictionary<string, IndependentTrainingSkill> byName)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(byName);
        var cost = Math.Max(0, skill.NeedSkillPoint);
        if (cost == 0)
            return 0;

        var relatedNames = skill.OtherTiers.Split('/',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        IndependentTrainingSkill? prerequisite = null;
        if (skill.Rarity == 2)
        {
            // The catalog lists the preferred lower tier first. Do not add
            // every alternative in that list.
            prerequisite = relatedNames
                .Select(name => byName.GetValueOrDefault(name))
                .FirstOrDefault(other => other is { Rarity: 1, NeedSkillPoint: > 0 });
        }
        else if (skill.Rarity == 1
            && skill.SkillName.EndsWith('◎'))
        {
            // An ◎ upgrade has its corresponding ○ skill as its lower tier.
            var lowerName = skill.SkillName[..^1] + '○';
            if (relatedNames.Contains(lowerName, StringComparer.OrdinalIgnoreCase))
                prerequisite = byName.GetValueOrDefault(lowerName);
        }

        return checked(cost + Math.Max(0, prerequisite?.NeedSkillPoint ?? 0));
    }
}
