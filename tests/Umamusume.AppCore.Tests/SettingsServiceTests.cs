using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Services;
using Xunit;

namespace Umamusume.AppCore.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void NonexistentSettingsFileReturnsDefaults()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"uma_test_{Guid.NewGuid():N}.json");
        try
        {
            var service = new JsonSettingsService(tempPath);
            var settings = service.Load();
            Assert.NotNull(settings);
            Assert.Equal("General", settings.ConnectConfig);
            Assert.Equal("en-US", settings.Language);
            Assert.True(settings.AutoDetectConnection);
            Assert.Equal("System", settings.Theme);
            Assert.Equal(15, settings.ConnectionTimeoutSeconds);
            Assert.NotNull(settings.Hachimi);
            Assert.True(settings.Hachimi.Shop.Enabled);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void RoundTripSettingsSerializationPreservesAllFields()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"uma_test_{Guid.NewGuid():N}.json");
        try
        {
            var service = new JsonSettingsService(tempPath);
            var original = new ConnectionSettings
            {
                AdbPath = "/path/to/adb",
                ConnectAddress = "127.0.0.1:5555",
                AutoDetectConnection = false,
                AutoStartEmulator = true,
                AutoStartEmulatorWaitSeconds = 15,
                ConnectConfig = "BlueStacks",
                Language = "zh-CN",
                Theme = "Light",
                ConnectionTimeoutSeconds = 87,
                TargetActivityName = "com.test.Activity",
                Hachimi =
                {
                    SelectedTaskId = "mail-collection",
                    SelectedTaskIndex = 0,
                    TaskQueue =
                    [
                        new HachimiTaskQueueItem
                        {
                            TaskId = "mail-collection",
                            IsEnabled = true,
                            Settings = new JsonObject
                            {
                                ["definitionPath"] = "resource/hachimi/pipelines/mail_collection.json",
                            },
                        },
                    ],
                },
            };
            original.ConnectAddressHistory.Add("127.0.0.1:5555");
            original.TargetPackageIds.Add("jp.co.cygames.umamusume");
            original.Hachimi.Shop.BuyAlarmClock = true;
            original.Hachimi.Shop.BuyShoes = true;

            service.Save(original);
            Assert.True(File.Exists(tempPath));

            var loaded = service.Load();
            Assert.Equal(original.AdbPath, loaded.AdbPath);
            Assert.Equal(original.ConnectAddress, loaded.ConnectAddress);
            Assert.Equal(original.AutoDetectConnection, loaded.AutoDetectConnection);
            Assert.Equal(original.AutoStartEmulator, loaded.AutoStartEmulator);
            Assert.Equal(original.AutoStartEmulatorWaitSeconds, loaded.AutoStartEmulatorWaitSeconds);
            Assert.Equal(original.ConnectConfig, loaded.ConnectConfig);
            Assert.Equal(original.Language, loaded.Language);
            Assert.Equal("Light", loaded.Theme);
            Assert.Equal(87, loaded.ConnectionTimeoutSeconds);
            Assert.Equal(original.TargetActivityName, loaded.TargetActivityName);
            Assert.Single(loaded.ConnectAddressHistory);
            Assert.Equal("127.0.0.1:5555", loaded.ConnectAddressHistory[0]);
            Assert.Single(loaded.TargetPackageIds);
            Assert.True(loaded.Hachimi.Shop.BuyAlarmClock);
            Assert.True(loaded.Hachimi.Shop.BuyShoes);
            Assert.False(loaded.Hachimi.Shop.BuyFlags);
            Assert.Equal("mail-collection", loaded.Hachimi.SelectedTaskId);
            Assert.Equal(0, loaded.Hachimi.SelectedTaskIndex);
            Assert.Single(loaded.Hachimi.TaskQueue);
            Assert.Equal("mail-collection", loaded.Hachimi.TaskQueue[0].TaskId);
            Assert.Equal("mail-collection", loaded.TaskQueue[0].TaskId);
            Assert.Equal(
                "resource/hachimi/pipelines/mail_collection.json",
                loaded.Hachimi.TaskQueue[0].Settings["definitionPath"]!.GetValue<string>());
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void LoadsWindowsRootTaskQueueAndPreservesItInAppCoreHachimiSettings()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"uma_test_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempPath, """
                {
                  "Hachimi": { "Shop": { "Enabled": true, "BuyAlarmClock": true } },
                  "TaskQueue": [
                    { "TaskId": "team-race", "IsEnabled": false, "Settings": { "raceCount": 5 } }
                  ]
                }
                """);
            var service = new JsonSettingsService(tempPath);

            var loaded = service.Load();

            Assert.Single(loaded.Hachimi.TaskQueue);
            Assert.Equal("team-race", loaded.Hachimi.TaskQueue[0].TaskId);
            Assert.False(loaded.Hachimi.TaskQueue[0].IsEnabled);
            Assert.Equal(5, loaded.Hachimi.TaskQueue[0].Settings["raceCount"]!.GetValue<int>());
            Assert.True(loaded.Hachimi.Shop.BuyAlarmClock);

            loaded.Hachimi.TaskQueue[0].Settings["raceCount"] = 3;
            service.Save(loaded);
            using var saved = JsonDocument.Parse(File.ReadAllText(tempPath));
            Assert.Equal(3, saved.RootElement.GetProperty("TaskQueue")[0].GetProperty("Settings").GetProperty("raceCount").GetInt32());
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void AnExplicitlyEmptyWindowsQueueOverridesStaleNestedQueue()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"uma_test_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempPath, """
                {
                  "Hachimi": {
                    "TaskQueue": [
                      { "TaskId": "mail-collection", "IsEnabled": true, "Settings": {} }
                    ]
                  },
                  "TaskQueue": []
                }
                """);

            var loaded = new JsonSettingsService(tempPath).Load();

            Assert.Empty(loaded.Hachimi.TaskQueue);
            Assert.Empty(loaded.TaskQueue);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void SavePropagatesFilesystemErrors()
    {
        string targetPath = Path.Combine(Path.GetTempPath(), $"uma_test_dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetPath);
        try
        {
            var service = new JsonSettingsService(targetPath);
            Assert.ThrowsAny<IOException>(() => service.Save(new ConnectionSettings()));
        }
        finally
        {
            Directory.Delete(targetPath, recursive: true);
        }
    }

    [Fact]
    public void ConnectionSettingsClampNewNumericTimeoutAndNormalizeTheme()
    {
        var settings = new ConnectionSettings
        {
            ConnectionTimeoutSeconds = 0,
            Theme = "dark",
        };

        Assert.Equal(1, settings.ConnectionTimeoutSeconds);
        Assert.Equal("Dark", settings.Theme);

        settings.ConnectionTimeoutSeconds = 500;
        settings.Theme = "sepia";

        Assert.Equal(300, settings.ConnectionTimeoutSeconds);
        Assert.Equal("System", settings.Theme);
    }
}
