using System.IO;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class HachimiPipelineDefinitionTests
{
    [Theory]
    [InlineData("mail_collection.json", "Home")]
    [InlineData("team_race.json", "RaceTab")]
    [InlineData("daily_race.json", "DailyProgram")]
    [InlineData("mission_collection.json", "missionIcon")]
    public async Task Ordinary_pipeline_definitions_load_with_the_shared_schema(
        string fileName,
        string expectedTask)
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", fileName);

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

        Assert.NotNull(definition);
        Assert.Equal(1, definition!.SchemaVersion);
        Assert.Contains(expectedTask, definition.Tasks.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.True(definition.ReferenceWidth > 0);
        Assert.True(definition.ReferenceHeight > 0);
    }

    [Fact]
    public async Task Team_race_can_call_the_shared_shop_pipeline()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "team_race.json");

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

        Assert.NotNull(definition);
        var trigger = definition!.GetTask("runRandomShop");
        Assert.Equal("RunPipeline", trigger.Action);
        Assert.Equal("shop.json", trigger.Pipeline);
        Assert.Equal("shopProbe", trigger.Entry);
        Assert.Contains("MiddleNext", trigger.Next);
        Assert.Contains("MiddleNext", trigger.OnErrorNext);
    }

    [Fact]
    public async Task Mission_collection_checks_each_tab_and_returns_home()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "mission_collection.json");

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

        Assert.NotNull(definition);
        Assert.Equal("templates/mission_collection/mission_icon.png", definition!.GetTask("missionIcon").Template);
        Assert.Equal("mainTab", definition.GetTask("dailyRed").OnErrorNext.Single());
        Assert.Equal("titlesTab", definition.GetTask("mainRed").OnErrorNext.Single());
        Assert.Equal("specialTab", definition.GetTask("titlesRed").OnErrorNext.Single());
        Assert.Equal("returnHome", definition.GetTask("specialRed").OnErrorNext.Single());
        Assert.True(definition.GetTask("homeVerify").Success);
    }

    [Fact]
    public async Task Team_race_uses_a_parallel_result_monitor_until_race_again()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "team_race.json");

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path);

        Assert.NotNull(definition);
        var next = definition!.GetTask("next");
        var whiteskip = definition.GetTask("whiteskip");
        var monitor = definition.GetTask("resultMonitor");

        Assert.Contains("resultMonitor", next.Next);
        Assert.Contains("resultMonitor", whiteskip.Next);
        Assert.Equal("ParallelMonitor", monitor.Algorithm);
        Assert.Equal(
            ["next", "newhighscore", "runRandomShop", "MiddleNext", "nexttwo", "noticesClose"],
            monitor.MonitorTasks);
        Assert.Equal("raceagain", monitor.SuccessTask);
        Assert.Equal("templates/shop/shop_title.png", definition.GetTask("runRandomShop").Template);
        Assert.Equal(
            "templates/start_game/notices_close.png",
            definition.GetTask("noticesClose").Template);
    }

    [Fact]
    public async Task Daily_race_enables_multi_race_and_configures_ticket_count()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "resource", "hachimi", "daily_race.json");

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
        Assert.Equal("multiRaceTicketPlus", ticketMinus.Next.Single());
        Assert.Equal("multiRaceTicketPlus", ticketMinus.OnErrorNext.Single());
        Assert.Equal("multiRaceTicketPlus", ticketMinus.ExceededNext.Single());
        Assert.Equal(6, ticketMinus.MaxTimes);
        Assert.Equal("templates/daily_race/multi_race_ticket_plus.png", ticketPlus.Template);
        Assert.Equal("multiRaceTicketPlus", ticketPlus.Next.Single());
        Assert.Equal("multiRaceTicketConfirm", ticketPlus.ExceededNext.Single());
        Assert.Equal("templates/daily_race/multi_race_ticket_confirm.png", ticketConfirm.Template);
        Assert.Equal("multiRaceComplete", ticketConfirm.Next.Single());
        Assert.Equal("templates/daily_race/multi_race_complete.png", multiRaceComplete.Template);
        Assert.Equal("multiRaceSaleProbe", multiRaceComplete.Next.Single());
        Assert.Equal("multiRaceSaleShopButton", multiRaceSaleProbe.Next.Single());
        Assert.Equal("verifyDailyRaceReturn", multiRaceSaleProbe.OnErrorNext.Single());
        Assert.Equal("runMultiRaceSaleShop", multiRaceSaleShopButton.Next.Single());
        Assert.Equal("multiRaceSaleCancel", multiRaceSaleShopButton.OnErrorNext.Single());
        Assert.Equal("shop.json", runMultiRaceSaleShop.Pipeline);
        Assert.Equal("multiRaceSaleCancel", runMultiRaceSaleShop.Next.Single());
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
        var path = Path.Combine(root, "resource", "hachimi", "start_game.json");
        var json = File.ReadAllText(path);
        var definition = System.Text.Json.JsonSerializer.Deserialize<StartGamePipelineDefinition>(json);

        Assert.NotNull(definition);
        Assert.Equal("StartupMonitor", definition!.Start);
        var monitor = definition.Tasks[definition.Start];
        Assert.Equal("StartupMonitor", monitor.Algorithm);
        Assert.Equal("CheckStartNoticeSkip", monitor.TriggerTask);
        Assert.Equal(["CheckLogoSkip"], monitor.TriggerChain);
        Assert.Contains("CheckGameHome", monitor.MonitorTasks);
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
