using System.IO;
using System.Text.Json.Nodes;
using UmamusumeWpfGui.Services.Training;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerVisualPackageTests
{
    [Fact]
    public async Task Legacy_career_manifest_reports_the_fragment_migration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "career-legacy-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var manifest = Path.Combine(directory, "manifest.json");
            await File.WriteAllTextAsync(manifest, "{\"schemaVersion\":1}");

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => CareerVisualPackageLoader.LoadAsync(manifest));

            Assert.Contains("schemaVersion 1", exception.Message, StringComparison.Ordinal);
            Assert.Contains("ordered 'screens' and 'execution' fragment arrays", exception.Message, StringComparison.Ordinal);
            Assert.Contains("ordinary pipeline schemaVersion 1 remains supported", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task New_task_template_is_saved_relative_to_its_source_fragment()
    {
        var package = await LoadBuiltInPackageAsync();
        var template = package.VisualResources!.ResolveVisualResource(
            "career.entry.trainee.display_button");
        var definition = package.ExecutionDefinition;
        var taskJson = new JsonObject
        {
            ["template"] = Path.GetRelativePath(definition.BaseDirectory, template)
                .Replace(Path.DirectorySeparatorChar, '/'),
            ["transitionTemplates"] = new JsonArray(),
        };

        DeveloperToolsViewModel.PreserveSourceRelativeTemplatePaths(
            taskJson,
            "new_editor_task",
            package.ExecutionPath,
            definition,
            package.VisualResources);

        Assert.Equal(
            "templates/trainee/runner_display_button.png",
            taskJson["template"]!.GetValue<string>());
        Assert.False(package.VisualResources.TryGetResolvedTaskTemplate(
            "new_editor_task",
            out _));
    }

    [Fact]
    public async Task Task_editor_preserves_business_outcomes_across_clone_and_save_models()
    {
        var package = await LoadBuiltInPackageAsync();
        var task = package.ExecutionDefinition.Tasks["race_runner_last_next"];
        var editor = HachimiPipelineTaskEditorItem.FromTask("race_runner_last_next", task);
        var clone = editor.Clone();
        var saved = clone.ToTask();

        Assert.Equal("race.replay.completed", editor.Outcome);
        Assert.Equal("race.replay.completed", clone.Outcome);
        Assert.Equal("race.replay.completed", saved.Outcome);

        var dynamicCardTask = package.ExecutionDefinition.Tasks["independent_agenda_race_card_find"];
        var dynamicCardEditor = HachimiPipelineTaskEditorItem.FromTask(
            "independent_agenda_race_card_find",
            dynamicCardTask);
        var dynamicCardSaved = dynamicCardEditor.Clone().ToTask();
        Assert.Equal("independent.race_card", dynamicCardEditor.TemplateCollection);
        Assert.Equal("independent.race_card", dynamicCardSaved.TemplateCollection);
    }

    [Fact]
    public async Task Unknown_dynamic_template_collection_is_rejected_with_task_diagnostic()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "career-unknown-template-collection-" + Guid.NewGuid().ToString("N"));
        var scenarioRoot = Path.Combine(root, "ura");
        var sharedRoot = Path.Combine(root, "career");
        Directory.CreateDirectory(scenarioRoot);
        Directory.CreateDirectory(Path.Combine(sharedRoot, "templates"));
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(scenarioRoot, "manifest.json"),
                """
                {
                  "schemaVersion": 2,
                  "scenarioId": "test",
                  "screens": ["../career/profile.json"],
                  "execution": ["../career/execution.json"],
                  "resourceCatalog": "../career/catalog.json"
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(sharedRoot, "profile.json"),
                """
                {
                  "$schema": "uma.screen.profile.fragment.v1",
                  "profileId": "test",
                  "scenarioId": "test",
                  "referenceWidth": 900,
                  "referenceHeight": 1600,
                  "clawMachine": { "creditLabelTemplate": "templates/claw_credit.png" },
                  "screens": [{
                    "screenId": "test_screen",
                    "flow": "entry",
                    "order": 0,
                    "recognition": { "template": "templates/screen.png", "priority": 20 }
                  }]
                }
                """);
            await File.WriteAllBytesAsync(Path.Combine(sharedRoot, "templates", "screen.png"), []);
            await File.WriteAllBytesAsync(Path.Combine(sharedRoot, "templates", "claw_credit.png"), []);
            await File.WriteAllTextAsync(
                Path.Combine(sharedRoot, "execution.json"),
                """
                {
                  "name": "test",
                  "schemaVersion": 1,
                  "referenceWidth": 900,
                  "referenceHeight": 1600,
                  "tasks": {
                    "dynamic_card": {
                      "algorithm": "MatchTemplateScaled",
                      "action": "ClickSelf",
                      "templateCollection": "missing.race_card",
                      "roi": [0, 0, 100, 100]
                    }
                  }
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(sharedRoot, "catalog.json"),
                """
                {
                  "schemaVersion": 1,
                  "assets": {},
                  "regions": {},
                  "collections": {}
                }
                """);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                CareerVisualPackageLoader.LoadAsync(Path.Combine(scenarioRoot, "manifest.json")));
            Assert.Contains("dynamic_card", exception.Message, StringComparison.Ordinal);
            Assert.Contains("templateCollection", exception.Message, StringComparison.Ordinal);
            Assert.Contains("missing.race_card", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static Task<ScenarioExecutionPackage> LoadBuiltInPackageAsync()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var manifest = Path.Combine(
                directory.FullName,
                "resource",
                "hachimi",
                "ura",
                "manifest.json");
            if (File.Exists(manifest))
                return ScenarioPackageLoader.LoadExecutionAsync(manifest);
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository's URA manifest.");
    }
}
