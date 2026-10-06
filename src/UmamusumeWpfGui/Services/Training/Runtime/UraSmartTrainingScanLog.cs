using System.Globalization;
using System.Text;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>One Hachimi log card for a completed smart training scan; no additional OCR.</summary>
internal static class UraSmartTrainingScanLog
{
    public static void Write(IHachimiTaskLogSink? sink, UraCareerSessionState state,
        UraSmartTrainingDecision decision)
    {
        if (sink is null)
            return;
        var message = new StringBuilder();
        message.Append("日期：").Append(state.TurnPositionLabel ?? "未知")
            .Append(" ｜ 体力：").Append(Percent(state.Energy.Value)).AppendLine();
        foreach (var type in UraTrainingTypeCatalog.SupportedTypes)
        {
            var item = decision.ScoredCandidates.FirstOrDefault(item =>
                string.Equals(item.Candidate.TrainingType, type, StringComparison.OrdinalIgnoreCase));
            var candidate = item.Candidate;
            message.AppendLine();
            message.Append(Name(type)).Append("训练：")
                .Append("速度 ").Append(Gain(candidate?.SpeedGain))
                .Append("，耐力 ").Append(Gain(candidate?.StaminaGain))
                .Append("，力量 ").Append(Gain(candidate?.PowerGain))
                .Append("，根性 ").Append(Gain(candidate?.GutsGain))
                .Append("，智力 ").Append(Gain(candidate?.WitGain))
                .Append("，技能点 ").Append(Gain(candidate?.SkillPointGain)).AppendLine();
            message.Append("失败率：").Append(Percent(candidate?.FailureRatePercent))
                .Append(" ｜ 评分：")
                .Append(item.Score is { ExclusionReason: null } score
                    ? score.TotalScore.ToString("0.00", CultureInfo.InvariantCulture) : "未参与")
                .Append(" ｜ ");
            if (candidate is null)
                message.Append("排除：未扫描到可靠数据");
            else if (item.Score.ExclusionReason is { } reason)
                message.Append("排除：").Append(Exclusion(reason, candidate));
            else if (string.Equals(type, decision.Candidate?.TrainingType, StringComparison.OrdinalIgnoreCase))
                message.Append("本轮胜出");
            else
                message.Append("安全候选");
            message.AppendLine();
        }
        message.AppendLine();
        message.Append(decision.Candidate is { } chosen
            ? $"本轮选择：{Name(chosen.TrainingType)}训练（待确认）"
            : "本轮选择：无可靠安全候选，返回主界面休息");
        // The existing Hachimi entry template supplies the border. One Add preserves
        // all five rows inside a single card, including unknown and excluded candidates.
        sink.Add("智能训练扫描", message.ToString(), HachimiTaskLogEventKind.Detection);
    }

    private static string Gain(int? value) => value is { } number
        ? "+" + number.ToString(CultureInfo.InvariantCulture) : "未知";

    private static string Percent(int? value) => value is { } number
        ? number.ToString(CultureInfo.InvariantCulture) + "%" : "未知";

    private static string Name(string type) => type.Trim().ToLowerInvariant() switch
    {
        "speed" => "速度",
        "stamina" => "耐力",
        "power" => "力量",
        "guts" => "根性",
        "wit" => "智力",
        _ => type,
    };

    private static string Exclusion(string reason, UraTrainingCandidate candidate) => reason switch
    {
        "core gains are unknown or below confidence threshold" => "增益未知、不稳定或可信度不足",
        "failure rate is unknown or below confidence threshold" => "失败率未知、不稳定或可信度不足",
        "expected gains are zero" => "预计收益为零",
        _ when candidate.FailureRatePercent > UraSmartTrainingScorer.MaximumFailureRatePercent =>
            $"失败率超过 {UraSmartTrainingScorer.MaximumFailureRatePercent}%",
        _ => reason,
    };
}
