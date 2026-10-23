using System.Text.Json;
using System.Text.Json.Nodes;
using Umamusume.AppCore.Platform;
using Umamusume.AppCore.Services;
using Umamusume.Avalonia.Services;
using Umamusume.Avalonia.ViewModels;
using Xunit;

namespace Umamusume.Avalonia.Tests;

public sealed class SkillCatalogTests
{
    [Fact]
    public void NullSkillPointCostsDoNotDiscardTheDatabaseCatalog()
    {
        var path = Path.Combine(PlatformPaths.GetActiveResourceDirectory(), "uma", "database", "global", "skills.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var rows = document.RootElement.GetProperty("skills").EnumerateArray().ToArray();
        var withoutCost = rows.First(row => row.GetProperty("needSkillPoint").ValueKind == JsonValueKind.Null);

        Assert.Equal(rows.Select(row => row.GetProperty("skillId").GetInt32()).Order(),
            UmaCatalogService.Skills.Select(skill => skill.SkillId).Order());
        var skill = Assert.Single(UmaCatalogService.Skills,
            item => item.SkillId == withoutCost.GetProperty("skillId").GetInt32());
        Assert.Null(skill.NeedSkillPoint);
        Assert.False(skill.IsIndependentSelectable);
        Assert.Contains(UmaCatalogService.Skills, item => item.IsIndependentSelectable);
    }

    [Fact]
    public void BothSkillPickersPopulateFromTheDatabase()
    {
        var editor = Career().SettingsEditor;

        Assert.NotEmpty(editor.RichFilteredNormalSkills);
        Assert.Equal(UmaCatalogService.Skills.Count(skill => skill.IsIndependentSelectable),
            editor.RichFilteredNormalSkills.Count);
        Assert.Equal(UmaCatalogService.Skills.Count, editor.RichFilteredIndependentSkills.Count);
        Assert.Contains(editor.RichFilteredIndependentSkills, option => option.IsExecutable);
        Assert.Contains(editor.RichFilteredIndependentSkills, option => !option.IsExecutable);
    }

    [Fact]
    public void DatabaseSkillSelectionsSurviveSearchAndReload()
    {
        var task = Career();
        var editor = task.SettingsEditor;
        Assert.NotEmpty(editor.RichFilteredNormalSkills);
        var skill = editor.RichFilteredNormalSkills[0];
        editor.NormalSkillSearchText = skill.Name;
        Assert.Contains(skill, editor.RichFilteredNormalSkills);
        editor.SelectedAvailableNormalSkill = skill;
        editor.AddSelectedNormalSkillCommand.Execute(null);

        var option = editor.RichFilteredIndependentSkills.Single(item => item.Skill.SkillId == skill.SkillId);
        option.IsSelected = true;
        editor.IndependentSkillSearchText = "__no_matching_skill__";
        Assert.Empty(editor.RichFilteredIndependentSkills);
        editor.IndependentSkillSearchText = skill.Name;
        Assert.Contains(editor.RichFilteredIndependentSkills, item => item.Skill.SkillId == skill.SkillId && item.IsSelected);

        var reloaded = Career(JsonNode.Parse(task.Settings.ToJsonString())!.AsObject()).SettingsEditor;
        Assert.Equal(skill.SkillId, Assert.Single(reloaded.TargetNormalSkills).SkillId);
        Assert.Equal(skill.SkillId, Assert.Single(reloaded.RichFilteredIndependentSkills, item => item.IsSelected).Skill.SkillId);
    }

    private static TaskQueueItemViewModel Career(JsonObject? settings = null) =>
        new(UmaTaskCatalog.Get("career-training"), true, settings ?? new());
}
