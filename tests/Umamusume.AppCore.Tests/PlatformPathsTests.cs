using System;
using System.IO;
using Umamusume.AppCore.Platform;
using Xunit;

namespace Umamusume.AppCore.Tests;

[Collection("PlatformPaths")]
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

    [Fact]
    public void OldAbsoluteResourcePathsRemapIntoTheNewActiveView()
    {
        string firstRoot = Path.Combine(Path.GetTempPath(), "uma-resource-old-" + Guid.NewGuid().ToString("N"));
        string secondRoot = Path.Combine(Path.GetTempPath(), "uma-resource-new-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(firstRoot, "hachimi", "pipelines"));
        Directory.CreateDirectory(Path.Combine(secondRoot, "hachimi", "pipelines"));
        string oldFile = Path.Combine(firstRoot, "hachimi", "pipelines", "example.json");
        try
        {
            PlatformPaths.SetActiveResourceDirectory(firstRoot, firstRoot);
            PlatformPaths.SetActiveResourceDirectory(secondRoot, secondRoot);

            Assert.Equal(Path.Combine(secondRoot, "hachimi", "pipelines", "example.json"),
                PlatformPaths.ResolveActiveResourcePath(oldFile));
        }
        finally
        {
            PlatformPaths.SetActiveResourceDirectory(PlatformPaths.GetDefaultResourceDirectory());
            if (Directory.Exists(firstRoot)) Directory.Delete(firstRoot, recursive: true);
            if (Directory.Exists(secondRoot)) Directory.Delete(secondRoot, recursive: true);
        }
    }
}
