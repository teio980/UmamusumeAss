using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class SupportDeckPresetCatalogTests
{
    [Fact]
    public void Presets_expose_the_existing_order_labels_and_type_counts()
    {
        Assert.Equal(
            [
                "custom",
                "speed3-stamina3",
                "speed3-stamina2-wit1",
                "speed2-stamina2-power1-wit1",
                "speed2-stamina1-power1-wit1-friend1",
            ],
            SupportDeckPresetCatalog.Presets.Select(item => item.Value));

        Assert.Equal(
            [0, 6, 6, 6, 6],
            SupportDeckPresetCatalog.Presets.Select(item => item.RequiredCardCount));

        var friendPreset = SupportDeckPresetCatalog.Find(
            " SPEED2-STAMINA1-POWER1-WIT1-FRIEND1 ");
        Assert.NotNull(friendPreset);
        Assert.Equal("2 Speed / 1 Stamina / 1 Power / 1 Wit / 1 Friend", friendPreset!.Label);
        Assert.Equal(1, friendPreset.RequiredTypes["Friend"]);
    }

    [Theory]
    [InlineData("Speed", "speed")]
    [InlineData(" STAMINA ", "stamina")]
    [InlineData("Friend", "friend")]
    [InlineData("unknown", null)]
    public void Support_type_filter_keys_are_normalized(string supportType, string? expected)
    {
        Assert.Equal(expected, SupportDeckPresetCatalog.GetFilterKey(supportType));
    }

    [Fact]
    public void Deck_and_friend_legality_are_defined_by_the_catalog()
    {
        Assert.True(
            SupportDeckPresetCatalog.IsValidDeck(
                "speed3-stamina3",
                ["Speed", "Speed", "Speed", "Stamina", "Stamina", "Stamina"]));
        Assert.False(
            SupportDeckPresetCatalog.IsValidDeck(
                "speed3-stamina3",
                ["Speed", "Speed", "Stamina", "Stamina", "Stamina", "Wit"]));
        Assert.True(SupportDeckPresetCatalog.IsValidDeck("custom", ["Anything"]));

        Assert.True(SupportDeckPresetCatalog.IsValidFriendCardType("speed3-stamina3", "Speed"));
        Assert.False(SupportDeckPresetCatalog.IsValidFriendCardType("speed3-stamina3", "Power"));
        Assert.True(
            SupportDeckPresetCatalog.IsValidFriendCardType(
                "speed2-stamina1-power1-wit1-friend1",
                "Power"));
        Assert.True(
            SupportDeckPresetCatalog.IsValidFriendCardType(
                "custom",
                "Power",
                allowCustomPreset: true));
    }
}
