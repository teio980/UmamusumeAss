using System.IO;
using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class JsonSettingsServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public JsonSettingsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "UmamusumeAssTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "connection_settings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ================================================================
    // Load — defaults when file missing
    // ================================================================

    [Fact]
    public void Load_WhenFileMissing_ReturnsDefaults()
    {
        var service = new JsonSettingsService(_filePath);
        var settings = service.Load();

        Assert.Equal("", settings.AdbPath);
        Assert.True(settings.AutoDetectConnection);
        Assert.Equal("General", settings.ConnectConfig);
        Assert.Equal("en-US", settings.Language);
        Assert.Empty(settings.ConnectAddressHistory);
    }

    // ================================================================
    // Load — malformed JSON
    // ================================================================

    [Fact]
    public void Load_WhenFileMalformed_ReturnsDefaults()
    {
        File.WriteAllText(_filePath, "not valid json{{{");
        var service = new JsonSettingsService(_filePath);
        var settings = service.Load();

        Assert.Equal("", settings.AdbPath);
        Assert.True(settings.AutoDetectConnection);
    }

    [Fact]
    public void Load_WhenFileEmpty_ReturnsDefaults()
    {
        File.WriteAllText(_filePath, "");
        var service = new JsonSettingsService(_filePath);
        var settings = service.Load();

        Assert.Equal("", settings.AdbPath);
        Assert.True(settings.AutoDetectConnection);
    }

    // ================================================================
    // Save + Load roundtrip
    // ================================================================

    [Fact]
    public void SaveThenLoad_RoundtripsAllProperties()
    {
        var service = new JsonSettingsService(_filePath);

        var original = new ConnectionSettings
        {
            AdbPath = @"D:\tools\adb.exe",
            ConnectAddress = "192.168.1.50:5555",
            AutoDetectConnection = false,
            AlwaysAutoDetectConnection = true,
            ConnectConfig = "General",
            Language = "zh-CN",
            TargetActivityName = "com.umamusume.app/com.example.MainActivity",
        };
        original.AddAddressToHistory("10.0.0.1:5555");
        original.AddAddressToHistory("10.0.0.2:5555");
        original.TargetPackageIds.Add("com.umamusume.app");
        original.TaskQueue.Add(new GrassTaskCacheItem
        {
            TaskId = "start-game",
            IsEnabled = false,
            Settings = new JsonObject
            {
                ["packageId"] = "com.umamusume.app",
                ["activityName"] = "com.umamusume.app/com.example.MainActivity",
            },
        });

        service.Save(original);
        var loaded = service.Load();

        Assert.Equal(original.AdbPath, loaded.AdbPath);
        Assert.Equal(original.ConnectAddress, loaded.ConnectAddress);
        Assert.Equal(original.AutoDetectConnection, loaded.AutoDetectConnection);
        Assert.Equal(original.AlwaysAutoDetectConnection, loaded.AlwaysAutoDetectConnection);
        Assert.Equal(original.ConnectConfig, loaded.ConnectConfig);
        Assert.Equal(original.Language, loaded.Language);
        Assert.Equal(original.ConnectAddressHistory, loaded.ConnectAddressHistory);
        Assert.Equal(original.TargetPackageIds, loaded.TargetPackageIds);
        Assert.Equal(original.TargetActivityName, loaded.TargetActivityName);
        var cachedTask = Assert.Single(loaded.TaskQueue);
        Assert.Equal("start-game", cachedTask.TaskId);
        Assert.False(cachedTask.IsEnabled);
        Assert.Equal("com.umamusume.app", cachedTask.Settings["packageId"]!.GetValue<string>());
        Assert.Equal(
            "com.umamusume.app/com.example.MainActivity",
            cachedTask.Settings["activityName"]!.GetValue<string>());
    }

    [Fact]
    public void SaveThenLoad_DefaultSettingsRoundtrip()
    {
        var service = new JsonSettingsService(_filePath);
        var original = new ConnectionSettings();

        service.Save(original);
        var loaded = service.Load();

        Assert.Equal("", loaded.AdbPath);
        Assert.True(loaded.AutoDetectConnection);
        Assert.False(loaded.AlwaysAutoDetectConnection);
        Assert.Equal("General", loaded.ConnectConfig);
        Assert.Equal("en-US", loaded.Language);
        Assert.Empty(loaded.ConnectAddressHistory);
        Assert.Empty(loaded.TargetPackageIds);
        Assert.Equal("", loaded.TargetActivityName);
        Assert.Empty(loaded.TaskQueue);
    }

    // ================================================================
    // File is actually written
    // ================================================================

    [Fact]
    public void Save_WritesJsonFile()
    {
        var service = new JsonSettingsService(_filePath);
        service.Save(new ConnectionSettings());

        Assert.True(File.Exists(_filePath));
        var content = File.ReadAllText(_filePath);
        Assert.Contains("AutoDetectConnection", content);
    }

    // ================================================================
    // Recovery — overwrite corrupted file with valid defaults on save
    // ================================================================

    [Fact]
    public void Load_CorruptedThenSave_WritesValidJson()
    {
        File.WriteAllText(_filePath, "{broken");
        var service = new JsonSettingsService(_filePath);

        // Load from corrupted file returns defaults
        var loaded = service.Load();
        Assert.True(loaded.AutoDetectConnection);

        // Save should write valid JSON
        loaded.Language = "ja-JP";
        service.Save(loaded);

        // Re-read should get the saved value
        var reloaded = service.Load();
        Assert.Equal("ja-JP", reloaded.Language);
    }
}
