using System.IO;
using System.Text.Json;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class DeveloperToolsCareerPackageInspectionTests
{
    [Fact]
    public void NativeInspectorRequestIncludesCurrentEditorTextAndStagedExecutionOverlays()
    {
        var resourceRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "career-inspector-test", "resource"));
        var manifestPath = Path.Combine(resourceRoot, "hachimi", "career", "sample", "manifest.json");
        var fragmentPath = Path.Combine(resourceRoot, "hachimi", "career", "sample", "execution.json");
        const string unsavedManifest = "{\"schemaVersion\":2,\"fragments\":[\"execution.json\"]}";
        const string stagedExecution = "{\"tasks\":{\"new-task\":{\"on_success\":\"done\"}}}";
        var editedManifest = new ScenarioPackageFileEditorItem(
            "manifest.json", manifestPath, "{\"schemaVersion\":1}");
        editedManifest.JsonText = unsavedManifest;

        var overlays = DeveloperToolsViewModel.BuildScenarioPackageOverlays(
            [editedManifest],
            new Dictionary<string, string> { [fragmentPath] = stagedExecution });
        using var request = JsonDocument.Parse(
            DeveloperToolsViewModel.CreateCareerPackageInspectionRequestJson(resourceRoot, overlays));

        Assert.Equal(resourceRoot, request.RootElement.GetProperty("resource_root").GetString());
        var files = request.RootElement.GetProperty("files").EnumerateArray()
            .ToDictionary(item => item.GetProperty("path").GetString()!,
                item => item.GetProperty("raw_json").GetString()!,
                StringComparer.OrdinalIgnoreCase);
        Assert.Equal(unsavedManifest, files[Path.GetFullPath(manifestPath)]);
        Assert.Equal(stagedExecution, files[Path.GetFullPath(fragmentPath)]);
    }
}
