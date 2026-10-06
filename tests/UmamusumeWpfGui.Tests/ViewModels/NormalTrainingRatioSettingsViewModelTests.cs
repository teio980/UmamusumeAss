using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class NormalTrainingRatioSettingsViewModelTests
{
    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("101")]
    [InlineData("")]
    public void Ratio_rejects_non_integer_or_out_of_range_input(string value)
    {
        using var settings = new CareerTrainingTaskSettingsViewModel();
        settings.NormalTrainingRatio.SpeedText = value;

        Assert.False(settings.NormalTrainingRatio.IsValid);
        Assert.Equal("—", settings.NormalTrainingRatio.SpeedPercentage);
    }

    [Fact]
    public void Ratio_inputs_expose_percentages_and_a_reduced_cycle_preview()
    {
        using var settings = new CareerTrainingTaskSettingsViewModel
        {
            CareerMode = CareerTrainingTaskSettingsViewModel.NormalCareerMode,
            NormalTrainingStrategy = "custom-ratio",
        };
        settings.NormalTrainingRatio.SpeedText = "4";
        settings.NormalTrainingRatio.StaminaText = "2";
        settings.NormalTrainingRatio.PowerText = "0";
        settings.NormalTrainingRatio.GutsText = "0";
        settings.NormalTrainingRatio.WitText = "2";

        Assert.True(settings.NormalTrainingRatio.IsValid);
        Assert.Equal("50%", settings.NormalTrainingRatio.SpeedPercentage);
        Assert.Equal("25%", settings.NormalTrainingRatio.StaminaPercentage);
        Assert.Equal("Speed → Speed → Stamina → Wit", settings.NormalTrainingRatio.CyclePreview);
        Assert.True(settings.NormalTrainingRatio.IsValid);
    }

    [Fact]
    public void Serialization_preserves_explicit_zero_ratio_values()
    {
        using var settings = new CareerTrainingTaskSettingsViewModel();
        settings.NormalTrainingRatio.SpeedText = "2";
        settings.NormalTrainingRatio.StaminaText = "1";
        settings.NormalTrainingRatio.PowerText = "0";
        settings.NormalTrainingRatio.GutsText = "0";
        settings.NormalTrainingRatio.WitText = "0";

        var exported = CareerTaskSettingsSerializer.Export(settings);
        var ratio = exported["normalTrainingRatio"]!.AsObject();
        Assert.Equal(0, ratio["power"]!.GetValue<int>());
        Assert.Equal(0, ratio["guts"]!.GetValue<int>());
        Assert.Equal(0, ratio["wit"]!.GetValue<int>());

        using var imported = new CareerTrainingTaskSettingsViewModel();
        CareerTaskSettingsSerializer.Import(imported, exported);
        Assert.Equal("0", imported.NormalTrainingRatio.PowerText);
        Assert.Equal("0", imported.NormalTrainingRatio.GutsText);
        Assert.Equal("0", imported.NormalTrainingRatio.WitText);
    }

    [Fact]
    public void All_zero_ratio_is_invalid_for_custom_strategy()
    {
        using var settings = new CareerTrainingTaskSettingsViewModel
        {
            CareerMode = CareerTrainingTaskSettingsViewModel.NormalCareerMode,
            NormalTrainingStrategy = "custom-ratio",
        };
        settings.NormalTrainingRatio.SpeedText = "0";
        settings.NormalTrainingRatio.StaminaText = "0";
        settings.NormalTrainingRatio.PowerText = "0";
        settings.NormalTrainingRatio.GutsText = "0";
        settings.NormalTrainingRatio.WitText = "0";

        Assert.False(settings.NormalTrainingRatio.IsValid);
        Assert.False(settings.IsValid);
    }
}
