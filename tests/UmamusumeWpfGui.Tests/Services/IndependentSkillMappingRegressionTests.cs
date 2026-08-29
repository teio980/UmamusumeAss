using System.IO;
using System.Text.Json;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class IndependentSkillMappingRegressionTests
{
    [Fact]
    public void Every_current_global_skill_keeps_an_explicit_verified_mapping()
    {
        var catalog = IndependentTrainingCatalog.Load(FindSolutionRoot());

        Assert.Equal(223, catalog.Skills.Count);
        Assert.True(catalog.SkillSource.IsExplicitGlobalClient);
        Assert.Equal(223, catalog.Skills.Count(skill => skill.IsGameSearchMapped));

        var searchTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.All(catalog.Skills, skill =>
        {
            Assert.True(skill.AvailableInGlobal, skill.SkillName);
            Assert.True(skill.SingleModeEnabled, skill.SkillName);
            Assert.True(skill.IsGameSearchMapped, skill.SkillName);
            Assert.True(skill.SearchResultRow > 0, skill.SkillName);
            Assert.False(string.IsNullOrWhiteSpace(skill.EffectiveSearchText), skill.SkillName);
            Assert.True(searchTexts.Add(skill.EffectiveSearchText),
                $"Search query is ambiguous: '{skill.EffectiveSearchText}'.");
        });
    }

    [Fact]
    public void Stable_verified_mapping_source_contains_all_current_skill_ids()
    {
        var root = FindSolutionRoot();
        var sourcePath = Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "independent_training",
            "skills.global.verified.json");

        using var document = JsonDocument.Parse(File.ReadAllText(sourcePath));
        var source = document.RootElement;
        Assert.Equal("5e44800^", source.GetProperty("sourceRevision").GetString());

        var verifiedIds = source.GetProperty("skills")
            .EnumerateArray()
            .Select(item => item.GetProperty("skillId").GetInt32())
            .ToHashSet();
        var catalog = IndependentTrainingCatalog.Load(root);

        Assert.Equal(223, verifiedIds.Count);
        Assert.True(catalog.Skills.All(skill => verifiedIds.Contains(skill.SkillId)));
    }

    [Fact]
    public void Numeric_skill_retains_the_short_verified_query_and_ocr_name()
    {
        var catalog = IndependentTrainingCatalog.Load(FindSolutionRoot());
        var skill = Assert.Single(catalog.Skills, item => item.SkillId == 201412);

        Assert.Equal("1,500,000 CC", skill.SkillName);
        Assert.Equal("500", skill.EffectiveSearchText);
        Assert.Equal("1,500,000 CC", skill.OcrTargetText);
        Assert.True(skill.IsGameSearchMapped);
        Assert.Equal(1, skill.SearchResultRow);
        Assert.Equal(0, skill.SearchResultPage);
        Assert.Equal(0, skill.SearchResultPickerRow);
    }

    [Fact]
    public void Ocr_failure_can_resolve_the_verified_checkbox_fallback_row()
    {
        var catalog = IndependentTrainingCatalog.Load(FindSolutionRoot());
        var skill = Assert.Single(catalog.Skills, item => item.SkillId == 201412);

        Assert.True(
            IndependentTrainingCatalog.TryGetVerifiedSkillFallback(
                skill,
                out var page,
                out var pickerRow));
        Assert.Equal(0, page);
        Assert.Equal(0, pickerRow);
        Assert.Equal(
            [20, 120, 150, 220],
            IndependentTrainingCatalog.GetSkillPickerCheckboxFallbackRoi(pickerRow));

        var laterRow = Assert.Single(catalog.Skills, item => item.SearchResultRow == 5);
        Assert.True(
            IndependentTrainingCatalog.TryGetVerifiedSkillFallback(
                laterRow,
                out page,
                out pickerRow));
        Assert.Equal(0, page);
        Assert.Equal(4, pickerRow);
        Assert.Equal(
            [20, 680, 150, 220],
            IndependentTrainingCatalog.GetSkillPickerCheckboxFallbackRoi(pickerRow));
    }

    [Fact]
    public async Task Skill_selection_keeps_ocr_primary_and_verified_template_fallback()
    {
        var root = FindSolutionRoot();
        var executionPath = Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "execution.json");
        var profilePath = Path.Combine(
            root,
            "resource",
            "hachimi",
            "ura",
            "screens",
            "screen_profile.json");

        var definition = await HachimiPipelineDefinitionLoader.LoadAsync(executionPath);
        Assert.NotNull(definition);

        var ocr = definition!.GetTask("independent_skills_search_checkbox_ocr");
        Assert.Equal("OcrText", ocr.Algorithm, ignoreCase: true);
        Assert.Equal("ClickText", ocr.Action, ignoreCase: true);
        Assert.Equal(0.82, ocr.FuzzyThreshold);
        Assert.Equal("line", ocr.OcrMatchMode, ignoreCase: true);

        var fallback = definition.GetTask("independent_skills_search_checkbox");
        Assert.Equal("MatchTemplate", fallback.Algorithm, ignoreCase: true);
        Assert.Equal("ClickSelf", fallback.Action, ignoreCase: true);
        Assert.Equal([20, 120, 150, 220], fallback.Roi!);
        Assert.False(string.IsNullOrWhiteSpace(fallback.Template));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(profilePath));
        var career = document.RootElement
            .GetProperty("screens")
            .EnumerateArray()
            .Single(item => item.GetProperty("screenId").GetString() == "career_final_confirmation");
        var actions = career.GetProperty("actions")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("semanticId").GetString()!,
                item => item.GetProperty("task").GetString()!,
                StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            "independent_skills_search_checkbox_ocr",
            actions["independent.skills.search.checkbox"]);
        Assert.Equal(
            "independent_skills_search_checkbox",
            actions["independent.skills.search.checkbox.fallback"]);
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
