using System;
using System.IO;
using Umamusume.AppCore.Platform;
using Xunit;

namespace Umamusume.AppCore.Tests;

public sealed class PlatformPathsTests
{
    [Fact]
    public void GetDefaultUserDataDirectoryReturnsValidPath()
    {
        string dir = PlatformPaths.GetDefaultUserDataDirectory();
        Assert.False(string.IsNullOrWhiteSpace(dir));
        Assert.Contains("UmamusumeAss", dir);
    }

    [Fact]
    public void GetDefaultSettingsFilePathReturnsExpectedPath()
    {
        string path = PlatformPaths.GetDefaultSettingsFilePath();
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.EndsWith("config.json", path);
    }

    [Fact]
    public void GetDefaultResourceDirectoryFindsRepoResource()
    {
        string dir = PlatformPaths.GetDefaultResourceDirectory();
        Assert.False(string.IsNullOrWhiteSpace(dir));
        Assert.True(Directory.Exists(dir), $"Resource directory should exist: {dir}");
        Assert.True(Directory.Exists(Path.Combine(dir, "hachimi")), "Should contain hachimi directory");
        Assert.True(File.Exists(Path.Combine(dir, "connection.json")), "Should contain connection.json");
    }
}
