using System.IO;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class UraScenarioPackLoaderTests
{
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
        Assert.All(
            pack.ExecutionDefinition.Tasks.Values,
            task => Assert.Equal("ClickSelf", task.Action));
        Assert.Contains(pack.ScreenProfile.Screens, item => item.ScreenId == "home");
        Assert.Contains(pack.ScreenProfile.Screens, item => item.ScreenId == "career_complete");
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
