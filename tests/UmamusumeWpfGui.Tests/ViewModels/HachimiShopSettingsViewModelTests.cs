using System.IO;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class HachimiShopSettingsViewModelTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly JsonSettingsService _settings;

    public HachimiShopSettingsViewModelTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "UmamusumeAssTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _settings = new JsonSettingsService(
            Path.Combine(_tempDirectory, "connection_settings.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    [Fact]
    public void SettingsPersistOutsideTaskQueue()
    {
        var viewModel = new HachimiShopSettingsViewModel(_settings);

        viewModel.Enabled = false;
        viewModel.BuyShoes = true;

        var loaded = _settings.Load();

        Assert.False(loaded.Hachimi.Shop.Enabled);
        Assert.True(loaded.Hachimi.Shop.BuyShoes);
        Assert.Empty(loaded.TaskQueue);
    }
}
