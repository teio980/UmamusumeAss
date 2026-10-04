using System.IO;
using System.Text.Json;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class HachimiResourceReferenceTests
{
    private static readonly string[] OrdinaryDefinitions =
    [
        HachimiResourcePaths.DailyRaceDefinition,
        HachimiResourcePaths.MailCollectionDefinition,
        HachimiResourcePaths.MissionCollectionDefinition,
        HachimiResourcePaths.ShopDefinition,
        HachimiResourcePaths.ShopTaskDefinition,
        HachimiResourcePaths.StartGameDefinition,
        HachimiResourcePaths.TeamRaceDefinition,
    ];

    [Theory]
    [InlineData("resource/hachimi/daily_race.json", HachimiResourcePaths.DailyRaceDefinition)]
    [InlineData("resource\\hachimi\\mail_collection.json", HachimiResourcePaths.MailCollectionDefinition)]
    [InlineData("resource/hachimi/mission_collection.json", HachimiResourcePaths.MissionCollectionDefinition)]
    [InlineData("resource/hachimi/team_race.json", HachimiResourcePaths.TeamRaceDefinition)]
    public void Legacy_builtin_definition_paths_migrate_to_the_pipeline_directory(
        string legacyPath,
        string expectedPath)
    {
        Assert.Equal(expectedPath, HachimiResourcePaths.MigrateLegacyDefinitionPath(legacyPath));
    }

    [Fact]
    public void Custom_definition_paths_are_not_rewritten()
    {
        const string customPath = "custom/pipelines/daily_race.json";
        const string absoluteCustomPath = @" C:\custom\resource\hachimi\daily_race.json ";

        Assert.Equal(customPath, HachimiResourcePaths.MigrateLegacyDefinitionPath(customPath));
        Assert.Equal(
            absoluteCustomPath,
            HachimiResourcePaths.MigrateLegacyDefinitionPath(absoluteCustomPath));
    }

    [Fact]
    public void Ordinary_definitions_and_nested_pipeline_references_exist()
    {
        var root = FindSolutionRoot();
        foreach (var relativeDefinitionPath in OrdinaryDefinitions)
        {
            var definitionPath = Path.Combine(
                root,
                relativeDefinitionPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(definitionPath), definitionPath);

            using var document = JsonDocument.Parse(File.ReadAllText(definitionPath));
            var baseDirectory = Path.GetDirectoryName(definitionPath)!;
            foreach (var reference in EnumerateResourceStrings(document.RootElement))
            {
                var path = Path.GetFullPath(Path.Combine(
                    baseDirectory,
                    reference.Replace('/', Path.DirectorySeparatorChar)));
                Assert.True(
                    File.Exists(path),
                    $"'{relativeDefinitionPath}' references missing '{reference}' (resolved to '{path}').");
            }
        }
    }

    [Fact]
    public async Task Ura_package_and_all_runtime_template_families_exist()
    {
        var root = FindSolutionRoot();
        var manifestPath = Path.Combine(
            root,
            HachimiResourcePaths.UraManifest.Replace('/', Path.DirectorySeparatorChar));
        var pack = await UraScenarioPackLoader.LoadAsync(manifestPath);

        Assert.NotNull(pack.VisualResources);
        var legacyTemplates = Path.Combine(
            Path.GetDirectoryName(pack.ManifestPath)!,
            "..",
            "career",
            "entry",
            "templates",
            "legacy");
        Assert.True(Directory.EnumerateFiles(legacyTemplates).Any());
        Assert.True(pack.VisualResources!.DynamicCollections.ContainsKey("independent.race_card"));
        Assert.True(pack.VisualResources.DynamicCollections.ContainsKey("career.runtime_frame"));
        Assert.True(Directory.EnumerateFiles(
            pack.VisualResources.DynamicCollections["independent.race_card"].RootDirectory).Any());

        foreach (var screen in pack.ScreenProfile.Screens)
        {
            foreach (var reference in screen.Templates)
            {
                var path = UraScenarioResourceResolver.Resolve(pack, screen, reference);
                Assert.True(File.Exists(path), $"Screen '{screen.ScreenId}' references missing '{reference}'.");
            }
        }

        foreach (var reference in pack.Races.Races
                     .Select(race => race.ObservedOutcome?.Capture)
                     .Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            Assert.False(reference!.StartsWith("testdata/", StringComparison.OrdinalIgnoreCase), reference);
            var path = UraScenarioResourceResolver.Resolve(pack, reference!);
            Assert.True(File.Exists(path), path);
            var runtimeFrameId = $"career.runtime_frame:{Path.GetFileNameWithoutExtension(reference)}";
            var runtimeFramePath = pack.VisualResources!.ResolveVisualResource(runtimeFrameId);
            var runtimeFrameRoot = pack.VisualResources.DynamicCollections["career.runtime_frame"].RootDirectory;
            Assert.Equal(
                Path.GetFullPath(Path.Combine(runtimeFrameRoot, Path.GetFileName(reference!))),
                Path.GetFullPath(runtimeFramePath));
            Assert.True(
                File.Exists(runtimeFramePath),
                runtimeFrameId);
        }

        foreach (var task in pack.ExecutionDefinition.Tasks.Keys)
        {
            if (pack.VisualResources.TaskSourceFiles.ContainsKey(task)
                && !string.IsNullOrWhiteSpace(pack.VisualResources.GetOriginalTaskTemplate(task)))
            {
                Assert.True(File.Exists(pack.VisualResources.ResolveTaskTemplate(task)), task);
            }
            foreach (var transition in pack.VisualResources.GetResolvedTaskTransitionTemplates(task))
                Assert.True(File.Exists(transition), task);
        }
    }

    private static IEnumerable<string> EnumerateResourceStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
            {
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value)
                    && (value.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                        || value.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                        || value.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                        || value.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                        || value.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                {
                    yield return value;
                }

                yield break;
            }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var value in EnumerateResourceStrings(item))
                        yield return value;
                }

                yield break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var value in EnumerateResourceStrings(property.Value))
                        yield return value;
                }

                yield break;
        }
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

        throw new InvalidOperationException(
            $"Could not locate solution root from {AppContext.BaseDirectory}");
    }
}
