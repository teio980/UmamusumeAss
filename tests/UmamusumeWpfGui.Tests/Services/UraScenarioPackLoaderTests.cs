using System.IO;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraScenarioPackLoaderTests
{
    private static readonly string[] SupportedExecutionActions =
        [
            "ClickSelf",
            "ClickSelfUntilTransition",
            "ClickRect",
            "ClickText",
            "FindText",
            "SelectUraTrainee",
            "JustReturn",
            "Swipe",
            "Wait",
            "SelectUraLegacy",
            "Input",
            "KeyEvent",
            "Stop",
        ];
    private static readonly int[] ScenarioHeaderRoi = [0, 190, 430, 80];
    private static readonly int[] ScenarioStartupHeaderRoi = [620, 165, 205, 29];
    private static readonly int[] TraineeHeaderRoi = [0, 190, 280, 80];
    private static readonly int[] CareerMainHeaderRoi = [0, 0, 120, 50];
    private static readonly int[] ScenarioNextCardRect = [815, 650, 75, 180];
    private static readonly int[] RaceListEntryRect = [30, 840, 840, 220];
    private static readonly int[] RaceListStartRect = [270, 1300, 360, 120];

    [Fact]
    public async Task LoadAsync_LoadsCheckedInUraPack()
    {
        var root = FindWorkspaceRoot();
        var manifest = Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "manifest.json");

        var pack = await UraScenarioPackLoader.LoadAsync(manifest);

        Assert.Equal("ura", pack.Manifest.ScenarioId);
        Assert.Equal("ura", pack.Definition.ScenarioId);
        Assert.Equal("ura", pack.Objectives.ScenarioId);
        Assert.Equal("ura", pack.Races.ScenarioId);
        Assert.Equal("ura", pack.Events.ScenarioId);
        Assert.Equal("ura", pack.ScreenProfile.ScenarioId);
        Assert.NotEmpty(pack.ExecutionDefinition.Tasks);
        Assert.Equal(
            "SelectUraTrainee",
            pack.ExecutionDefinition.Tasks["trainee_select_pick"].Action);
        Assert.Equal(
            15,
            pack.ExecutionDefinition.Tasks["trainee_select_pick"].SearchRois.Count);
        var speedEntry = pack.ExecutionDefinition.Tasks["training_selection_training_speed"];
        var speedClick = pack.ExecutionDefinition.Tasks["training_selection_speed_first_click"];
        Assert.Equal("JustReturn", speedEntry.Action);
        Assert.Equal([20, 1260, 180, 90], speedEntry.Roi!);
        Assert.Equal("ClickSelfUntilTransition", speedClick.Action);
        Assert.Equal([20, 1160, 180, 125], speedClick.FallbackRoi!);
        Assert.Equal(2, speedClick.TransitionTemplates.Count);
        Assert.All(
            pack.ExecutionDefinition.Tasks.Values,
            task => Assert.Contains(
                task.Action,
                SupportedExecutionActions));
        Assert.Contains(pack.ScreenProfile.Screens, item => item.ScreenId == "home");
        Assert.Contains(pack.ScreenProfile.Screens, item => item.ScreenId == "career_continue");
        Assert.Contains(pack.ScreenProfile.Screens, item => item.ScreenId == "career_complete");
        var raceRunner = pack.ScreenProfile.Find("race_runner");
        Assert.NotNull(raceRunner);
        Assert.Equal(
            "templates/career/race/race_runner_label.png",
            raceRunner!.Recognition.Template);
        Assert.Equal([700, 1060, 180, 140], raceRunner.Recognition.Roi!);
        Assert.Equal(0.88, raceRunner.Recognition.TemplateThreshold);
        Assert.Equal(
            "race_runner_strategy_apply_pace",
            raceRunner.FindAction("strategy.apply.pace")?.Task);
        Assert.Equal(
            "race_runner_strategy_apply_front",
            raceRunner.FindAction("strategy.apply.front")?.Task);
        Assert.Equal(
            "race_runner_entry_start",
            raceRunner.FindAction("entry.start")?.Task);
        var racePlaybackStartScreen = pack.ScreenProfile.Find("race_playback_start");
        Assert.NotNull(racePlaybackStartScreen);
        Assert.Equal(
            "templates/career/race/race_playback_start.png",
            racePlaybackStartScreen.Recognition.Template);
        Assert.Equal(
            [230, 1380, 450, 180],
            racePlaybackStartScreen.Recognition.Roi!);
        Assert.Equal(
            "race_runner_playback_start",
            racePlaybackStartScreen.FindAction("race.play")?.Task);
        Assert.Equal(
            "templates/career/race/race_strategy_selected_marker.png",
            pack.ExecutionDefinition.Tasks["race_runner_strategy_apply_pace"].Template);
        Assert.Equal(
            "MatchTemplateColor",
            pack.ExecutionDefinition.Tasks["race_runner_strategy_apply_pace"].Algorithm);
        Assert.Contains(
            "race_runner_strategy_change_pace",
            pack.ExecutionDefinition.Tasks["race_runner_strategy_apply_pace"].OnErrorNext);
        Assert.Equal(
            "templates/career/race/race_strategy_change.png",
            pack.ExecutionDefinition.Tasks["race_runner_strategy_change_pace"].Template);
        Assert.Equal(
            "templates/independent/strategy_confirm.png",
            pack.ExecutionDefinition.Tasks["race_runner_strategy_save"].Template);
        Assert.Equal(
            "templates/career/race/race_entry_race.png",
            pack.ExecutionDefinition.Tasks["race_runner_entry_start"].Template);
        Assert.Equal(
            "templates/career/race/race_playback_ok.png",
            pack.ExecutionDefinition.Tasks["race_runner_playback_ok"].Template);
        Assert.Equal(
            "templates/career/race/race_playback_start.png",
            pack.ExecutionDefinition.Tasks["race_runner_playback_start"].Template);
        var racePlaybackStart = pack.ExecutionDefinition.Tasks["race_runner_playback_start"];
        Assert.Equal("ClickSelfUntilTransition", racePlaybackStart.Action);
        Assert.Equal([230, 1380, 450, 180], racePlaybackStart.FallbackRoi!);
        Assert.Equal(
            ["templates/career/race/race_playback_skip.png"],
            racePlaybackStart.TransitionTemplates);
        Assert.Equal([630, 1440, 170, 150], racePlaybackStart.TransitionRoi!);
        Assert.Equal(0.82, racePlaybackStart.TransitionThreshold);
        Assert.Equal(
            "templates/career/race/race_result_replay.png",
            pack.ExecutionDefinition.Tasks["race_runner_replay_probe"].Template);
        Assert.Equal(
            [680, 520, 210, 150],
            pack.ExecutionDefinition.Tasks["race_runner_replay_probe"].Roi!);
        Assert.Contains(
            "race_runner_playback_skip",
            pack.ExecutionDefinition.Tasks["race_runner_replay_probe"].OnErrorNext);
        Assert.Equal(
            120,
            pack.ExecutionDefinition.Tasks["race_runner_playback_skip"].MaxTimes);
        Assert.Equal(
            "templates/career/race/race_result_next.png",
            pack.ExecutionDefinition.Tasks["race_runner_result_next"].Template);
        Assert.Equal(
            "templates/career/race/race_last_next.png",
            pack.ExecutionDefinition.Tasks["race_runner_last_next"].Template);
        Assert.True(File.Exists(Path.Combine(
            FindWorkspaceRoot(),
            "resource",
            "hachimi",
            "ura",
            "screens",
            raceRunner.Recognition.Template!)));
        Assert.Equal("home", pack.ScreenProfile.Find("home")?.EntryTask);
        Assert.Equal(
            "templates/career_continue_header.png",
            pack.ScreenProfile.Find("career_continue")?.Recognition.Template);
        Assert.Equal(
            "career_continue_resume",
            pack.ScreenProfile.Find("career_continue")?.FindAction("resume")?.Task);
        Assert.Equal(
            "career_continue_delete",
            pack.ScreenProfile.Find("career_continue")?.FindAction("delete")?.Task);
        Assert.Equal(
            "templates/career_continue_resume.png",
            pack.ExecutionDefinition.Tasks["career_continue_resume"].Template);
        Assert.Equal(
            "templates/career_continue_delete.png",
            pack.ExecutionDefinition.Tasks["career_continue_delete"].Template);
        Assert.Equal(
            "templates/career_continue_delete_confirm.png",
            pack.ExecutionDefinition.Tasks["career_continue_delete_confirm"].Template);
        Assert.Contains(
            "career_continue_delete_confirm",
            pack.ExecutionDefinition.Tasks["career_continue_delete"].Next);
        Assert.Equal(
            "support_select_support_display_settings",
            pack.ScreenProfile.Find("support_select")?.FindAction("ranked.display_settings")?.Task);
        Assert.Equal(
            "support_select_support_sort_level",
            pack.ScreenProfile.Find("support_select")?.FindAction("ranked.sort_level")?.Task);
        Assert.Equal(
            "support_select_support_sort_apply",
            pack.ScreenProfile.Find("support_select")?.FindAction("ranked.sort_apply")?.Task);
        Assert.Equal(
            "templates/scenario_select_header.png",
            pack.ScreenProfile.Find("scenario_select")?.Recognition.Template);
        Assert.Equal(
            ScenarioHeaderRoi,
            pack.ScreenProfile.Find("scenario_select")?.Recognition.Roi);
        Assert.Equal(
            0.95,
            pack.ScreenProfile.Find("scenario_select")?.Recognition.TemplateThreshold);
        var scenarioStartup = pack.ScreenProfile.Find("normal_scenario_select_startup");
        Assert.Equal(
            "templates/normal/scenario_record_header.png",
            scenarioStartup?.Recognition.Template);
        Assert.Equal(ScenarioStartupHeaderRoi, scenarioStartup?.Recognition.Roi);
        Assert.Equal(0.88, scenarioStartup?.Recognition.TemplateThreshold);
        Assert.Single(scenarioStartup?.Templates ?? []);
        Assert.True(File.Exists(Path.Combine(
            FindWorkspaceRoot(),
            "resource",
            "hachimi",
            "ura",
            "screens",
            scenarioStartup!.Recognition.Template!)));
        var traineeStartup = pack.ScreenProfile.Find("normal_trainee_select_startup");
        Assert.Equal(
            "templates/normal/trainee_select_career_info.png",
            traineeStartup?.Recognition.Template);
        Assert.Equal([576, 510, 145, 32], traineeStartup!.Recognition.Roi!);
        Assert.Single(traineeStartup?.Templates ?? []);
        Assert.Equal(
            "templates/trainee_select_header.png",
            pack.ScreenProfile.Find("trainee_select")?.Recognition.Template);
        Assert.Equal(
            TraineeHeaderRoi,
            pack.ScreenProfile.Find("trainee_select")?.Recognition.Roi);
        Assert.Equal(
            0.88,
            pack.ScreenProfile.Find("trainee_select")?.Recognition.TemplateThreshold);
        var careerMain = pack.ScreenProfile.Find("career_main");
        Assert.Equal(
            "templates/career_main_header.png",
            careerMain?.Recognition.Template);
        Assert.Equal(CareerMainHeaderRoi, careerMain!.Recognition.Roi!);
        Assert.Equal(0.92, careerMain?.Recognition.TemplateThreshold);
        Assert.Single(careerMain?.Templates ?? []);
        Assert.True(File.Exists(Path.Combine(
            FindWorkspaceRoot(),
            "resource",
            "hachimi",
            "ura",
            "screens",
            careerMain!.Recognition.Template!)));
        var trainingSelection = pack.ScreenProfile.Find("training_selection");
        Assert.Equal(
            "templates/training_selection_header.png",
            trainingSelection?.Recognition.Template);
        Assert.Equal([0, 0, 120, 50], trainingSelection!.Recognition.Roi!);
        Assert.Equal(0.92, trainingSelection?.Recognition.TemplateThreshold);
        Assert.True(File.Exists(Path.Combine(
            FindWorkspaceRoot(),
            "resource",
            "hachimi",
            "ura",
            "screens",
            trainingSelection!.Recognition.Template!)));
        Assert.Contains(
            "templates/runtime_frames/scenario_select_ura.png",
            pack.ScreenProfile.Find("scenario_select")?.Recognition.AlternativeTemplates
                ?? []);
        Assert.Equal("ura", pack.ScreenProfile.ScenarioSelection?.ScenarioId);
        Assert.Equal(
            "templates/scenario_select_ura_card.png",
            pack.ScreenProfile.ScenarioSelection?.Recognition.Template);
        Assert.Equal(
            "ClickRect",
            pack.ExecutionDefinition.Tasks["scenario_select_scenario_next_card"].Action);
        Assert.Equal(
            ScenarioNextCardRect,
            pack.ExecutionDefinition.Tasks["scenario_select_scenario_next_card"].SpecificRect);
        Assert.Equal(
            "../../pipelines/templates/start_game/game_home_selected.png",
            pack.ExecutionDefinition.Tasks["home"].Template);
        Assert.Equal(
            "../../pipelines/templates/start_game/game_home_unselected.png",
            pack.ExecutionDefinition.Tasks["homeAlt"].Template);
        Assert.Equal(10_000, pack.ExecutionDefinition.Tasks["home"].TimeoutMilliseconds);
        Assert.Equal(2, pack.ExecutionDefinition.Tasks["home"].RetryTimes);
        Assert.Equal(15_000, pack.ExecutionDefinition.Tasks["career_main_action_training"].TimeoutMilliseconds);
        Assert.Contains(
            "career_main_action_recover",
            pack.ExecutionDefinition.Tasks["career_main_action_training"].OnErrorNext);
        Assert.Contains("home_home_career", pack.ExecutionDefinition.Tasks["home"].Next);
        Assert.Contains("homeAlt", pack.ExecutionDefinition.Tasks["home"].OnErrorNext);
        Assert.Contains("home_home_career", pack.ExecutionDefinition.Tasks["homeAlt"].Next);
        Assert.Equal(
            "templates/home_home_career.png",
            pack.ExecutionDefinition.Tasks["home_home_career"].Template);
        Assert.Contains(
            "home_home_career_active",
            pack.ExecutionDefinition.Tasks["home_home_career"].OnErrorNext);
        Assert.Equal(
            "templates/home_home_career_active_label.png",
            pack.ExecutionDefinition.Tasks["home_home_career_active"].Template);
        Assert.Equal(
            "ClickSelf",
            pack.ExecutionDefinition.Tasks["home_home_career_active"].Action);

        var recommendedEntry = pack.ExecutionDefinition.Tasks["race_list_race_fans_entry"];
        Assert.Equal("ClickRect", recommendedEntry.Action);
        Assert.Equal(RaceListEntryRect, recommendedEntry.SpecificRect);
        Assert.Contains("race_list_race_start", recommendedEntry.Next);

        var raceListStart = pack.ExecutionDefinition.Tasks["race_list_race_start"];
        Assert.Equal("ClickRect", raceListStart.Action);
        Assert.Equal(RaceListStartRect, raceListStart.SpecificRect);
        Assert.True(raceListStart.Success);

        var goalEntry = pack.ExecutionDefinition.Tasks["race_list_race_goal_entry"];
        Assert.Contains("race_list_race_start", goalEntry.Next);
    }

    [Fact]
    public async Task ScenarioPackageLoader_LoadsCareerExecutionWithoutUraSpecificValidation()
    {
        var root = FindWorkspaceRoot();
        var manifest = Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "manifest.json");

        var package = await ScenarioPackageLoader.LoadExecutionAsync(manifest);

        Assert.Equal("ura", package.ScenarioId);
        Assert.Equal("URA Finale", package.DisplayName);
        Assert.EndsWith(
            Path.Combine("screens", "execution.json"),
            package.ExecutionPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("career-training-execution", package.ExecutionDefinition.Name);
    }

    [Fact]
    public async Task LoadAsync_RejectsMissingTemplateReference()
    {
        var root = FindWorkspaceRoot();
        var sourcePack = Path.Combine(
            root, "resource", "hachimi", "ura");
        var tempRoot = Path.Combine(Path.GetTempPath(), "ura-loader-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(sourcePack, tempRoot);
        try
        {
            var manifestPath = Path.Combine(tempRoot, "manifest.json");
            var profilePath = Path.Combine(tempRoot, "screens", "screen_profile.json");
            var profile = await File.ReadAllTextAsync(profilePath);
            profile = profile.Replace(
                "templates/runtime_frames/ura_prelim_races_active.png",
                "templates/runtime_frames/does-not-exist.png",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(profilePath, profile);

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                UraScenarioPackLoader.LoadAsync(manifestPath));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "resource", "hachimi", "ura", "manifest.json")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository workspace.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
