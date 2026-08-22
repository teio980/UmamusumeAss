using System.IO;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Training;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class CareerTrainingTaskSettingsViewModelTests
{
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

        Assert.Equal(CareerTrainingTaskSettingsViewModel.NormalCareerMode, settings.CareerMode);
        Assert.False(settings.IsIndependentCareer);

        settings.CareerMode = "Independent";

        Assert.Equal(CareerTrainingTaskSettingsViewModel.IndependentCareerMode, settings.CareerMode);
        Assert.True(settings.IsIndependentCareer);
        Assert.True(settings.IsIndependentTrainingSettingsValid);

        settings.CareerMode = "normal";

        Assert.Equal(CareerTrainingTaskSettingsViewModel.NormalCareerMode, settings.CareerMode);
        Assert.False(settings.IsIndependentCareer);
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
