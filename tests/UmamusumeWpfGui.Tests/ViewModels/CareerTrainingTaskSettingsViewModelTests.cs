using System.Globalization;
using System.IO;
using System.Xml.Linq;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Training;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class CareerTrainingTaskSettingsViewModelTests
{
    [Fact]
    public void Independent_agenda_reset_clears_only_agenda_selection_and_filter()
    {
        var settings = new CareerTrainingTaskSettingsViewModel();
        var agenda = settings.IndependentRaceOptions.First(item => item.IsExecutable);
        var skill = settings.IndependentSkillOptions.First(item => item.IsExecutable);
        agenda.IsSelected = true;
        skill.IsSelected = true;
        settings.IndependentAgendaSearchText = "no matching agenda race";
        var skillIdText = skill.Skill.SkillId.ToString(CultureInfo.InvariantCulture);
        settings.IndependentSkillSearchText = skillIdText;
        var changedProperties = new List<string?>();
        settings.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        settings.ResetIndependentAgendaCommand.Execute(null);

        Assert.Equal(string.Empty, settings.IndependentAgendaSearchText);
        Assert.Empty(settings.IndependentAgendaSelectionsText);
        Assert.Equal(0, settings.SelectedIndependentAgendaCount);
        Assert.Equal(settings.IndependentRaceOptions.Count, settings.FilteredIndependentRaceOptions.Count);
        Assert.True(skill.IsSelected);
        Assert.Equal(skillIdText, settings.IndependentSkillIdsText);
        Assert.Equal(skillIdText, settings.IndependentSkillSearchText);
        Assert.Contains(nameof(settings.IndependentAgendaSelectionsText), changedProperties);
        Assert.Contains(nameof(settings.SelectedIndependentAgendaCountText), changedProperties);
    }

    [Fact]
    public void Independent_skill_reset_clears_only_skill_selection_and_filter()
    {
        var settings = new CareerTrainingTaskSettingsViewModel();
        var agenda = settings.IndependentRaceOptions.First(item => item.IsExecutable);
        var skill = settings.IndependentSkillOptions.First(item => item.IsExecutable);
        agenda.IsSelected = true;
        skill.IsSelected = true;
        settings.IndependentAgendaSearchText = agenda.Race.RaceName;
        settings.IndependentSkillSearchText = "no matching skill";
        var changedProperties = new List<string?>();
        settings.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        settings.ResetIndependentSkillsCommand.Execute(null);

        Assert.Equal(string.Empty, settings.IndependentSkillSearchText);
        Assert.Empty(settings.IndependentSkillIdsText);
        Assert.Equal(0, settings.SelectedIndependentSkillCount);
        Assert.Equal(settings.IndependentSkillOptions.Count, settings.FilteredIndependentSkillOptions.Count);
        Assert.True(agenda.IsSelected);
        Assert.Equal(agenda.Race.Key, settings.IndependentAgendaSelectionsText);
        Assert.Equal(agenda.Race.RaceName, settings.IndependentAgendaSearchText);
        Assert.Contains(nameof(settings.IndependentSkillIdsText), changedProperties);
        Assert.Contains(nameof(settings.SelectedIndependentSkillCountText), changedProperties);
    }

    [Fact]
    public void Continue_existing_career_defaults_to_delete_and_round_trips()
    {
        var settings = new CareerTrainingTaskSettingsViewModel();

        Assert.False(settings.ContinueExistingCareer);

        settings.ContinueExistingCareer = true;
        Assert.True(settings.ContinueExistingCareer);

        settings.ContinueExistingCareer = false;
        Assert.False(settings.ContinueExistingCareer);
    }

    [Fact]
    public void Career_mode_normalizes_and_preserves_independent_selection()
    {
        var settings = new CareerTrainingTaskSettingsViewModel();
        var agenda = settings.IndependentRaceOptions.First(item => item.IsExecutable);
        var skill = settings.IndependentSkillOptions.First(item => item.IsExecutable);
        var changedProperties = new List<string?>();
        settings.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        Assert.Equal(CareerTrainingTaskSettingsViewModel.NormalCareerMode, settings.CareerMode);
        Assert.False(settings.IsIndependentCareer);

        settings.CareerMode = "Independent";
        settings.IndependentLineupStrategy = "front";
        settings.IndependentTrainingFocus = "stamina";
        agenda.IsSelected = true;
        skill.IsSelected = true;

        Assert.Equal(CareerTrainingTaskSettingsViewModel.IndependentCareerMode, settings.CareerMode);
        Assert.True(settings.IsIndependentCareer);
        Assert.True(settings.IsIndependentTrainingSettingsValid);
        Assert.Contains(nameof(settings.IsIndependentCareer), changedProperties);

        settings.CareerMode = "normal";

        Assert.Equal(CareerTrainingTaskSettingsViewModel.NormalCareerMode, settings.CareerMode);
        Assert.False(settings.IsIndependentCareer);

        settings.CareerMode = "independent";

        Assert.Equal("front", settings.IndependentLineupStrategy);
        Assert.Equal("stamina", settings.IndependentTrainingFocus);
        Assert.Equal(agenda.Race.Key, settings.IndependentAgendaSelectionsText);
        Assert.Equal(skill.Skill.SkillId.ToString(CultureInfo.InvariantCulture), settings.IndependentSkillIdsText);
    }

    [Fact]
    public void Independent_train_settings_are_nested_under_career_mode_in_view()
    {
        var root = FindSolutionRoot();
        var view = XDocument.Load(Path.Combine(
            root,
            "src",
            "UmamusumeWpfGui",
            "Views",
            "Tasks",
            "CareerTrainingTaskSettingsView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var tabs = view.Descendants(presentation + "TabItem").ToArray();
        var basic = Assert.Single(tabs, tab => (string?)tab.Attribute("Header") == "Basic");
        Assert.DoesNotContain(tabs, tab => (string?)tab.Attribute("Header") == "Train");

        var trainSettings = Assert.Single(basic.Descendants(presentation + "Expander"), element =>
            (string?)element.Attribute("Header") == "Independent Training (auto) · Train settings");
        Assert.Equal("True", (string?)trainSettings.Attribute("IsExpanded"));
        Assert.Equal(
            "{Binding IsIndependentCareer, Converter={StaticResource BoolToVisibility}}",
            (string?)trainSettings.Attribute("Visibility"));

        var modePicker = Assert.Single(basic.Descendants(presentation + "ComboBox"), element =>
            (string?)element.Attribute("ItemsSource") == "{Binding CareerModes}");
        Assert.Same(modePicker.Parent, trainSettings.ElementsBeforeSelf().Last());

        foreach (var source in new[]
        {
            "IndependentLineupStrategyOptions",
            "IndependentTrainingFocusOptions",
            "FilteredIndependentRaceOptions",
            "FilteredIndependentSkillOptions",
        })
        {
            var control = Assert.Single(view.Descendants(), element =>
                (string?)element.Attribute("ItemsSource") == $"{{Binding {source}}}");
            Assert.Contains(trainSettings, control.Ancestors());
        }
    }

    [Fact]
    public void Career_settings_host_keeps_expanded_train_settings_vertically_scrollable()
    {
        var host = XDocument.Load(Path.Combine(
            FindSolutionRoot(), "src", "UmamusumeWpfGui", "Views", "GrassView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace taskViews = "clr-namespace:UmamusumeWpfGui.Views.Tasks";
        var careerView = Assert.Single(host.Descendants(taskViews + "CareerTrainingTaskSettingsView"));
        var scroll = Assert.Single(careerView.Ancestors(presentation + "ScrollViewer"));

        Assert.Equal("Auto", (string?)scroll.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("Disabled", (string?)scroll.Attribute("HorizontalScrollBarVisibility"));
    }

    [Theory]
    [InlineData("normal", "start")]
    [InlineData("independent", "independent.select_mode")]
    public void Career_mode_selects_the_expected_final_confirmation_flow(
        string careerMode,
        string expectedAction)
    {
        Assert.Equal(
            expectedAction,
            AdbCareerTrainingPipeline.ResolveCareerFinalConfirmationFirstSemanticAction(careerMode));
    }

    [Fact]
    public void Guest_support_filter_uses_selected_card_type_not_friend_card_type()
    {
        var actions = AdbCareerTrainingPipeline.BuildHighestStarFilterActions("Speed", "SSR");

        Assert.Contains("ranked.filter_speed", actions);
        Assert.Contains("ranked.filter_ssr", actions);
        Assert.DoesNotContain("ranked.filter_friend", actions);
        Assert.Equal("ranked.sort_desc", actions[actions.Count - 1]);
    }

    [Fact]
    public void Highest_star_filter_selects_ssr_and_sr_when_rarity_is_unspecified()
    {
        var actions = AdbCareerTrainingPipeline.BuildHighestStarFilterActions(
            "Speed",
            rarity: null);

        Assert.Contains("ranked.filter_ssr", actions);
        Assert.Contains("ranked.filter_sr", actions);
        Assert.DoesNotContain("ranked.filter_r", actions);
    }

    [Fact]
    public async Task Career_settings_validate_auto_selected_and_all_highest_star_presets()
    {
        var root = FindSolutionRoot();
        var database = new UmaDatabaseService();
        await database.LoadAsync(Path.Combine(root, "resource"));

        var settings = new CareerTrainingTaskSettingsViewModel(database)
        {
            ManifestPath = Path.Combine(root, "resource", "hachimi", "ura", "manifest.json"),
            TraineeId = database.Trainees.First(item => item.Available).TraineeId,
        };

        Assert.True(settings.IsValid);

        settings.StrategyId = "not-registered";
        Assert.False(settings.IsValid);
        settings.StrategyId = CareerTrainingTaskSettingsViewModel.DefaultStrategyId;

        settings.SupportDeckMode = "highest-star";
        Assert.NotEqual("custom", settings.SupportDeckPreset);
        Assert.True(settings.IsValid);
        Assert.NotEmpty(settings.FriendSupportCardOptions);

        var staminaGuest = database.SupportCards.First(
            item => item.Available
                && string.Equals(item.Type, "Stamina", StringComparison.OrdinalIgnoreCase));

        foreach (var preset in new[]
        {
            "speed3-stamina3",
            "speed3-stamina2-wit1",
            "speed2-stamina2-power1-wit1",
            "speed2-stamina1-power1-wit1-friend1",
        })
        {
        settings.SupportDeckPreset = preset;
            if (preset.EndsWith("friend1", StringComparison.OrdinalIgnoreCase))
            {
                settings.FriendSupportCardId = database.SupportCards
                    .First(item => item.Available)
                    .SupportCardId;
            }
            else
            {
                settings.FriendSupportCardId = staminaGuest.SupportCardId;
            }
            Assert.Contains(
                settings.FriendSupportCardOptions,
                option => option.SupportCardId == settings.FriendSupportCardId);
            Assert.True(settings.IsValid);
        }

        settings.SupportDeckPreset = "speed3-stamina3";
        settings.FriendSupportCardId = null;
        Assert.True(settings.IsValid);

        var fiveCards = database.SupportCards
            .Where(item => item.Available)
            .Take(5)
            .Select(item => item.SupportCardId);
        settings.SupportDeckMode = "selected";
        settings.SupportDeckPreset = "custom";
        settings.SupportCardIdsText = string.Join(",", fiveCards);
        Assert.True(settings.IsFriendSupportCardSettingEnabled);
        Assert.True(settings.IsValid);

        settings.SupportCardIdsText = "1,2,3,4";
        Assert.False(settings.IsValid);
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
