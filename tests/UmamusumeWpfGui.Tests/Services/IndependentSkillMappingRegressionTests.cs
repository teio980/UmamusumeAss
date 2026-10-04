using System.IO;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class IndependentSkillMappingRegressionTests
{
    [Fact]
    public void Complete_catalog_keeps_all_skill_rows_and_previous_verified_mappings()
    {
        var catalog = IndependentTrainingCatalog.Load(CareerTestResourceResolver.FindWorkspaceRoot());

        Assert.Equal(725, catalog.Skills.Count);
        Assert.Equal("global", catalog.SkillSource.Region);
        Assert.Equal("unofficial-community-skill-reference", catalog.SkillSource.SourceType);
        Assert.Equal(725, catalog.Skills.Select(skill => skill.SkillId).Distinct().Count());
        Assert.Equal(579, catalog.Skills.Count(skill => skill.IsSelectable));
        Assert.Equal(223, catalog.Skills.Count(skill => skill.IsGameSearchMapped));

        var searchTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.All(catalog.Skills.Where(skill => skill.IsGameSearchMapped), skill =>
        {
            Assert.True(skill.AvailableInGlobal, skill.SkillName);
            Assert.True(skill.SingleModeEnabled, skill.SkillName);
            Assert.True(skill.SearchResultRow > 0, skill.SkillName);
            Assert.False(string.IsNullOrWhiteSpace(skill.EffectiveSearchText), skill.SkillName);
            Assert.True(searchTexts.Add(skill.EffectiveSearchText),
                $"Search query is ambiguous: '{skill.EffectiveSearchText}'.");
        });
    }

    [Fact]
    public void Complete_catalog_contains_gourmand_and_marks_unverified_searches()
    {
        var catalog = IndependentTrainingCatalog.Load(CareerTestResourceResolver.FindWorkspaceRoot());
        var gourmand = Assert.Single(catalog.Skills, skill => skill.SkillId == 201351);

        Assert.Equal("Gourmand", gourmand.SkillName);
        Assert.Equal(180, gourmand.NeedSkillPoint);
        Assert.Equal(2, gourmand.Rarity);
        Assert.True(gourmand.IsSelectable);
        Assert.Equal("Gourmand", gourmand.EffectiveSearchText);
        Assert.False(gourmand.IsGameSearchMapped);
        Assert.False(IndependentTrainingCatalog.TryGetVerifiedSkillFallback(
            gourmand, out _, out _));
    }

    [Fact]
    public void Numeric_skill_retains_the_short_verified_query_and_ocr_name()
    {
        var catalog = IndependentTrainingCatalog.Load(CareerTestResourceResolver.FindWorkspaceRoot());
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
        var catalog = IndependentTrainingCatalog.Load(CareerTestResourceResolver.FindWorkspaceRoot());
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
        var pack = await CareerTestResourceResolver.LoadBuiltInUraPackAsync();
        var definition = pack.ExecutionDefinition;

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

        var career = pack.ScreenProfile.Find("career_final_confirmation");
        Assert.NotNull(career);
        var actions = career!.Actions.ToDictionary(
            item => item.SemanticId,
            item => item.Task,
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            "independent_skills_search_checkbox_ocr",
            actions["independent.skills.search.checkbox"]);
        Assert.Equal(
            "independent_skills_search_checkbox",
            actions["independent.skills.search.checkbox.fallback"]);
    }

}
