using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class NormalCareerSkillSettingsViewModelTests
{
    [Fact]
    public void Picker_searches_only_global_selectable_skills_and_edits_ordered_targets()
    {
        using var settings = new CareerTrainingTaskSettingsViewModel();
        var picker = settings.NormalSkills;

        Assert.False(picker.IsCareerModeActive);
        Assert.Equal(579, picker.FilteredSkills.Count);
        Assert.All(picker.FilteredSkills, option => Assert.True(option.Skill.IsSelectable));

        var first = picker.FilteredSkills[0];
        var second = picker.FilteredSkills[1];
        picker.SearchText = first.SkillId.ToString(CultureInfo.InvariantCulture);
        Assert.Contains(first, picker.FilteredSkills);
        Assert.DoesNotContain(second, picker.FilteredSkills);

        picker.SelectedAvailableSkill = first;
        Assert.True(picker.AddSelectedSkillCommand.CanExecute(null));
        picker.AddSelectedSkillCommand.Execute(null);
        Assert.False(picker.AddSelectedSkillCommand.CanExecute(null));
        picker.SelectedAvailableSkill = second;
        picker.AddSelectedSkillCommand.Execute(null);

        Assert.Equal([first.SkillId, second.SkillId], settings.ParseNormalSkillIds());
        picker.SelectedTargetSkill = second;
        Assert.True(picker.MoveSelectedSkillUpCommand.CanExecute(null));
        picker.MoveSelectedSkillUpCommand.Execute(null);
        Assert.Equal([second.SkillId, first.SkillId], settings.ParseNormalSkillIds());

        picker.RemoveSelectedSkillCommand.Execute(null);
        Assert.Equal([first.SkillId], settings.ParseNormalSkillIds());
    }

    [Fact]
    public void Normal_skill_ids_round_trip_in_priority_order_and_map_to_pipeline_contract()
    {
        using var settings = new CareerTrainingTaskSettingsViewModel
        {
            CareerMode = CareerTrainingTaskSettingsViewModel.NormalCareerMode,
            TraineeId = 1,
        };
        Assert.True(settings.NormalSkills.IsCareerModeActive);

        var skillIds = settings.NormalSkills.FilteredSkills.Take(3)
            .Select(option => option.SkillId)
            .Reverse()
            .ToArray();
        settings.NormalSkills.SkillIdsText = string.Join(",", skillIds);

        var exported = CareerTaskSettingsSerializer.Export(settings);
        Assert.Equal(
            skillIds,
            Assert.IsType<JsonArray>(exported["normalSkillIds"])
                .Select(item => item!.GetValue<int>()));

        using var imported = new CareerTrainingTaskSettingsViewModel();
        CareerTaskSettingsSerializer.Import(imported, exported);

        Assert.Equal(skillIds, imported.ParseNormalSkillIds());
        Assert.Equal(skillIds, CareerTaskSettingsMapper.ToNormalSettings(imported).EffectiveNormalSkillIds);
        Assert.True(imported.NormalSkills.IsCareerModeActive);
        imported.CareerMode = CareerTrainingTaskSettingsViewModel.IndependentCareerMode;
        Assert.False(imported.NormalSkills.IsCareerModeActive);
    }

    [Fact]
    public void Picker_is_hosted_only_as_a_normal_career_settings_view()
    {
        var viewPath = Path.Combine(
            FindSolutionRoot(),
            "src",
            "UmamusumeWpfGui",
            "Views",
            "Tasks",
            "NormalCareerSkillSettingsView.xaml");
        var view = XDocument.Load(viewPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var border = Assert.Single(view.Descendants(presentation + "Border"));
        Assert.Equal(
            "{Binding IsCareerModeActive, Converter={StaticResource BoolToVisibility}}",
            (string?)border.Attribute("Visibility"));
        Assert.Contains("{Binding FilteredSkills}", view.ToString(), StringComparison.Ordinal);
        Assert.Contains("{Binding TargetSkills}", view.ToString(), StringComparison.Ordinal);

        var basicPath = Path.Combine(
            FindSolutionRoot(),
            "src",
            "UmamusumeWpfGui",
            "Views",
            "Tasks",
            "CareerBasicSettingsView.xaml");
        var basic = XDocument.Load(basicPath);
        Assert.Contains(
            "<taskviews:NormalCareerSkillSettingsView DataContext=\"{Binding NormalSkills}\" />",
            basic.ToString(),
            StringComparison.Ordinal);
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
