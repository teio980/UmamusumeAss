using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

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
    public async Task PipelineEditorDtoPreservesNativeExtensionFieldsWithoutValidatingThem()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pipeline-dto-{Guid.NewGuid():N}.json");
        const string json = """
            {
              "name": "future-pipeline",
              "schemaVersion": 7,
              "futureRoot": { "enabled": true },
              "tasks": {
                "home": {
                  "action": "ClickSelf",
                  "futureTaskField": 17
                }
              }
            }
            """;

        try
        {
            await File.WriteAllTextAsync(path, json);
            var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

            Assert.NotNull(definition);
            Assert.Equal(7, definition!.SchemaVersion);
            Assert.Equal(17, definition.GetTask("home").AdditionalProperties!["futureTaskField"].GetInt32());
            using var roundTrip = System.Text.Json.JsonDocument.Parse(
                System.Text.Json.JsonSerializer.Serialize(definition));
            Assert.True(roundTrip.RootElement.GetProperty("futureRoot").GetProperty("enabled").GetBoolean());
            Assert.Equal(17, roundTrip.RootElement.GetProperty("tasks").GetProperty("home")
                .GetProperty("futureTaskField").GetInt32());
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
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
        var shopDefinition = await HachimiPipelineDefinitionLoader.LoadAsync(
            Path.Combine(root, "resource", "hachimi", "pipelines", "shop.json"));

        Assert.NotNull(definition);
        Assert.NotNull(shopDefinition);
        var trigger = definition!.GetTask("runRandomShop");
        var probe = definition.GetTask("teamShopProbe");
        var shopEntry = shopDefinition!.GetTask("shopProbe");
        Assert.Equal("RunPipeline", trigger.Action);
        Assert.Equal("JustReturn", trigger.Algorithm);
        Assert.Equal("shop.json", trigger.Pipeline);
        Assert.Equal("shopProbe", trigger.Entry);
        Assert.Equal(shopEntry.Template, trigger.Template);
        Assert.Equal(shopEntry.Roi, trigger.Roi);
        Assert.Contains("postShopMonitor", trigger.Next);
        Assert.Contains("postShopMonitor", trigger.OnErrorNext);
        Assert.Equal("Wait", probe.Action);
        Assert.Equal(shopEntry.Template, probe.Template);
        Assert.Equal(shopEntry.Roi, probe.Roi);
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
        Assert.Contains("runRandomShop", monitor.SuccessTasks);
        Assert.DoesNotContain("runRandomShop", monitor.MonitorTasks);
        Assert.Equal("raceagain", monitor.SuccessTask);
        Assert.Contains("teamRaceAfterShop", monitor.SuccessTasks);
        Assert.DoesNotContain("teamTrialsAfterShop", monitor.SuccessTasks);
        Assert.DoesNotContain("homeAfterShop", monitor.SuccessTasks);
        var postShopMonitor = definition.GetTask("postShopMonitor");
        Assert.Equal("ParallelMonitor", postShopMonitor.Algorithm);
        Assert.Contains("next", postShopMonitor.MonitorTasks);
        Assert.Contains("teamTrialsAfterShop", postShopMonitor.SuccessTasks);
        Assert.Contains("homeAfterShop", postShopMonitor.SuccessTasks);
        var homeAfterShop = definition.GetTask("homeAfterShop");
        Assert.Equal("MatchTemplateColor", homeAfterShop.Algorithm);
        Assert.Equal("templates/start_game/game_home_selected.png", homeAfterShop.Template);
        Assert.Equal("DoNothing", homeAfterShop.Action);
        Assert.Equal("raceTabAfterShop", homeAfterShop.Next.Single());
        Assert.Equal("teamTrialsAfterShop", definition.GetTask("raceTabAfterShop").Next.Single());
        Assert.Equal("teamRaceAfterShop", definition.GetTask("teamTrialsAfterShop").Next.Single());
        Assert.Equal("raceAdvance", definition.GetTask("teamRaceAfterShop").CountKey);
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
            "templates/shop/daily_sales_header.png",
            definition.GetTask("runRandomShop").Template);
        Assert.Equal(
            definition.GetTask("runRandomShop").Template,
            shopAfterHighScore.Template);
        Assert.Equal(
            definition.GetTask("runRandomShop").Roi,
            shopAfterHighScore.Roi);
        Assert.Equal("ParallelMonitor", monitor.Algorithm);
        Assert.Contains("next", monitor.MonitorTasks);
        Assert.Contains("newhighscore", monitor.MonitorTasks);
        Assert.Contains("teamSaleShopButton", monitor.MonitorTasks);
        Assert.Contains("runRandomShop", monitor.SuccessTasks);
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
    public async Task Shop_pipeline_uses_select_all_or_ocr_and_returns_with_back()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "pipelines", "shop.json");

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

        Assert.NotNull(definition);
        var probe = definition!.GetTask("shopProbe");
        Assert.Equal("DoNothing", probe.Action, ignoreCase: true);
        Assert.Equal("templates/shop/daily_sales_header.png", probe.Template);
        Assert.Equal([580, 60, 300, 110], probe.Roi!);
        Assert.Equal("shopNoShopComplete", probe.OnErrorNext.Single());
        Assert.Equal("shopSelectItems", definition.GetTask("shopSelectAll").ExceededNext.Single());
        Assert.Equal("SelectShopItems", definition.GetTask("shopSelectItems").Action);
        Assert.Equal("shopConfirm", definition.GetTask("shopSelectItems").Next.Single());
        Assert.Equal("shopBack", definition.GetTask("shopSelectItems").OnErrorNext.Single());
        Assert.DoesNotContain(definition.Tasks.Keys, name => name.StartsWith("shopBuy", StringComparison.Ordinal));
        var back = definition.GetTask("shopBack");
        Assert.Equal("ClickSelf", back.Action, ignoreCase: true);
        Assert.Equal("templates/shop/back.png", back.Template);
        Assert.Equal("shopComplete", back.Next.Single());
        Assert.Equal("shopAndroidBack", back.OnErrorNext.Single());
        Assert.True(definition.GetTask("shopComplete").Success);
        Assert.True(File.Exists(Path.Combine(root, "resource", "hachimi", "pipelines", back.Template!)));
    }

    [Fact]
    public void Shop_settings_choose_ocr_when_select_all_is_off()
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

        Assert.Equal(0, overrides["shopSelectAll"]);
        Assert.Single(overrides);
        Assert.True(options.HasIndividualSelections);
        Assert.True(options.IsSelected(ShopItemCategory.StarPieces));
        Assert.False(options.IsSelected(ShopItemCategory.AlarmClock));

        var buyAll = options with { SelectAll = true };
        Assert.Empty(buyAll.ToMaxTimesOverrides());
    }

    [Fact]
    public async Task Shop_task_enters_daily_sales_and_calls_shared_shop_pipeline()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "pipelines", "shop_task.json");

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

        Assert.NotNull(definition);
        var specialShop = definition!.GetTask("specialShop");
        Assert.Equal("MatchTemplate", specialShop.Algorithm, ignoreCase: true);
        Assert.Equal("ClickSelf", specialShop.Action, ignoreCase: true);
        Assert.Equal("templates/shop/shop_home_button.png", specialShop.Template);
        Assert.Equal([160, 1200, 300, 260], specialShop.Roi!);
        Assert.Equal("homeReturn", specialShop.OnErrorNext.Single());
        var dailySales = definition.GetTask("dailySales");
        Assert.Equal("MatchTemplate", dailySales.Algorithm, ignoreCase: true);
        Assert.Equal("ClickSelf", dailySales.Action, ignoreCase: true);
        Assert.Equal("templates/shop/daily_sales.png", dailySales.Template);
        Assert.Equal([20, 580, 860, 710], dailySales.Roi!);
        Assert.Equal("homeReturn", dailySales.OnErrorNext.Single());
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
