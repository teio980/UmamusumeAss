using System.IO;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CareerVisualPackageLoaderTests
{
    [Fact]
    public async Task Duplicate_source_relative_template_paths_keep_each_screen_and_task_origin()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"career-visual-alias-{Guid.NewGuid():N}");
        var sharedRoot = Path.Combine(tempRoot, "resource", "hachimi");
        var scenarioRoot = Path.Combine(sharedRoot, "ura");
        var firstProfile = Path.Combine(sharedRoot, "career", "profiles", "first");
        var secondProfile = Path.Combine(sharedRoot, "career", "profiles", "second");
        var firstExecution = Path.Combine(sharedRoot, "career", "execution", "first");
        var secondExecution = Path.Combine(sharedRoot, "career", "execution", "second");

        try
        {
            await WriteFragmentAsync(firstProfile, "screen_first", "task_first", "first profile");
            await WriteFragmentAsync(secondProfile, "screen_second", "task_second", "second profile");
            await WriteExecutionFragmentAsync(firstExecution, "task_first", "first task");
            await WriteExecutionFragmentAsync(secondExecution, "task_second", "second task");

            var catalogPath = Path.Combine(sharedRoot, "career", "catalog.json");
            Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
            await File.WriteAllTextAsync(catalogPath, """
                {
                  "schemaVersion": 1,
                  "assets": {},
                  "regions": {},
                  "collections": {},
                  "aliases": {}
                }
                """);
            Directory.CreateDirectory(scenarioRoot);
            var manifestPath = Path.Combine(scenarioRoot, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, """
                {
                  "$schema": "uma.scenario.manifest.v2",
                  "schemaVersion": 2,
                  "scenarioId": "ura",
                  "screens": [
                    "../career/profiles/first/profile.json",
                    "../career/profiles/second/profile.json"
                  ],
                  "execution": [
                    "../career/execution/first/execution.json",
                    "../career/execution/second/execution.json"
                  ],
                  "resourceCatalog": "../career/catalog.json"
                }
                """);

            var package = await CareerVisualPackageLoader.LoadAsync(manifestPath);
            var firstScreen = package.ScreenProfile.Find("screen_first")!;
            var secondScreen = package.ScreenProfile.Find("screen_second")!;
            var firstScreenTemplate = package.Resources.ResolveScreenTemplate(
                firstScreen,
                firstScreen.Recognition.Template!);
            var secondScreenTemplate = package.Resources.ResolveScreenTemplate(
                secondScreen,
                secondScreen.Recognition.Template!);
            var firstTaskTemplate = package.Resources.ResolveTaskTemplate("task_first");
            var secondTaskTemplate = package.Resources.ResolveTaskTemplate("task_second");

            Assert.Equal(Path.Combine(firstProfile, "templates", "confirm.png"), firstScreenTemplate);
            Assert.Equal(Path.Combine(secondProfile, "templates", "confirm.png"), secondScreenTemplate);
            Assert.Equal(Path.Combine(firstExecution, "templates", "confirm.png"), firstTaskTemplate);
            Assert.Equal(Path.Combine(secondExecution, "templates", "confirm.png"), secondTaskTemplate);
            Assert.Equal("first profile", await File.ReadAllTextAsync(firstScreenTemplate));
            Assert.Equal("second profile", await File.ReadAllTextAsync(secondScreenTemplate));
            Assert.Equal("first task", await File.ReadAllTextAsync(firstTaskTemplate));
            Assert.Equal("second task", await File.ReadAllTextAsync(secondTaskTemplate));
            Assert.Throws<InvalidDataException>(() => package.Resources.ResolveVisualResource("templates/confirm.png"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static async Task WriteFragmentAsync(
        string directory,
        string screenId,
        string taskId,
        string marker)
    {
        var templatePath = Path.Combine(directory, "templates", "confirm.png");
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        await File.WriteAllTextAsync(templatePath, marker);
        await File.WriteAllTextAsync(Path.Combine(directory, "templates", "claw_credit.png"), "credit");
        await File.WriteAllTextAsync(Path.Combine(directory, "profile.json"), $$"""
            {
              "$schema": "uma.screen.profile.fragment.v1",
              "profileId": "alias-collision-test",
              "scenarioId": "ura",
              "referenceWidth": 900,
              "referenceHeight": 1600,
              "clawMachine": { "creditLabelTemplate": "templates/claw_credit.png" },
              "screens": [
                {
                  "screenId": "{{screenId}}",
                  "flow": "entry",
                  "order": 1,
                  "recognition": {
                    "template": "templates/confirm.png",
                    "roi": [0, 0, 10, 10]
                  },
                  "actions": [
                    { "semanticId": "confirm", "task": "{{taskId}}" }
                  ]
                }
              ]
            }
            """);
    }

    private static async Task WriteExecutionFragmentAsync(
        string directory,
        string taskId,
        string marker)
    {
        var templatePath = Path.Combine(directory, "templates", "confirm.png");
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        await File.WriteAllTextAsync(templatePath, marker);
        await File.WriteAllTextAsync(Path.Combine(directory, "execution.json"), $$"""
            {
              "name": "alias collision test",
              "schemaVersion": 1,
              "referenceWidth": 900,
              "referenceHeight": 1600,
              "tasks": {
                "{{taskId}}": {
                  "algorithm": "MatchTemplate",
                  "action": "ClickSelf",
                  "template": "templates/confirm.png",
                  "roi": [0, 0, 10, 10]
                }
              }
            }
            """);
    }
}
