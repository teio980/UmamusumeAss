using System.IO;
using System.Text.Json;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class SupportDeckSettingsViewModelTests
{
    [Fact]
    public void Highest_star_supports_custom_counts_and_reloading_the_shared_profile_value()
    {
        using var settings = new SupportDeckSettingsViewModel { SupportDeckMode = "highest-star" };
        Assert.Single(settings.SupportDeckPresets);
        Assert.Equal("by-type", settings.SelectedSupportDeckPresetValue);
        Assert.True(settings.IsSupportDeckValid);
        settings.SupportStaminaCount = 0;
        Assert.False(settings.IsSupportDeckValid);
        settings.SupportPowerCount = 3;
        Assert.Equal("types-speed3-power3", settings.SupportDeckPreset);
        Assert.True(settings.IsSupportDeckValid);

        using var loaded = new SupportDeckSettingsViewModel
        {
            SupportDeckMode = "highest-star",
            SupportDeckPreset = settings.SupportDeckPreset,
        };
        Assert.Equal(3, loaded.SupportSpeedCount);
        Assert.Equal(3, loaded.SupportPowerCount);
        Assert.Equal(0, loaded.SupportStaminaCount);
        Assert.Equal(6, loaded.SupportTypeTotal);
        Assert.True(loaded.IsSupportDeckValid);
        loaded.SupportDeckMode = "auto";
        Assert.False(loaded.IsSupportTypeComposition);
        loaded.SupportDeckMode = "highest-star";
        Assert.Equal(3, loaded.SupportPowerCount);
    }

    [Theory]
    [InlineData("speed3-stamina3", 3, 3, 0)]
    [InlineData("speed3-stamina2-wit1", 3, 2, 0)]
    [InlineData("speed2-stamina2-power1-wit1", 2, 2, 0)]
    [InlineData("speed2-stamina1-power1-wit1-friend1", 2, 1, 1)]
    public void Legacy_presets_load_as_editable_counts(string preset, int speed, int stamina, int friend)
    {
        using var settings = new SupportDeckSettingsViewModel { SupportDeckMode = "highest-star", SupportDeckPreset = preset };
        Assert.Equal(speed, settings.SupportSpeedCount);
        Assert.Equal(stamina, settings.SupportStaminaCount);
        Assert.Equal(friend, settings.SupportFriendCount);
        Assert.Equal("by-type", settings.SelectedSupportDeckPresetValue);
        Assert.True(settings.IsSupportDeckValid);
    }

    [Fact]
    public void Borrowed_card_must_match_the_requested_composition()
    {
        var database = new UmaDatabaseService();
        var cards = JsonSerializer.Deserialize<JsonElement[]>("""
            [{"support_card_id":1,"type":"Speed","available":true},
             {"support_card_id":2,"type":"Power","available":true}]
            """)!;
        database.LoadFromCoreCatalog(CoreCatalog.Empty with { SupportCards = cards }, Path.GetTempPath());
        using var settings = new SupportDeckSettingsViewModel(database)
        {
            SupportDeckMode = "highest-star",
            SupportDeckPreset = "types-power5-friend1",
            FriendSupportCardId = 1,
        };
        Assert.False(settings.IsSupportDeckValid);
        settings.FriendSupportCardId = 2;
        Assert.True(settings.IsSupportDeckValid);
    }
}
