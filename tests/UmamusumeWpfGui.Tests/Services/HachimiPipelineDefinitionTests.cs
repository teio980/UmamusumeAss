using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class HachimiPipelineDefinitionTests
{
    private static readonly int[] TopCardRoi = [35, 130, 165, 220];
    private static readonly int[] FilterTabRoi = [430, 120, 450, 110];

    [Theory]
    [InlineData("mail_collection.json", "Home")]
    [InlineData("team_race.json", "RaceTab")]
    [InlineData("daily_race.json", "DailyProgram")]
    [InlineData("mission_collection.json", "missionIcon")]
    [InlineData("shop_task.json", "home")]
    public async Task Ordinary_pipeline_definitions_load_with_the_shared_schema(
        string fileName,
        string expectedTask)
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "pipelines", fileName);

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

        Assert.NotNull(definition);
        Assert.Equal(1, definition!.SchemaVersion);
        Assert.Contains(expectedTask, definition.Tasks.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.True(definition.ReferenceWidth > 0);
        Assert.True(definition.ReferenceHeight > 0);
    }

    [Fact]
    public async Task Career_delete_closes_the_deleted_data_dialog_with_a_template()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "ura", "screens", "execution.json");

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

        Assert.NotNull(definition);
        var deleteConfirm = definition!.GetTask("career_continue_delete_confirm");
        var close = definition.GetTask("career_continue_delete_close");

        Assert.Equal("career_continue_delete_close", deleteConfirm.Next.Single(), ignoreCase: true);
        Assert.Equal("ClickSelf", close.Action, ignoreCase: true);
        Assert.Equal("templates/career_continue_delete_close.png", close.Template);
        Assert.Equal([200, 900, 520, 260], close.Roi!);
        Assert.True(File.Exists(Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            close.Template!)));
    }

    [Fact]
    public async Task Mail_collection_clicks_close_after_collecting_all()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "pipelines", "mail_collection.json");

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

        Assert.NotNull(definition);
        var collectAll = definition!.GetTask("collectAll");
        var rewardClose = definition.GetTask("rewardClose");
        var close = definition.GetTask("close");

        Assert.Equal("rewardClose", collectAll.Next.Single(), ignoreCase: true);
        Assert.Equal("ClickSelf", rewardClose.Action, ignoreCase: true);
        Assert.Equal("templates/mission_collection/reward_close.png", rewardClose.Template);
        Assert.Equal("closeOptional", rewardClose.Next.Single(), ignoreCase: true);
        Assert.Equal("close", rewardClose.OnErrorNext.Single(), ignoreCase: true);
        Assert.Equal("ClickSelf", close.Action, ignoreCase: true);
        Assert.Equal("templates/mail_collection/close.png", close.Template);
        Assert.NotNull(close.Roi);
        Assert.Equal([0, 0, 900, 1600], close.Roi!);
        Assert.True(File.Exists(Path.Combine(root, "resource", "hachimi", "pipelines", close.Template!)));
    }

    [Fact]
    public async Task Team_race_exposes_the_shop_pipeline_to_the_parallel_result_monitor()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "pipelines", "team_race.json");

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

        Assert.NotNull(definition);
        var trigger = definition!.GetTask("runRandomShop");
        var probe = definition.GetTask("teamShopProbe");
        Assert.Equal("RunPipeline", trigger.Action);
        Assert.Equal("JustReturn", trigger.Algorithm);
        Assert.Equal("shop.json", trigger.Pipeline);
        Assert.Equal("shopProbe", trigger.Entry);
        Assert.Contains("MiddleNext", trigger.Next);
        Assert.Contains("MiddleNext", trigger.OnErrorNext);
        Assert.Equal("Wait", probe.Action);
        Assert.Equal("templates/shop/shop_title.png", probe.Template);
        Assert.Contains("runRandomShop", probe.Next);
        Assert.Contains("newhighscoreProbe", probe.OnErrorNext);
        var saleProbe = definition.GetTask("teamSaleProbe");
        var saleButton = definition.GetTask("teamSaleShopButton");
        Assert.Equal("templates/daily_race/items_cancel.png", saleProbe.Template);
        Assert.Contains("teamSaleShopButton", saleProbe.Next);
        Assert.Contains("teamShopProbe", saleProbe.OnErrorNext);
        Assert.Equal(
            "templates/daily_race/daily_sale_shop_button.png",
            saleButton.Template);
        Assert.Contains("runRandomShop", saleButton.Next);
        var monitor = definition.GetTask("resultMonitor");
        Assert.Equal("ParallelMonitor", monitor.Algorithm);
        Assert.Contains("teamSaleShopButton", monitor.MonitorTasks);
        Assert.Contains("runRandomShop", monitor.MonitorTasks);
        Assert.Equal("raceagain", monitor.SuccessTask);
        Assert.Contains("teamRaceAfterShop", monitor.SuccessTasks);
        Assert.Equal(180000, monitor.TimeoutMilliseconds);
    }

    [Fact]
    public async Task Mission_collection_checks_each_tab_and_returns_home()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "pipelines", "mission_collection.json");

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

        Assert.NotNull(definition);
        Assert.Equal("templates/mission_collection/mission_icon.png", definition!.GetTask("missionIcon").Template);
        Assert.Equal("mainTab", definition.GetTask("dailyRed").OnErrorNext.Single());
        Assert.Equal("titlesTab", definition.GetTask("mainRed").OnErrorNext.Single());
        Assert.Equal("specialTab", definition.GetTask("titlesRed").OnErrorNext.Single());
        Assert.Equal("returnHome", definition.GetTask("specialRed").OnErrorNext.Single());
        Assert.True(definition.GetTask("homeVerify").Success);

        var tabRects = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["dailySelected"] = [20, 570, 215, 75],
            ["dailyUnselected"] = [20, 570, 215, 75],
            ["mainTab"] = [235, 570, 215, 75],
            ["titlesTab"] = [450, 570, 215, 75],
            ["specialTab"] = [665, 570, 215, 75],
        };
        foreach (var (taskName, expectedRect) in tabRects)
        {
            var tab = definition.GetTask(taskName);
            Assert.Equal("ClickRect", tab.Action, ignoreCase: true);
            Assert.Equal(expectedRect, tab.SpecificRect);
            Assert.Null(tab.Template);
        }

        var returnHome = definition.GetTask("returnHome");
        Assert.Equal("ClickRect", returnHome.Action, ignoreCase: true);
        Assert.Equal([20, 1300, 150, 100], returnHome.SpecificRect!);
        Assert.Null(returnHome.Template);
        Assert.Equal("homeVerify", returnHome.Next.Single(), ignoreCase: true);

        foreach (var closeTaskName in new[] { "dailyClose", "mainClose", "titlesClose", "specialClose" })
        {
            Assert.Equal(
                [250, 850, 450, 750],
                definition.GetTask(closeTaskName).Roi!);
        }
    }

    [Fact]
    public async Task Team_race_uses_parallel_result_monitor_until_race_again()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "pipelines", "team_race.json");

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

        Assert.NotNull(definition);
        var next = definition!.GetTask("next");
        var whiteskip = definition.GetTask("whiteskip");
        var highScoreProbe = definition.GetTask("newhighscoreProbe");
        var highScore = definition.GetTask("newhighscore");
        var saleAfterHighScore = definition.GetTask("teamSaleAfterHighscoreProbe");
        var shopAfterHighScore = definition.GetTask("teamShopAfterHighscore");

        var monitor = definition.GetTask("resultMonitor");
        Assert.Contains("resultMonitor", whiteskip.Next);
        Assert.Contains("resultMonitor", next.Next);
        Assert.Contains("newhighscore", highScoreProbe.Next);
        Assert.Contains("MiddleNext", highScoreProbe.OnErrorNext);
        Assert.Contains("teamSaleShopButtonAfterHighscore", highScore.Next);
        Assert.Contains("teamShopAfterHighscore", saleAfterHighScore.OnErrorNext);
        Assert.Contains("runRandomShop", shopAfterHighScore.Next);
        Assert.Contains("MiddleNext", shopAfterHighScore.OnErrorNext);
        Assert.Equal(
            "templates/shop/shop_title.png",
            definition.GetTask("runRandomShop").Template);
        Assert.Equal("ParallelMonitor", monitor.Algorithm);
        Assert.Contains("next", monitor.MonitorTasks);
        Assert.Contains("newhighscore", monitor.MonitorTasks);
        Assert.Contains("teamSaleShopButton", monitor.MonitorTasks);
        Assert.Contains("runRandomShop", monitor.MonitorTasks);
        Assert.Equal("raceagain", monitor.SuccessTask);
        Assert.Contains("teamRaceAfterShop", monitor.SuccessTasks);
        Assert.Equal(
            "templates/team_race/team_race.png",
            definition.GetTask("teamRaceAfterShop").Template);
        Assert.Equal(
            "opponent",
            definition.GetTask("teamRaceAfterShop").Next.Single());
        Assert.Equal("race", definition.GetTask("teamRaceAfterShop").CountAs);
        Assert.Equal("raceAdvance", definition.GetTask("teamRaceAfterShop").CountKey);
        Assert.Equal("raceAdvance", definition.GetTask("raceagain").CountKey);
        Assert.Equal(
            "complete",
            definition.GetTask("teamRaceAfterShop").ExceededNext.Single());
        Assert.Equal(180000, monitor.TimeoutMilliseconds);
        Assert.Equal(
            "templates/start_game/notices_close.png",
            definition.GetTask("noticesClose").Template);
        var storyUnlockedClose = definition.GetTask("storyUnlockedClose");
        Assert.Equal(
            "templates/team_race/story_unlocked_close.png",
            storyUnlockedClose.Template);
        Assert.Equal("ClickSelf", storyUnlockedClose.Action, ignoreCase: true);
        Assert.Equal([240, 940, 420, 260], storyUnlockedClose.Roi!);
        Assert.False(storyUnlockedClose.Required);
        Assert.Contains("storyUnlockedClose", monitor.MonitorTasks);
        Assert.True(File.Exists(Path.Combine(
            root,
            "resource",
            "hachimi",
            "pipelines",
            storyUnlockedClose.Template!)));
    }

    [Fact]
    public async Task Shop_pipeline_skips_sold_out_items_and_returns_with_back()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "pipelines", "shop.json");

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

        Assert.NotNull(definition);
        Assert.Equal("shopNoShopComplete", definition!.GetTask("shopProbe").OnErrorNext.Single());
        Assert.False(definition.GetTask("shopBuy1").Required);
        Assert.False(definition.GetTask("shopBuy21").Required);
        Assert.Equal([680, 580, 220, 220], definition.GetTask("shopBuy1").Roi!);
        var back = definition.GetTask("shopBack");
        Assert.Equal("ClickSelf", back.Action, ignoreCase: true);
        Assert.Equal("templates/shop/back.png", back.Template);
        Assert.Equal("shopComplete", back.Next.Single());
        Assert.Equal("shopAndroidBack", back.OnErrorNext.Single());
        Assert.True(definition.GetTask("shopComplete").Success);
        Assert.True(File.Exists(Path.Combine(root, "resource", "hachimi", "pipelines", back.Template!)));
    }

    [Fact]
    public void Shop_settings_repeat_each_trigger_for_three_daily_sales()
    {
        var options = new ShopPurchaseOptions(
            SelectAll: false,
            BuyStarPieces: true,
            BuyAlarmClock: false,
            BuyPleasingParfait: false,
            BuyShoes: false,
            BuySupportPoints: false,
            BuyFlags: false);

        var overrides = options.ToMaxTimesOverrides();

        Assert.Equal(16, overrides.Count);
        foreach (var slot in Enumerable.Range(1, 21))
        {
            var triggerSlot = (slot - 1) % 7 + 1;
            if (triggerSlot is 1 or 2)
                Assert.DoesNotContain($"shopBuy{slot}", overrides.Keys);
            else
                Assert.Equal(0, overrides[$"shopBuy{slot}"]);
        }
    }

    [Fact]
    public async Task Shop_task_enters_daily_sales_and_calls_shared_shop_pipeline()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "pipelines", "shop_task.json");

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

        Assert.NotNull(definition);
        Assert.Equal("ClickRect", definition!.GetTask("specialShop").Action, ignoreCase: true);
        Assert.Equal([270, 1320, 100, 120], definition.GetTask("specialShop").SpecificRect!);
        Assert.Equal([40, 1140, 400, 150], definition.GetTask("dailySales").SpecificRect!);
        var runShop = definition.GetTask("runShop");
        Assert.Equal("RunPipeline", runShop.Action, ignoreCase: true);
        Assert.Equal("shop.json", runShop.Pipeline);
        Assert.Equal("shopProbe", runShop.Entry);
    }

    [Fact]
    public async Task Daily_race_enables_multi_race_and_configures_ticket_count()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "pipelines", "daily_race.json");

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

        Assert.NotNull(definition);
        var onProbe = definition!.GetTask("multiRaceOnProbe");
        var modeGate = definition.GetTask("multiRaceModeGate");
        var enable = definition.GetTask("enableMultiRace");
        var onVerify = definition.GetTask("multiRaceOnVerify");
        var offProbe = definition.GetTask("multiRaceOffProbe");
        var disable = definition.GetTask("disableMultiRace");
        var offVerify = definition.GetTask("multiRaceOffVerify");
        var ticketDialog = definition.GetTask("multiRaceTicketDialog");
        var ticketGate = definition.GetTask("multiRaceTicketGate");
        var ticketMinus = definition.GetTask("multiRaceTicketMinus");
        var ticketPlus = definition.GetTask("multiRaceTicketPlus");
        var ticketConfirm = definition.GetTask("multiRaceTicketConfirm");
        var multiRaceComplete = definition.GetTask("multiRaceComplete");
        var multiRaceSaleProbe = definition.GetTask("multiRaceSaleProbe");
        var multiRaceSaleShopButton = definition.GetTask("multiRaceSaleShopButton");
        var runMultiRaceSaleShop = definition.GetTask("runMultiRaceSaleShop");
        var multiRaceSaleCancel = definition.GetTask("multiRaceSaleCancel");
        var sortConfirm = definition.GetTask("runnerSortDialogConfirm");
        var runnerConfirm = definition.GetTask("runnerConfirm");
        var itemsViewResult = definition.GetTask("itemsViewResult");
        var viewResultTap = definition.GetTask("viewResultTap");
        var preRace = definition.GetTask("preRace");
        var playbackOk = definition.GetTask("playbackOk");
        var playbackStart = definition.GetTask("racePlaybackResult");
        var itemsRace = definition.GetTask("itemsRace");
        var raceSkip = definition.GetTask("raceSkip");
        var finalNext = definition.GetTask("finalNext");
        var finalNextSupport = definition.GetTask("finalNextSupport");
        var dailySaleProbe = definition.GetTask("dailySaleProbe");
        var dailySaleShopButton = definition.GetTask("dailySaleShopButton");
        var runDailySaleShop = definition.GetTask("runDailySaleShop");
        var rewardNext = definition.GetTask("rewardNext");
        var verifyReturn = definition.GetTask("verifyDailyRaceReturn");

        Assert.Equal("templates/daily_race/multi_race_on_text.png", onProbe.Template);
        Assert.Contains("enableMultiRace", onProbe.OnErrorNext);
        Assert.Equal("multiRaceOnProbe", modeGate.Next.Single());
        Assert.Equal("multiRaceOffProbe", modeGate.ExceededNext.Single());
        Assert.Equal("templates/daily_race/multi_race_off_text.png", enable.Template);
        Assert.Equal("templates/daily_race/multi_race_on_text.png", onVerify.Template);
        Assert.Equal("templates/daily_race/multi_race_off_text.png", offProbe.Template);
        Assert.Equal("templates/daily_race/multi_race_on_text.png", disable.Template);
        Assert.Equal("templates/daily_race/multi_race_off_text.png", offVerify.Template);
        Assert.Equal("runnerSelectHighest", sortConfirm.Next.Single());
        Assert.Equal("multiRaceTicketGate", runnerConfirm.Next.Single());
        Assert.Equal("multiRaceTicketDialog", ticketGate.Next.Single());
        Assert.Equal("previewNext", ticketGate.ExceededNext.Single());
        Assert.Equal("templates/daily_race/multi_race_ticket_dialog.png", ticketDialog.Template);
        Assert.Equal("multiRaceTicketMinus", ticketDialog.Next.Single());
        Assert.Equal("templates/daily_race/multi_race_ticket_minus.png", ticketMinus.Template);
        // The minus step intentionally loops while tickets are above the
        // minimum. It falls through to the plus step only when the matcher
        // can no longer find the minus button (or its max-times guard trips).
        Assert.Equal("multiRaceTicketMinus", ticketMinus.Next.Single());
        Assert.Equal("multiRaceTicketPlus", ticketMinus.OnErrorNext.Single());
        Assert.Equal("multiRaceTicketPlus", ticketMinus.ExceededNext.Single());
        Assert.Equal(6, ticketMinus.MaxTimes);
        Assert.Equal("templates/daily_race/multi_race_ticket_plus.png", ticketPlus.Template);
        Assert.Equal("multiRaceTicketPlus", ticketPlus.Next.Single());
        Assert.Equal("multiRaceTicketConfirm", ticketPlus.ExceededNext.Single());
        Assert.Equal("templates/daily_race/multi_race_ticket_confirm.png", ticketConfirm.Template);
        Assert.Equal("multiRaceComplete", ticketConfirm.Next.Single());
        Assert.Equal("templates/daily_race/multi_race_complete.png", multiRaceComplete.Template);
        Assert.Equal("multiRaceResultsClose", multiRaceComplete.Next.Single());
        var multiRaceResultsClose = definition.GetTask("multiRaceResultsClose");
        Assert.Equal("ClickRect", multiRaceResultsClose.Action, ignoreCase: true);
        Assert.Equal("multiRaceSaleProbe", multiRaceResultsClose.Next.Single());
        Assert.Equal("multiRaceSaleShopButton", multiRaceSaleProbe.Next.Single());
        Assert.Equal("verifyDailyRaceReturn", multiRaceSaleProbe.OnErrorNext.Single());
        Assert.Equal("runMultiRaceSaleShop", multiRaceSaleShopButton.Next.Single());
        Assert.Equal("multiRaceSaleCancel", multiRaceSaleShopButton.OnErrorNext.Single());
        Assert.Equal("shop.json", runMultiRaceSaleShop.Pipeline);
        Assert.Equal("verifyDailyRaceReturn", runMultiRaceSaleShop.Next.Single());
        Assert.Equal("verifyDailyRaceReturn", runMultiRaceSaleShop.OnErrorNext.Single());
        Assert.Equal("verifyDailyRaceReturn", multiRaceSaleCancel.Next.Single());
        Assert.Equal("itemsViewResult", itemsRace.Next.Single());
        Assert.False(itemsViewResult.Required);
        Assert.Equal("ClickSelf", itemsViewResult.Action);
        Assert.Equal("templates/daily_race/view_results_button.png", itemsViewResult.Template);
        Assert.Equal("viewResultTap", itemsViewResult.Next.Single());
        Assert.Equal("preRace", itemsViewResult.OnErrorNext.Single());
        Assert.Equal("ClickSelf", viewResultTap.Action);
        Assert.Equal("templates/daily_race/view_result_tap.png", viewResultTap.Template);
        Assert.Equal("finalNext", viewResultTap.Next.Single());
        Assert.Equal("ClickSelf", preRace.Action);
        Assert.Equal("templates/daily_race/pre_race_start_button.png", preRace.Template);
        Assert.Equal("racePlaybackResult", playbackOk.Next.Single());
        Assert.Equal("ClickSelf", playbackStart.Action);
        Assert.Equal("templates/daily_race/playback_race_button.png", playbackStart.Template);
        Assert.Equal("raceSkip", playbackStart.Next.Single());
        Assert.Equal("ClickRect", raceSkip.Action);
        Assert.Equal("templates/team_race/whiteskip.png", raceSkip.Template);
        Assert.NotNull(raceSkip.Roi);
        Assert.Equal([620, 1360, 180, 240], raceSkip.Roi!);
        Assert.NotNull(raceSkip.SpecificRect);
        Assert.Equal([650, 1450, 110, 130], raceSkip.SpecificRect!);
        Assert.True(raceSkip.Required);
        Assert.Equal("finalNext", raceSkip.Next.Single());
        Assert.Empty(raceSkip.OnErrorNext);
        Assert.Equal("ClickSelf", finalNext.Action);
        Assert.Equal("templates/daily_race/result_next_moonlight_button.png", finalNext.Template);
        Assert.Equal("finalNextSupport", finalNext.OnErrorNext.Single());
        Assert.Equal("templates/daily_race/result_next_button.png", finalNextSupport.Template);
        Assert.Equal("dailySaleProbe", finalNextSupport.Next.Single());
        Assert.Equal("dailySaleProbe", finalNext.Next.Single());
        Assert.Equal("templates/daily_race/items_cancel.png", dailySaleProbe.Template);
        Assert.Equal("dailySaleShopButton", dailySaleProbe.Next.Single());
        Assert.Equal("rewardNext", dailySaleProbe.OnErrorNext.Single());
        Assert.Equal("ClickSelf", dailySaleShopButton.Action);
        Assert.Equal("templates/daily_race/daily_sale_shop_button.png", dailySaleShopButton.Template);
        Assert.Equal("runDailySaleShop", dailySaleShopButton.Next.Single());
        Assert.Equal("dailySaleCancel", dailySaleShopButton.OnErrorNext.Single());
        Assert.Equal("RunPipeline", runDailySaleShop.Action);
        Assert.Equal("shop.json", runDailySaleShop.Pipeline);
        Assert.Equal("shopProbe", runDailySaleShop.Entry);
        Assert.Equal("rewardNext", runDailySaleShop.Next.Single());
        Assert.Equal("ClickSelf", rewardNext.Action);
        Assert.Equal("templates/daily_race/reward_next_button.png", rewardNext.Template);
        Assert.Equal("verifyDailyRaceReturn", rewardNext.Next.Single());
        Assert.Equal("templates/daily_race/daily_races_header.png", verifyReturn.Template);
        Assert.Equal("complete", verifyReturn.Next.Single());
    }

    [Fact]
    public void Start_game_keeps_its_special_monitor_and_trigger_chain_definition()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "pipelines", "start_game.json");
        var json = File.ReadAllText(path);
        var definition = System.Text.Json.JsonSerializer.Deserialize<StartGamePipelineDefinition>(json);

        Assert.NotNull(definition);
        Assert.Equal("StartupMonitor", definition!.Start);
        var monitor = definition.Tasks[definition.Start];
        Assert.Equal("StartupMonitor", monitor.Algorithm);
        Assert.Equal("CheckStartNoticeSkip", monitor.TriggerTask);
        Assert.Equal(["CheckLogoSkip"], monitor.TriggerChain);
        Assert.Contains("CheckGameHome", monitor.MonitorTasks);
        Assert.Equal(180000, monitor.TimeoutMilliseconds);
        Assert.Equal(5000, monitor.SuccessConfirmationDelayMilliseconds);
    }

    [Fact]
    public async Task Ura_support_start_buttons_use_template_detection()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "ura", "screens", "execution.json");

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

        Assert.NotNull(definition);
        foreach (var taskName in new[]
        {
            "support_select_support_start",
            "support_ready_support_start",
        })
        {
            var task = definition!.GetTask(taskName);
            Assert.Equal("MatchTemplate", task.Algorithm, ignoreCase: true);
            Assert.Equal("ClickSelf", task.Action, ignoreCase: true);
            Assert.NotNull(task.Template);
            Assert.Equal([250, 1260, 500, 180], task.Roi!);
            Assert.Equal(0.56, task.TemplateThreshold);
        }
    }

    [Fact]
    public void Ura_support_select_recognition_uses_the_stable_auto_fill_button()
    {
        var root = FindSolutionRoot();
        var json = File.ReadAllText(Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "screen_profile.json"));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var supportSelect = document.RootElement
            .GetProperty("screens")
            .EnumerateArray()
            .Single(item => item.GetProperty("screenId").GetString() == "support_select");
        var recognition = supportSelect.GetProperty("recognition");

        Assert.Equal(
            "templates/support_select_support_auto_fill.png",
            recognition.GetProperty("template").GetString());
        Assert.Equal(
            [350, 1030, 520, 240],
            recognition.GetProperty("roi").EnumerateArray().Select(item => item.GetInt32()).ToArray());
        Assert.Equal(0.92, recognition.GetProperty("templThreshold").GetDouble());
    }

    [Fact]
    public void Ura_support_autofill_confirmation_recognition_uses_the_fixed_ok_button()
    {
        var root = FindSolutionRoot();
        var json = File.ReadAllText(Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "screen_profile.json"));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var confirmation = document.RootElement
            .GetProperty("screens")
            .EnumerateArray()
            .Single(item => item.GetProperty("screenId").GetString() == "support_autofill_confirmation");
        var recognition = confirmation.GetProperty("recognition");

        Assert.Equal(
            "templates/support_autofill_confirmation_support_autofill_ok.png",
            recognition.GetProperty("template").GetString());
        Assert.Equal(
            [430, 940, 440, 220],
            recognition.GetProperty("roi").EnumerateArray().Select(item => item.GetInt32()).ToArray());
        Assert.Equal(0.78, recognition.GetProperty("templThreshold").GetDouble());
    }

    [Fact]
    public void Ura_support_ready_recognition_uses_the_start_template()
    {
        var root = FindSolutionRoot();
        var json = File.ReadAllText(Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "screen_profile.json"));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var supportReady = document.RootElement
            .GetProperty("screens")
            .EnumerateArray()
            .Single(item => item.GetProperty("screenId").GetString() == "support_ready");
        var recognition = supportReady.GetProperty("recognition");

        Assert.Equal(
            "templates/support_ready_support_start.png",
            recognition.GetProperty("template").GetString());
        Assert.Equal(0.56, recognition.GetProperty("templThreshold").GetDouble());
        Assert.Equal(
            [250, 1260, 500, 180],
            recognition.GetProperty("roi").EnumerateArray().Select(item => item.GetInt32()).ToArray());
    }

    [Fact]
    public async Task Ura_support_picker_controls_are_template_driven()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "ura", "screens", "execution.json");
        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

        Assert.NotNull(definition);
        foreach (var taskName in new[]
        {
            "support_select_support_auto_fill",
            "support_select_support_display_settings",
            "support_select_support_sort_level",
            "support_select_support_friend_sort_level",
            "support_select_support_sort_apply",
            "support_select_support_filter_tab",
            "support_select_support_filter_reset",
            "support_select_support_filter_r",
            "support_select_support_filter_sr",
            "support_select_support_filter_ssr",
            "support_select_support_filter_speed",
            "support_select_support_filter_stamina",
            "support_select_support_filter_power",
            "support_select_support_filter_guts",
            "support_select_support_filter_wit",
            "support_select_support_filter_friend",
            "support_select_support_filter_apply",
            "support_select_support_reset",
            "support_select_support_reset_ok",
            "support_select_support_top_card_ssr",
            "support_select_support_top_card_sr",
            "support_select_support_friend_top_card_ssr",
            "support_select_support_friend_top_card_sr",
            "support_select_support_open",
            "support_select_support_close",
            "support_select_support_start",
        })
        {
            var task = definition!.GetTask(taskName);
            Assert.Equal("MatchTemplate", task.Algorithm, ignoreCase: true);
            Assert.Equal("ClickSelf", task.Action, ignoreCase: true);
            Assert.False(string.IsNullOrWhiteSpace(task.Template));
            Assert.Null(task.SpecificRect);
        }

        Assert.Null(definition!.GetTask("support_select_support_card").Roi);
        var exactCard = definition.GetTask("support_select_support_card_exact");
        Assert.Equal("MatchTemplateScaled", exactCard.Algorithm, ignoreCase: true);
        Assert.Equal("ClickSelf", exactCard.Action, ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(exactCard.Template));
        Assert.Null(exactCard.Roi);
        Assert.Equal(
            "support_select_support_card_exact_scroll",
            exactCard.OnErrorNext.Single());
        var exactScroll = definition.GetTask("support_select_support_card_exact_scroll");
        Assert.Equal("Swipe", exactScroll.Action, ignoreCase: true);
        Assert.Equal(
            "support_select_support_card_exact",
            exactScroll.Next.Single());
        Assert.Equal(
            "support_select_support_card_exact",
            GetSupportActionTask(root, "support.ranked.select_exact_card"));
        var topCard = definition.GetTask("support_select_support_top_card_ssr");
        Assert.Equal(TopCardRoi, topCard.Roi);
        Assert.Equal(
            "support_select_support_top_card_sr",
            topCard.OnErrorNext.Single());
        Assert.Equal(
            "support_select_support_top_card_ssr",
            GetSupportActionTask(root, "support.ranked.select_highest_card"));
        Assert.Equal(
            "templates/support_select_support_reset.png",
            definition.GetTask("support_select_support_reset").Template);
        Assert.Equal(
            [20, 1080, 460, 140],
            definition.GetTask("support_select_support_reset").Roi!);
        var resetIfNeeded = definition.GetTask("support_select_support_reset_if_needed");
        Assert.Equal("MatchTemplate", resetIfNeeded.Algorithm, ignoreCase: true);
        Assert.Equal("JustReturn", resetIfNeeded.Action, ignoreCase: true);
        Assert.Equal(
            "templates/support_select_support_reset_disabled.png",
            resetIfNeeded.Template);
        Assert.False(resetIfNeeded.Required);
        Assert.Equal(
            "support_select_support_reset_skip",
            resetIfNeeded.Next.Single());
        Assert.Equal(
            "support_select_support_reset",
            resetIfNeeded.OnErrorNext.Single());
        Assert.Equal(
            "support_select_support_reset_if_needed",
            GetSupportActionTask(root, "support.reset_if_needed"));
        Assert.Equal(
            "support_select_support_reset_ok",
            definition.GetTask("support_select_support_reset").Next.Single());
        Assert.Equal(
            "templates/support_select_support_reset_ok.png",
            definition.GetTask("support_select_support_reset_ok").Template);
        Assert.Equal(
            [450, 950, 420, 220],
            definition.GetTask("support_select_support_reset_ok").Roi!);
        Assert.Equal(
            "support_select_support_reset",
            GetSupportActionTask(root, "support.reset"));
        var filterTab = definition.GetTask("support_select_support_filter_tab");
        Assert.Equal(FilterTabRoi, filterTab.Roi);
        Assert.Equal(
            "support_select_support_filter_page_probe",
            filterTab.Next.Single());
        var filterPageProbe = definition.GetTask("support_select_support_filter_page_probe");
        Assert.Equal("JustReturn", filterPageProbe.Action, ignoreCase: true);
        Assert.Equal(
            "templates/support/filter_reset.png",
            filterPageProbe.Template);
        Assert.Equal(
            "support_select_support_card_list_probe",
            definition.GetTask("support_select_support_filter_apply").Next.Single());
        var cardListProbe = definition.GetTask("support_select_support_card_list_probe");
        Assert.Equal("JustReturn", cardListProbe.Action, ignoreCase: true);
        Assert.Equal(
            "templates/support/display_settings_icon.png",
            cardListProbe.Template);
        Assert.Equal([650, 1270, 120, 120], cardListProbe.Roi!);
        Assert.Equal(
            [650, 1270, 120, 120],
            definition.GetTask("support_select_support_display_settings").Roi!);
        foreach (var filterTaskName in new[]
        {
            "support_select_support_filter_reset",
            "support_select_support_filter_r",
            "support_select_support_filter_sr",
            "support_select_support_filter_ssr",
            "support_select_support_filter_speed",
            "support_select_support_filter_stamina",
            "support_select_support_filter_power",
            "support_select_support_filter_guts",
            "support_select_support_filter_wit",
            "support_select_support_filter_friend",
            "support_select_support_filter_apply",
        })
        {
            Assert.NotNull(definition.GetTask(filterTaskName).Roi);
        }
        var selectedProbe = definition.GetTask("support_select_support_selected_card");
        Assert.Equal("MatchTemplate", selectedProbe.Algorithm, ignoreCase: true);
        Assert.Equal("JustReturn", selectedProbe.Action, ignoreCase: true);
        Assert.Equal(
            "templates/support/selected.png",
            selectedProbe.Template);
        Assert.Equal(0.88, selectedProbe.TemplateThreshold);
        Assert.Equal(
            "support_select_support_selected_card",
            GetSupportActionTask(root, "support.ranked.detect_selected_card"));
        Assert.Equal(
            "templates/support/sort_level_live.png",
            definition.GetTask("support_select_support_sort_level").Template);
        Assert.Equal(
            "support_select_support_sort_level_selected",
            definition.GetTask("support_select_support_sort_level").OnErrorNext.Single());
        var selectedSort = definition.GetTask("support_select_support_sort_level_selected");
        Assert.Equal("JustReturn", selectedSort.Action, ignoreCase: true);
        Assert.Equal(
            "templates/support/sort_level_selected.png",
            selectedSort.Template);
        Assert.Equal([350, 280, 260, 130], definition.GetTask("support_select_support_sort_level").Roi!);
        Assert.Equal([350, 280, 260, 130], selectedSort.Roi!);
        var friendSort = definition.GetTask("support_select_support_friend_sort_level");
        Assert.Equal([20, 400, 230, 130], friendSort.Roi!);
        Assert.Equal(
            "support_select_support_friend_sort_level_selected",
            friendSort.OnErrorNext.Single());
        Assert.Equal(
            "templates/support/sort_level_friend_live.png",
            friendSort.Template);
        var friendSelectedSort = definition.GetTask(
            "support_select_support_friend_sort_level_selected");
        Assert.Equal([20, 400, 230, 130], friendSelectedSort.Roi!);
        Assert.Equal(
            "templates/support/sort_level_friend_selected.png",
            friendSelectedSort.Template);
        var sortDirection = definition.GetTask("support_select_support_sort_asc_click");
        Assert.Equal([700, 1300, 200, 100], sortDirection.Roi!);
        var supportOpen = definition.GetTask("support_select_support_open");
        Assert.Equal("templates/support/support_open.png", supportOpen.Template);
        Assert.Equal(6, supportOpen.SearchRois.Count);
        Assert.Equal([90, 420, 160, 160], supportOpen.SearchRois[0]);
        Assert.Equal([630, 770, 160, 160], supportOpen.SearchRois[^1]);
        var guestTopCard = definition.GetTask("support_select_support_guest_top_card_ssr");
        Assert.Equal("ClickSelf", guestTopCard.Action, ignoreCase: true);
        Assert.Equal([35, 130, 165, 220], guestTopCard.Roi!);
        Assert.Equal(
            "support_select_support_guest_top_card_ssr",
            GetSupportActionTask(root, "support.ranked.select_guest_highest_card"));
        var friendTopCard = definition.GetTask("support_select_support_friend_top_card_ssr");
        Assert.Equal([35, 250, 165, 230], friendTopCard.Roi!);
        Assert.Equal(
            "support_select_support_friend_top_card_sr",
            friendTopCard.OnErrorNext.Single());
        Assert.Equal(
            "support_select_support_friend_top_card_ssr",
            GetSupportActionTask(root, "support.ranked.select_friend_highest_card"));
        Assert.True(File.Exists(Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "templates",
            "support_cards",
            "r_badge.png")));
        foreach (var filterKey in new[]
        {
            "speed",
            "stamina",
            "power",
            "guts",
            "wit",
            "friend",
        })
        {
            Assert.True(
                File.Exists(Path.Combine(
                    root,
                    "resource",
                    "hachimi",
                    "ura",
                    "screens",
                    "templates",
                    "support_cards",
                    $"friend_type_{filterKey}.png")),
                $"Missing friend-page template for {filterKey}.");
        }
    }

    private static string GetSupportActionTask(string root, string semanticId)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "screen_profile.json")));
        var supportSelect = document.RootElement
            .GetProperty("screens")
            .EnumerateArray()
            .Single(item => item.GetProperty("screenId").GetString() == "support_select");
        return supportSelect
            .GetProperty("actions")
            .EnumerateArray()
            .Single(item => item.GetProperty("semanticId").GetString() == semanticId)
            .GetProperty("task")
            .GetString()!;
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CMakePresets.json")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
