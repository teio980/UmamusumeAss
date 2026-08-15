using System.IO;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraScenarioPackLoaderTests
{
    private static readonly string[] SupportedExecutionActions =
        ["ClickSelf", "ClickRect", "SelectUraTrainee"];
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
        Assert.Contains(pack.ScreenProfile.Screens, item => item.ScreenId == "career_complete");
        Assert.Equal("home", pack.ScreenProfile.Find("home")?.EntryTask);
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
            "captures/scenario_select_ura.png",
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
            "../../templates/start_game/game_home_selected.png",
            pack.ExecutionDefinition.Tasks["home"].Template);
        Assert.Equal(
            "../../templates/start_game/game_home_unselected.png",
            pack.ExecutionDefinition.Tasks["homeAlt"].Template);
        Assert.Contains("home_home_career", pack.ExecutionDefinition.Tasks["home"].Next);
        Assert.Contains("homeAlt", pack.ExecutionDefinition.Tasks["home"].OnErrorNext);
        Assert.Contains("home_home_career", pack.ExecutionDefinition.Tasks["homeAlt"].Next);
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
                "captures/ura_prelim_races_active.png",
                "captures/does-not-exist.png",
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
