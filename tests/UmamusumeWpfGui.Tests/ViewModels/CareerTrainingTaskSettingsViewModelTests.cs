using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class CareerTrainingTaskSettingsViewModelTests
{
    [Fact]
    public void Run_count_defaults_to_one_and_round_trips_through_task_settings()
    {
        using var settings = new CareerTrainingTaskSettingsViewModel();
        Assert.Equal(1, settings.RunCount);
        settings.RunCountText = " 5 ";

        var exported = CareerTaskSettingsSerializer.Export(settings);
        using var imported = new CareerTrainingTaskSettingsViewModel();
        CareerTaskSettingsSerializer.Import(imported, exported);

        Assert.Equal(5, imported.RunCount);
        Assert.Equal("5", imported.RunCountText);
        CareerTaskSettingsSerializer.Import(imported, new JsonObject());
        Assert.Equal(1, imported.RunCount);
    }

    [Theory]
    [InlineData("{\"runCount\":0}")]
    [InlineData("{\"runCount\":-3}")]
    [InlineData("{\"runCount\":\"invalid\"}")]
    [InlineData("{\"runCount\":null}")]
    public void Invalid_persisted_run_count_falls_back_to_one(string json)
    {
        using var settings = new CareerTrainingTaskSettingsViewModel();

        CareerTaskSettingsSerializer.Import(settings, JsonNode.Parse(json)!.AsObject());

        Assert.Equal(1, settings.RunCount);
    }

    [Fact]
    public void Full_skill_catalog_exposes_gourmand_in_the_existing_picker()
    {
        using var settings = new CareerTrainingTaskSettingsViewModel(CreateDatabase());
        var gourmand = Assert.Single(settings.IndependentSkillOptions,
            option => option.SkillId == 201351);

        Assert.True(gourmand.IsExecutable);
        Assert.Contains("180 SP", gourmand.Label);
        settings.IndependentSkillSearchText = "Gourmand";
        Assert.Contains(gourmand, settings.FilteredIndependentSkillOptions);
        gourmand.IsSelected = true;
        Assert.Equal("201351", settings.IndependentSkillIdsText);
    }

    [Fact]
    public void Independent_agenda_reset_clears_only_agenda_selection_and_filter()
    {
        using var settings = new CareerTrainingTaskSettingsViewModel(CreateDatabase());
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
        using var settings = new CareerTrainingTaskSettingsViewModel(CreateDatabase());
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
    public void Independent_agenda_month_filter_uses_the_schedule_month()
    {
        using var settings = new CareerTrainingTaskSettingsViewModel(CreateDatabase());
        var rescheduledRace = Assert.Single(settings.IndependentRaceOptions);

        Assert.Equal(6, rescheduledRace.Race.Month);
        Assert.Contains(settings.IndependentAgendaMonthOptions, option => option.Value == "6");
        Assert.Contains(settings.IndependentAgendaTurnOptions, option => option.Value == "07_01");
        Assert.Contains(settings.IndependentAgendaSurfaceOptions, option => option.Value == "Turf");
        Assert.Equal(
            ["all", "Sprint"],
            settings.IndependentAgendaDistanceOptions.Select(option => option.Value));

        settings.IndependentAgendaMonthFilter = "6";
        settings.IndependentAgendaSurfaceFilter = "Turf";
        settings.IndependentAgendaDistanceFilter = "Sprint";

        Assert.NotEmpty(settings.FilteredIndependentRaceOptions);
        Assert.All(
            settings.FilteredIndependentRaceOptions,
            option =>
            {
                Assert.Equal(6, option.Race.Month);
                Assert.Equal("Turf", option.Race.Surface);
                Assert.Equal("Sprint", option.Race.DistanceCategory);
            });

        settings.ResetIndependentAgendaCommand.Execute(null);

        Assert.Equal(IndependentTrainingSettingsViewModel.AllAgendaMonths, settings.IndependentAgendaMonthFilter);
        Assert.Equal(IndependentTrainingSettingsViewModel.AllAgendaFilters, settings.IndependentAgendaSurfaceFilter);
        Assert.Equal(IndependentTrainingSettingsViewModel.AllAgendaFilters, settings.IndependentAgendaDistanceFilter);
        Assert.Equal(settings.IndependentRaceOptions.Count, settings.FilteredIndependentRaceOptions.Count);
    }

    [Fact]
    public void Delete_existing_career_data_defaults_to_resume_and_round_trips()
    {
        using var settings = new CareerTrainingTaskSettingsViewModel(CreateDatabase());

        Assert.False(settings.DeleteExistingCareerData);

        settings.DeleteExistingCareerData = true;
        Assert.True(settings.DeleteExistingCareerData);

        settings.DeleteExistingCareerData = false;
        Assert.False(settings.DeleteExistingCareerData);
    }

    [Fact]
    public void Career_settings_migrate_legacy_resume_flag_without_flipping_behavior()
    {
        using var settings = new CareerTrainingTaskSettingsViewModel(CreateDatabase());

        CareerTaskSettingsSerializer.Import(
            settings,
            new JsonObject { ["continueExistingCareer"] = true });
        Assert.False(settings.DeleteExistingCareerData);

        CareerTaskSettingsSerializer.Import(
            settings,
            new JsonObject { ["continueExistingCareer"] = false });
        Assert.True(settings.DeleteExistingCareerData);
    }

    [Fact]
    public void Career_settings_export_writes_delete_flag_and_legacy_inverse()
    {
        var settings = new CareerTrainingTaskSettingsViewModel
        {
            DeleteExistingCareerData = true,
        };

        var exported = CareerTaskSettingsSerializer.Export(settings);

        Assert.True(exported["deleteExistingCareerData"]!.GetValue<bool>());
        Assert.False(exported["continueExistingCareer"]!.GetValue<bool>());
    }

    [Fact]
    public void Auto_restore_tp_defaults_to_false_and_round_trips()
    {
        using var settings = new CareerTrainingTaskSettingsViewModel(CreateDatabase());

        Assert.False(settings.AutoRestoreTp);

        settings.AutoRestoreTp = true;
        Assert.True(settings.AutoRestoreTp);

        var exported = CareerTaskSettingsSerializer.Export(settings);
        Assert.True(exported["autoRestoreTp"]!.GetValue<bool>());

        using var imported = new CareerTrainingTaskSettingsViewModel(CreateDatabase());
        CareerTaskSettingsSerializer.Import(imported, exported);
        Assert.True(imported.AutoRestoreTp);
    }

    [Fact]
    public void Career_mode_normalizes_and_preserves_independent_selection()
    {
        using var settings = new CareerTrainingTaskSettingsViewModel(CreateDatabase());
        var agenda = settings.IndependentRaceOptions.First(item => item.IsExecutable);
        var skill = settings.IndependentSkillOptions.First(item => item.IsExecutable);
        var changedProperties = new List<string?>();
        settings.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        Assert.Equal(CareerTrainingTaskSettingsViewModel.IndependentCareerMode, settings.CareerMode);
        Assert.True(settings.IsIndependentCareer);

        settings.CareerMode = "Independent";
        settings.IndependentLineupStrategy = "front";
        settings.IndependentTrainingFocus = "stamina";
        agenda.IsSelected = true;
        skill.IsSelected = true;

        Assert.Equal(CareerTrainingTaskSettingsViewModel.IndependentCareerMode, settings.CareerMode);
        Assert.True(settings.IsIndependentCareer);
        Assert.True(settings.IsIndependentTrainingSettingsValid);
        Assert.Contains(nameof(settings.IsIndependentTrainingSettingsValid), changedProperties);

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
    public void Independent_train_settings_use_a_separate_mode_specific_agenda_tab()
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
        XNamespace wpfui = "http://schemas.lepo.co/wpfui/2022/xaml";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var tabs = view.Descendants(presentation + "TabItem").ToArray();
        var basic = Assert.Single(tabs, tab =>
            (string?)tab.Attribute("Header") == "{DynamicResource GrassCareerBasicTab}");
        var agenda = Assert.Single(tabs, tab =>
            (string?)tab.Attribute("Header") == "{DynamicResource GrassCareerAgendaTab}");
        Assert.Equal(
            "{Binding IsIndependentCareer, Converter={StaticResource BoolToVisibility}}",
            (string?)agenda.Attribute("Visibility"));
        Assert.Contains("IndependentTrainingSettingsView", agenda.ToString(), StringComparison.Ordinal);

        Assert.Contains("CareerBasicSettingsView", basic.ToString(), StringComparison.Ordinal);
        var basicSettingsFile = XDocument.Load(Path.Combine(
            root,
            "src",
            "UmamusumeWpfGui",
            "Views",
            "Tasks",
            "CareerBasicSettingsView.xaml"));
        var basicFile = XDocument.Load(Path.Combine(
            root,
            "src",
            "UmamusumeWpfGui",
            "Views",
            "Tasks",
            "IndependentTrainingSettingsView.xaml"));
        var trainSettings = Assert.Single(basicFile.Descendants(presentation + "Expander"), element =>
            (string?)element.Attribute("Header") == "Independent Training (auto) · Train settings");
        Assert.Equal("True", (string?)trainSettings.Attribute("IsExpanded"));
        Assert.Equal(
            "{Binding IsCareerModeActive, Converter={StaticResource BoolToVisibility}}",
            (string?)trainSettings.Attribute("Visibility"));

        Assert.DoesNotContain(
            "RestartIndependentTraining",
            view.ToString(),
            StringComparison.Ordinal);

        var modePicker = Assert.Single(basicSettingsFile.Descendants(presentation + "ComboBox"), element =>
            (string?)element.Attribute("ItemsSource") == "{Binding CareerModes}");
        Assert.NotNull(modePicker);

        foreach (var source in new[]
        {
            "LineupStrategyOptions",
            "TrainingFocusOptions",
            "AgendaYearOptions",
            "AgendaMonthOptions",
            "AgendaTurnOptions",
            "AgendaDayOptions",
            "AgendaTimeOptions",
            "AgendaHalfOptions",
            "AgendaSurfaceOptions",
            "AgendaDistanceOptions",
            "AgendaTrackOptions",
            "AgendaDirectionOptions",
            "AgendaGradeOptions",
            "FilteredIndependentRaceOptions",
            "FilteredIndependentSkillOptions",
        })
        {
            var control = Assert.Single(basicFile.Descendants(), element =>
                (string?)element.Attribute("ItemsSource") == $"{{Binding {source}}}");
            Assert.Contains(trainSettings, control.Ancestors());
        }

        var agendaMonthPicker = Assert.Single(basicFile.Descendants(presentation + "ComboBox"), element =>
            (string?)element.Attribute("ItemsSource") == "{Binding AgendaMonthOptions}");
        Assert.Equal(
            "{Binding AgendaMonthFilter, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}",
            (string?)agendaMonthPicker.Attribute("SelectedValue"));

        var filterButton = Assert.Single(basicFile.Descendants(wpfui + "Button"), element =>
            (string?)element.Attribute(xaml + "Name") == "AgendaFilterButton");
        Assert.Equal("36", (string?)filterButton.Attribute("Height"));

        var selectionLists = basicFile.Descendants(presentation + "ListBox").ToArray();
        Assert.Equal(2, selectionLists.Length);
        Assert.All(selectionLists, list => Assert.Equal("280", (string?)list.Attribute("Height")));
    }

    [Fact]
    public void Career_settings_host_keeps_expanded_train_settings_vertically_scrollable()
    {
        var host = XDocument.Load(Path.Combine(
            FindSolutionRoot(), "src", "UmamusumeWpfGui", "Views", "HachimiTaskSettingsView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace taskViews = "clr-namespace:UmamusumeWpfGui.Views.Tasks";
        Assert.Single(host.Descendants(taskViews + "CareerTrainingTaskSettingsView"));
        var scroll = Assert.Single(host.Descendants(presentation + "ScrollViewer"));

        Assert.Equal("Auto", (string?)scroll.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("Disabled", (string?)scroll.Attribute("HorizontalScrollBarVisibility"));
    }

    [Fact]
    public void Career_settings_validate_against_core_catalog_projections()
    {
        var root = FindSolutionRoot();
        var database = CreateDatabase();

        using var settings = new CareerTrainingTaskSettingsViewModel(database)
        {
            ManifestPath = Path.Combine(root, "resource", "hachimi", "ura", "manifest.json"),
            TraineeId = database.Trainees.First(item => item.Available).TraineeId,
        };

        Assert.True(settings.IsValid);

        settings.StrategyId = "not-registered";
        Assert.True(settings.IsValid);
        settings.CareerMode = CareerTrainingTaskSettingsViewModel.NormalCareerMode;
        Assert.False(settings.IsValid);
        settings.StrategyId = "native-default";
        Assert.Contains(settings.NormalTrainingStrategyOptions, item => item.Value == "native-default");
        Assert.True(settings.IsValid);
        settings.CareerMode = CareerTrainingTaskSettingsViewModel.IndependentCareerMode;

        settings.SupportDeckMode = "highest-star";
        Assert.NotEqual("custom", settings.SupportDeckPreset);
        Assert.True(settings.IsValid);
        Assert.NotEmpty(settings.FriendSupportCardOptions);

        foreach (var preset in new[]
        {
            "speed3-stamina3",
            "speed3-stamina2-wit1",
            "speed2-stamina2-power1-wit1",
            "speed2-stamina1-power1-wit1-friend1",
        })
        {
            settings.SupportDeckPreset = preset;
            Assert.True(settings.IsValid);
        }

        settings.SupportDeckPreset = "speed3-stamina3";
        settings.FriendSupportCardId = null;
        Assert.True(settings.IsValid);

        var sixCards = database.SupportCards
            .Where(item => item.Type.Equals("Speed", StringComparison.OrdinalIgnoreCase)).Take(3)
            .Concat(database.SupportCards
                .Where(item => item.Type.Equals("Stamina", StringComparison.OrdinalIgnoreCase)).Take(3))
            .Select(item => item.SupportCardId);
        settings.SupportDeckMode = "selected";
        settings.SupportDeckPreset = "speed3-stamina3";
        settings.SupportCardIdsText = string.Join(",", sixCards);
        Assert.True(settings.IsFriendSupportCardSettingEnabled);
        Assert.True(settings.IsValid);

        settings.SupportCardIdsText = "1,2,3,4";
        Assert.False(settings.IsValid);
    }

    [Fact]
    public void Normal_training_strategy_options_are_projected_from_core_catalog()
    {
        using var settings = new CareerTrainingTaskSettingsViewModel(CreateDatabase());

        Assert.Contains(
            settings.NormalTrainingStrategyOptions,
            item => item.Value == "native-default" && item.Label == "Native default strategy");

        settings.NormalTrainingStrategy = "native-default";

        Assert.Equal("native-default", settings.StrategyId);
        Assert.Equal("native-default", settings.NormalTrainingStrategy);
    }

    [Fact]
    public void Normal_event_handling_defaults_to_default_and_round_trips()
    {
        var settings = new CareerTrainingTaskSettingsViewModel();

        Assert.Equal(CareerTrainingTaskSettingsViewModel.DefaultEventHandling, settings.NormalEventHandling);
        Assert.Single(settings.NormalEventHandlingOptions);
        Assert.Equal(CareerTrainingTaskSettingsViewModel.DefaultEventHandling, settings.NormalEventHandlingOptions[0].Value);

        settings.NormalEventHandling = "unsupported-future-mode";
        Assert.Equal(CareerTrainingTaskSettingsViewModel.DefaultEventHandling, settings.NormalEventHandling);

        var exported = CareerTaskSettingsSerializer.Export(settings);
        Assert.Equal(
            CareerTrainingTaskSettingsViewModel.DefaultEventHandling,
            exported["normalEventHandling"]!.GetValue<string>());

        settings.NormalEventHandling = "future-mode";
        CareerTaskSettingsSerializer.Import(
            settings,
            new JsonObject { ["normalEventHandling"] = CareerTrainingTaskSettingsViewModel.DefaultEventHandling });
        Assert.Equal(CareerTrainingTaskSettingsViewModel.DefaultEventHandling, settings.NormalEventHandling);
    }

    [Fact]
    public void Support_deck_settings_use_catalog_rules_for_selected_cards()
    {
        var database = CreateDatabase();

        var speed = database.SupportCards
            .Where(item => item.Available && item.Type.Equals("Speed", StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToArray();
        var stamina = database.SupportCards
            .Where(item => item.Available && item.Type.Equals("Stamina", StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToArray();
        Assert.Equal(3, speed.Length);
        Assert.Equal(3, stamina.Length);

        using var settings = new CareerTrainingTaskSettingsViewModel(database)
        {
            SupportDeckMode = "selected",
            SupportDeckPreset = "speed3-stamina3",
            SupportCardIdsText = string.Join(",", speed.Concat(stamina).Select(item => item.SupportCardId)),
        };

        var validTypes = speed.Concat(stamina).Select(item => item.Type).ToArray();
        Assert.Equal(
            SupportDeckPresetCatalog.IsValidDeck(settings.SupportDeckPreset, validTypes),
            settings.IsSupportDeckValid);

        var power = database.SupportCards.First(item => item.Type.Equals("Power", StringComparison.OrdinalIgnoreCase));
        var invalidTypes = speed.Skip(1).Concat(stamina).Append(
            power)
            .Select(item => item.Type)
            .ToArray();
        settings.SupportCardIdsText = string.Join(",", speed.Skip(1).Concat(stamina).Select(item => item.SupportCardId)
            .Append(power.SupportCardId));

        Assert.Equal(
            SupportDeckPresetCatalog.IsValidDeck(settings.SupportDeckPreset, invalidTypes),
            settings.IsSupportDeckValid);
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

    private static UmaDatabaseService CreateDatabase()
    {
        var catalog = CoreCatalog.Empty with
        {
            Characters = Rows("""
                [{"trainee_id":1,"base_character_id":1,"name_en":"Test Trainee","name_jp":"テスト","rarity":3,"strategies":["front"],"available":true}]
                """),
            IndependentRaces = Rows("""
                [{"raceName":"CBC Sho","grade":"G3","year":"Second Year","turn":"07_01","type":"Turf","location":"Tokyo ⇒","length":"Sprint","lengthM":"1200m","raceId":1001,"sourceOrder":0,"gameTrack":"Tokyo","gameDistance":1200,"gameGround":"Turf","gameAvailable":true,"gameSchedules":[{"month":6,"day":1,"timeName":"Junior"}],"gameOrder":1}]
                """),
            Skills = Rows("""
                [
                  {"skillId":201351,"skillName":"Gourmand","originalName":"Gourmand","rarity":2,"gradeValue":1,"skillCategory":"Recovery","effectSummary":"Recover stamina","needSkillPoint":180,"activationCondition":"Late","iconId":1,"searchText":"Gourmand","searchResultRow":1,"gameSearchMapped":false,"aliases":[],"availableInGlobal":true,"availabilitySource":"fixture","singleModeEnabled":true,"skillCategoryId":1,"otherTiers":""},
                  {"skillId":201412,"skillName":"1,500,000 CC","originalName":"Coins","rarity":2,"gradeValue":1,"skillCategory":"Unique","effectSummary":"Speed up","needSkillPoint":180,"activationCondition":"Late","iconId":2,"searchText":"500","searchResultRow":5,"gameSearchMapped":true,"aliases":["coins"],"availableInGlobal":true,"availabilitySource":"fixture","singleModeEnabled":true,"skillCategoryId":2,"otherTiers":""}
                ]
                """),
            SupportCards = Rows("""
                [
                  {"support_card_id":1,"name_en":"Speed A","rarity":"SSR","type":"Speed","available":true},
                  {"support_card_id":2,"name_en":"Speed B","rarity":"SSR","type":"Speed","available":true},
                  {"support_card_id":3,"name_en":"Speed C","rarity":"SSR","type":"Speed","available":true},
                  {"support_card_id":4,"name_en":"Stamina A","rarity":"SSR","type":"Stamina","available":true},
                  {"support_card_id":5,"name_en":"Stamina B","rarity":"SSR","type":"Stamina","available":true},
                  {"support_card_id":6,"name_en":"Stamina C","rarity":"SSR","type":"Stamina","available":true},
                  {"support_card_id":7,"name_en":"Power A","rarity":"SSR","type":"Power","available":true},
                  {"support_card_id":8,"name_en":"Wit A","rarity":"SSR","type":"Wit","available":true},
                  {"support_card_id":9,"name_en":"Friend A","rarity":"SSR","type":"Friend","available":true}
                ]
                """),
            Strategies = Rows("""
                [{"id":"native-default","value":"native-default","label":"Native default strategy","name":"Native default strategy","scenario":"ura"}]
                """),
        };
        var database = new UmaDatabaseService();
        database.LoadFromCoreCatalog(catalog, Path.Combine(FindSolutionRoot(), "resource"));
        return database;
    }

    private static JsonElement[] Rows(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(row => row.Clone()).ToArray();
    }
}
