using System.IO;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraScenarioPackLoaderTests
{
    private static readonly string[] SupportedExecutionActions =
        [
            "ClickSelf",
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
    private static readonly int[] TraineeHeaderRoi = [0, 190, 280, 80];
    private static readonly int[] ScenarioNextCardRect = [815, 650, 75, 180];

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
        Assert.All(
            pack.ExecutionDefinition.Tasks.Values,
            task => Assert.Contains(
                task.Action,
                SupportedExecutionActions));
        Assert.Contains(pack.ScreenProfile.Screens, item => item.ScreenId == "home");
        Assert.Contains(pack.ScreenProfile.Screens, item => item.ScreenId == "career_continue");
        Assert.Contains(pack.ScreenProfile.Screens, item => item.ScreenId == "career_complete");
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
        Assert.Equal(
            "templates/trainee_select_header.png",
            pack.ScreenProfile.Find("trainee_select")?.Recognition.Template);
        Assert.Equal(
            TraineeHeaderRoi,
            pack.ScreenProfile.Find("trainee_select")?.Recognition.Roi);
        Assert.Equal(
            0.88,
            pack.ScreenProfile.Find("trainee_select")?.Recognition.TemplateThreshold);
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
