using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Training;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraSmartTrainingScanLogTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frontend_receives_one_complete_card_including_unknown_and_unsafe_candidates(bool allUnsafe)
    {
        var log = new HachimiTaskLogViewModel();
        log.BeginRun([("career", "Career Training")],
            "Pending", "Running", "Completed", "Failed", "Canceled", "Skipped", "Queue started");
        try
        {
            var state = new UraCareerSessionState
            {
                TurnPositionLabel = "Classic July, first half",
                Energy = UraObservedValueFactory.FromObservation(80, 1),
            };
            var failure = allUnsafe ? 28 : 0;
            UraTrainingCandidate[] candidates =
            [
                UraTrainingCandidate.Reliable("speed", 21, 0, 9, 0, 0, 6, failure),
                UraTrainingCandidate.Reliable("stamina", 0, 13, 0, 7, 0, 4, failure),
                new("power", null, null, null, null, null, null, null),
                UraTrainingCandidate.Reliable("guts", 12, 0, 9, 16, 0, 7, failure),
                UraTrainingCandidate.Reliable("wit", 4, 0, 0, 0, 12, 5, 6),
            ];
            var decision = UraSmartTrainingScorer.Choose(candidates, UraTrainingDistance.Mile, "classic", 80);
            UraSmartTrainingScanLog.Write(log.ForTask("career"), state, decision);

            var entry = Assert.Single(Assert.Single(log.Tasks).Entries);
            Assert.Equal(HachimiTaskLogEventKind.Detection, entry.Kind);
            Assert.Equal("智能训练扫描", entry.Step);
            Assert.Contains(state.TurnPositionLabel, entry.Message);
            Assert.Contains("体力：80%", entry.Message);
            Assert.Contains("速度训练：", entry.Message);
            Assert.Contains("耐力训练：", entry.Message);
            Assert.Contains("力量训练：", entry.Message);
            Assert.Contains("根性训练：", entry.Message);
            Assert.Contains("智力训练：", entry.Message);
            Assert.Contains("技能点", entry.Message);
            Assert.Contains("失败率：未知", entry.Message);
            Assert.Contains("排除：", entry.Message);
            Assert.Contains("失败率超过 5%", entry.Message);
            Assert.Contains(allUnsafe ? "返回主界面休息" : "速度训练（待确认）", entry.Message);
            // Logging must not turn a selection into an executed training or advance a turn.
            Assert.False(state.TrainingTurnCommitPending);
            Assert.Null(state.TrainingClickIssuedType);
            Assert.Equal(0, state.TurnIndex);
        }
        finally
        {
            log.EndRun();
        }
    }
}
