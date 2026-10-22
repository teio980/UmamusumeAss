using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class SupportDeckPresetCatalogTests
{
    [Theory]
    [InlineData("types-speed3-power3", true)]
    [InlineData("types-speed6", true)]
    [InlineData("types-speed0-power6", true)]
    [InlineData("types-guts3-wit2-friend1", true)]
    [InlineData("types-speed3-power2", false)]
    [InlineData("types-speed6-power1", false)]
    [InlineData("types-speed3-speed3", false)]
    [InlineData("types-unknown6", false)]
    public void Custom_compositions_require_six_cards(string preset, bool expected)
    {
        var types = SupportDeckPresetCatalog.GetRequiredTypes(preset);
        var cards = types?.SelectMany(pair => Enumerable.Repeat(pair.Key, pair.Value)) ?? [];
        Assert.Equal(expected, SupportDeckPresetCatalog.IsValidDeck(preset, cards));
    }

    [Fact]
    public void Friend_type_is_not_a_wildcard_in_custom_compositions()
    {
        Assert.False(SupportDeckPresetCatalog.IsValidFriendCardType("types-power5-friend1", "Speed"));
        Assert.True(SupportDeckPresetCatalog.IsValidFriendCardType("types-power5-friend1", "Power"));
        Assert.True(SupportDeckPresetCatalog.IsValidFriendCardType("types-power5-friend1", "Friend"));
    }

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
