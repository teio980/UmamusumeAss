using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerSettlementEntryTests
{
    [Theory]
    [InlineData("rewards", false, true)]
    [InlineData("rewards_collected", false, true)]
    [InlineData("unknown", true, true)]
    [InlineData("career_main", false, false)]
    public async Task Collected_rewards_popup_is_recognized_during_settlement_and_on_resume(
        string previousScreen, bool resume, bool expected)
    {
        var root = FindWorkspaceRoot();
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var frame = Load(CareerTestResourceResolver.FindUraCapture(root, "rewards_collected.png"));
        if (!expected)
        {
            // Isolate the popup to check its phase gate independently of
            // unrelated turn templates against an out-of-phase capture.
            pack = pack with
            {
                ScreenProfile = new UraScreenProfile
                {
                    Screens = [pack.ScreenProfile.Find("rewards_collected")!],
                    VisualResources = pack.ScreenProfile.VisualResources,
                },
            };
        }
        var observer = new CareerScreenObserver(
            CareerFollowTrainerRecognitionTests.CapturedFrameRuntime.Create(frame));
        var connection = new LastVerifiedConnection(
            "adb", "serial", "android", "version", 900, 1600, 900, 1600,
            DateTimeOffset.UnixEpoch);
        var state = new UraCareerSessionState
        {
            CareerStarted = !resume,
            LastScreenId = previousScreen,
        };

        var observation = await observer.ObserveAsync(connection, pack, state,
            false, CancellationToken.None, careerOnly: resume);

        if (!expected)
        {
            Assert.Null(observation);
            return;
        }
        Assert.Equal("rewards_collected", observation?.ScreenId);
        Assert.Equal(CareerScreenKind.Settlement, observation?.Kind);
        var popup = pack.ScreenProfile.Find("rewards_collected")!;
        var task = pack.ExecutionDefinition.GetTask(popup.FindAction("rewards_collected.close")!.Task);
        var close = Load(CareerTestResourceResolver.ResolveUraVisualResource(pack, task.Template!));
        var match = TemplateMatcher.FindColor(frame, close, task.Roi,
            task.TemplateThreshold, 900, 1600);
        Assert.True(match.Found, $"Collected rewards Close score {match.Score:0.000}.");
        Assert.InRange(match.CenterX, 440, 460);
        Assert.InRange(match.CenterY, 1030, 1055);
        Assert.False(CareerScreenObserver.IsReturningHome(
            new UraCareerSessionState { CareerStarted = true, LastScreenId = "rewards_collected" }));
    }

    [Theory]
    [InlineData("ura_rewards_next.png")]
    [InlineData("event_reward_live.png")]
    [InlineData("event_reward_after_next.png")]
    [InlineData("career_story_unlocked_compact.png")]
    [InlineData("career_story_unlocked.png")]
    [InlineData("career_complete_close.png")]
    public async Task Collected_rewards_title_rejects_other_reward_pages(string capture)
    {
        var root = FindWorkspaceRoot();
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var popup = pack.ScreenProfile.Find("rewards_collected")!;
        var header = Load(CareerTestResourceResolver.ResolveUraScreenTemplate(
            pack, popup, popup.Recognition.Template!));
        var frame = Load(CareerTestResourceResolver.FindUraCapture(root, capture));

        Assert.Equal(0, header.RgbaPixels![3]);
        Assert.False(Match(frame, header, popup.Recognition).Found);
    }

    [Fact]
    public async Task Collected_rewards_requires_a_close_button()
    {
        var root = FindWorkspaceRoot();
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var popup = pack.ScreenProfile.Find("rewards_collected")!;
        pack = pack with
        {
            ScreenProfile = new UraScreenProfile
            {
                Screens = [popup],
                VisualResources = pack.ScreenProfile.VisualResources,
            },
        };
        var frame = Load(CareerTestResourceResolver.FindUraCapture(root, "rewards_collected.png"));
        // Leave the title intact but remove the button so a partial/animating
        // modal cannot be accepted as a stable actionable screen.
        for (var y = 950; y < 1400; y++)
        {
            Array.Fill(frame.Pixels, (byte)255, y * frame.Width + 250, 400);
            Array.Fill(frame.RgbaPixels!, (byte)255, (y * frame.Width + 250) * 4, 400 * 4);
        }
        var observer = new CareerScreenObserver(
            CareerFollowTrainerRecognitionTests.CapturedFrameRuntime.Create(frame));
        var connection = new LastVerifiedConnection(
            "adb", "serial", "android", "version", 900, 1600, 900, 1600,
            DateTimeOffset.UnixEpoch);

        Assert.Null(await observer.ObserveAsync(connection, pack,
            new UraCareerSessionState { CareerStarted = true, LastScreenId = "rewards" },
            false, CancellationToken.None));
    }

    [Fact]
    public async Task Rewards_after_event_reward_use_masked_title_and_opaque_cropped_next()
    {
        var root = FindWorkspaceRoot();
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var rewards = pack.ScreenProfile.Find("rewards");
        Assert.NotNull(rewards);
        Assert.Equal("rewards_rewards_next", rewards.FindAction("rewards.next")?.Task);

        var frame = Load(CareerTestResourceResolver.FindUraCapture(root, "event_reward_after_next.png"));
        var ordinaryRewards = Load(CareerTestResourceResolver.FindUraCapture(root, "ura_rewards_next.png"));
        var giftOverlay = Load(CareerTestResourceResolver.FindUraCapture(root, "event_reward_live.png"));
        var header = Load(CareerTestResourceResolver.ResolveUraScreenTemplate(
            pack, rewards, rewards.Recognition.Template!));
        Assert.Equal(0, header.RgbaPixels![3]);
        Assert.True(Match(frame, header, rewards.Recognition).Found);
        Assert.True(Match(ordinaryRewards, header, rewards.Recognition).Found);
        Assert.False(Match(giftOverlay, header, rewards.Recognition).Found);

        var nextTask = pack.ExecutionDefinition.GetTask("rewards_rewards_next");
        var next = Load(CareerTestResourceResolver.ResolveUraVisualResource(pack, "templates/rewards_rewards_next.png"));
        Assert.InRange(next.Width, 70, 90);
        Assert.InRange(next.Height, 30, 45);
        Assert.Equal(255, next.RgbaPixels![3]);
        var match = TemplateMatcher.FindColor(frame, next, nextTask.Roi,
            nextTask.TemplateThreshold, 900, 1600);
        Assert.True(match.Found, $"Rewards Next score {match.Score:0.000}.");
        Assert.InRange(match.CenterX, 440, 460);
        Assert.InRange(match.CenterY, 1460, 1480);
    }

    [Fact]
    public async Task Event_reward_gift_box_is_recognized_and_its_cropped_next_can_be_clicked()
    {
        var root = FindWorkspaceRoot();
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var eventReward = pack.ScreenProfile.Find("event_reward");
        Assert.NotNull(eventReward);
        Assert.Equal(CareerScreenKind.Settlement,
            CareerScreenClassification.Classify(eventReward.ScreenId, pack.ScreenProfile));
        Assert.Equal("event_reward_next", eventReward.FindAction("event_reward.next")?.Task);

        var frame = Load(CareerTestResourceResolver.FindUraCapture(root, "event_reward_live.png"));
        var afterNext = Load(CareerTestResourceResolver.FindUraCapture(root, "event_reward_after_next.png"));
        var ordinaryReward = Load(CareerTestResourceResolver.FindUraCapture(root, "ura_rewards_next.png"));
        var box = Load(CareerTestResourceResolver.ResolveUraScreenTemplate(
            pack, eventReward, eventReward.Recognition.Template!));
        var boxMatch = TemplateMatcher.FindColor(frame, box,
            eventReward.Recognition.Roi, eventReward.Recognition.TemplateThreshold,
            900, 1600);
        Assert.True(boxMatch.Found, $"Gift box score {boxMatch.Score:0.000}.");
        Assert.InRange(boxMatch.CenterX, 430, 470);
        Assert.InRange(boxMatch.CenterY, 1270, 1310);
        Assert.False(TemplateMatcher.FindColor(ordinaryReward, box,
            eventReward.Recognition.Roi, eventReward.Recognition.TemplateThreshold,
            900, 1600).Found);
        Assert.False(TemplateMatcher.FindColor(afterNext, box,
            eventReward.Recognition.Roi, eventReward.Recognition.TemplateThreshold,
            900, 1600).Found);

        var nextTask = pack.ExecutionDefinition.GetTask("event_reward_next");
        Assert.Equal("ClickSelf", nextTask.Action);
        Assert.Equal("MatchTemplateColor", nextTask.Algorithm);
        var nextText = Load(CareerTestResourceResolver.ResolveUraVisualResource(pack, "templates/event_reward_next_text.png"));
        Assert.InRange(nextText.Width, 70, 90);
        Assert.InRange(nextText.Height, 30, 45);
        var nextMatch = TemplateMatcher.FindColor(frame, nextText,
            nextTask.Roi, nextTask.TemplateThreshold, 900, 1600);
        Assert.True(nextMatch.Found, $"Next score {nextMatch.Score:0.000}.");
        Assert.InRange(nextMatch.CenterX, 440, 460);
        Assert.InRange(nextMatch.CenterY, 1460, 1480);
        Assert.Equal(0, box.RgbaPixels![3]);
        Assert.Equal(255, nextText.RgbaPixels![3]);
    }

    [Fact]
    public async Task Complete_career_entry_and_finish_dialog_are_distinguished()
    {
        var root = FindWorkspaceRoot();
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var entry = pack.ScreenProfile.Find("complete_career_entry");
        var dialog = pack.ScreenProfile.Find("complete_career");
        Assert.NotNull(entry);
        Assert.NotNull(dialog);
        Assert.Equal(CareerScreenKind.Settlement,
            CareerScreenClassification.Classify(entry.ScreenId, pack.ScreenProfile));
        Assert.Equal("complete_career_entry_open", entry.FindAction("career.open")?.Task);
        Assert.Equal("complete_career_career_finish", dialog.FindAction("career.finish")?.Task);

        var entryFrame = Load(CareerTestResourceResolver.FindUraCapture(root, "complete_career_entry.png"));
        var dialogFrame = Load(CareerTestResourceResolver.FindUraCapture(root, "ura_complete_career_next.png"));
        var entryTemplate = Load(CareerTestResourceResolver.ResolveUraScreenTemplate(
            pack, entry, entry.Recognition.Template!));
        var dialogTemplate = Load(CareerTestResourceResolver.ResolveUraScreenTemplate(
            pack, dialog, dialog.Recognition.Template!));
        var entryMatch = Match(entryFrame, entryTemplate, entry.Recognition);
        Assert.True(entryMatch.Found, $"Entry score {entryMatch.Score:0.000}.");
        Assert.InRange(entryMatch.CenterX, 65, 95);
        Assert.InRange(entryMatch.CenterY, 10, 40);
        Assert.False(Match(entryFrame, dialogTemplate, dialog.Recognition).Found);
        Assert.False(Match(dialogFrame, entryTemplate, entry.Recognition).Found);
        Assert.True(Match(dialogFrame, dialogTemplate, dialog.Recognition).Found);

        var open = pack.ExecutionDefinition.GetTask("complete_career_entry_open");
        Assert.Equal("ClickSelf", open.Action);
        Assert.Equal("MatchTemplateColor", open.Algorithm);
        var actionTemplate = Load(CareerTestResourceResolver.ResolveUraVisualResource(pack, open.Template!));
        var actionMatch = TemplateMatcher.FindColor(entryFrame, actionTemplate,
            open.Roi, open.TemplateThreshold, 900, 1600);
        Assert.True(actionMatch.Found, $"Click score {actionMatch.Score:0.000}.");
        Assert.InRange(actionMatch.CenterX, 590, 690);
        Assert.InRange(actionMatch.CenterY, 1320, 1380);

        var trainingSelectionFrame = Load(CareerTestResourceResolver.FindUraCapture(
            root, "training_selection_turn15_ura.png"));
        Assert.False(Match(trainingSelectionFrame, entryTemplate, entry.Recognition).Found);
        Assert.False(TemplateMatcher.FindColor(trainingSelectionFrame, actionTemplate,
            open.Roi, open.TemplateThreshold, 900, 1600).Found);

        var rgba = actionTemplate.RgbaPixels!;
        for (var x = 0; x < actionTemplate.Width; x++)
        {
            Assert.Equal(0, rgba[(x * 4) + 3]);
            Assert.Equal(0, rgba[(((actionTemplate.Height - 1) * actionTemplate.Width + x) * 4) + 3]);
        }
        for (var y = 0; y < actionTemplate.Height; y++)
        {
            Assert.Equal(0, rgba[((y * actionTemplate.Width) * 4) + 3]);
            Assert.Equal(0, rgba[((y * actionTemplate.Width + actionTemplate.Width - 1) * 4) + 3]);
        }
    }

    private static TemplateMatchResult Match(
        GrayImage frame,
        GrayImage template,
        UraScreenRecognition recognition) =>
        TemplateMatcher.FindColor(frame, template, recognition.Roi,
            recognition.TemplateThreshold, 900, 1600,
            requireTextContrast: recognition.MatchColorText);

    private static GrayImage Load(string path) =>
        GrayImageCodec.FromFile(path)
        ?? throw new FileNotFoundException("Could not load settlement image.", path);

    private static string FindWorkspaceRoot() => CareerTestResourceResolver.FindWorkspaceRoot();
}
