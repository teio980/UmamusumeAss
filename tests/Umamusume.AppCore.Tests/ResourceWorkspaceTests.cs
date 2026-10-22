using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Platform;
using Umamusume.AppCore.Services;
using Xunit;

namespace Umamusume.AppCore.Tests;

[Collection("PlatformPaths")]
public sealed class ResourceWorkspaceTests
{
    [Fact]
    public async Task NativeInitializationUsesWritableViewAndOverridesSurviveSourceUpdates()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"uma_workspace_{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(tempRoot, "bundle", "resource");
        var userDataRoot = Path.Combine(tempRoot, "user-data");
        var pipelinePath = Path.Combine(sourceRoot, "hachimi", "pipelines", "custom.json");
        var imagePath = Path.Combine(sourceRoot, "hachimi", "templates", "home.png");
        byte[] originalImageBytes = [0, 1, 2, 3, 4];
        byte[] replacementImageBytes = [9, 8, 7, 6, 5, 4];
        Directory.CreateDirectory(Path.GetDirectoryName(pipelinePath)!);
        await File.WriteAllTextAsync(pipelinePath, "{\"version\":1}");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "connection.json"), "{\"source\":1}");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        await File.WriteAllBytesAsync(imagePath, originalImageBytes);

        try
        {
            var workspace = await ResourceWorkspace.PrepareAsync(sourceRoot, userDataRoot);
            Assert.NotEqual(Path.GetFullPath(sourceRoot), workspace.ActiveResourceRoot);
            Assert.Equal("{\"version\":1}", await File.ReadAllTextAsync(pipelinePath));
            Assert.Equal("{\"version\":1}", await File.ReadAllTextAsync(
                Path.Combine(workspace.ActiveResourceRoot, "hachimi", "pipelines", "custom.json")));

            var core = new FakeUmaService();
            var automation = new UmaAutomationService(
                new LogService(),
                new ConnectionStateService(),
                core,
                new MemorySettingsService(new ConnectionSettings()),
                workspace);
            await automation.InitializeAsync();

            Assert.Equal(workspace.ActiveBaseDirectory, core.LastAppBaseDirectory);
            Assert.Equal(workspace.ActiveResourceRoot, automation.ResourcePath);
            Assert.Equal(
                Path.Combine(workspace.ActiveResourceRoot, "hachimi", "pipelines", "custom.json"),
                automation.ResolveActiveResourcePath(pipelinePath));
            Assert.True(await automation.SaveResourceTextAsync(pipelinePath, "{\"version\":99}"));
            Assert.Equal("{\"version\":1}", await File.ReadAllTextAsync(pipelinePath));
            Assert.Equal("{\"version\":99}", await File.ReadAllTextAsync(
                Path.Combine(workspace.ActiveResourceRoot, "hachimi", "pipelines", "custom.json")));
            Assert.Equal("{\"version\":99}", await File.ReadAllTextAsync(
                Path.Combine(userDataRoot, "resource-overrides", "hachimi", "pipelines", "custom.json")));
            Assert.True(await automation.SaveResourceBytesAsync(imagePath, replacementImageBytes));
            byte[] sourceImageAfterSave = await File.ReadAllBytesAsync(imagePath);
            byte[] viewImageAfterSave = await File.ReadAllBytesAsync(
                Path.Combine(workspace.ActiveResourceRoot, "hachimi", "templates", "home.png"));
            byte[] overrideImageAfterSave = await File.ReadAllBytesAsync(
                Path.Combine(userDataRoot, "resource-overrides", "hachimi", "templates", "home.png"));
            Assert.True(originalImageBytes.SequenceEqual(sourceImageAfterSave));
            Assert.True(replacementImageBytes.SequenceEqual(viewImageAfterSave));
            Assert.True(replacementImageBytes.SequenceEqual(overrideImageAfterSave));

            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "connection.json"), "{\"source\":2}");
            var updatedWorkspace = await ResourceWorkspace.PrepareAsync(sourceRoot, userDataRoot);

            Assert.NotEqual(workspace.ActiveResourceRoot, updatedWorkspace.ActiveResourceRoot);
            Assert.Equal(Path.GetDirectoryName(updatedWorkspace.ActiveResourceRoot), updatedWorkspace.ActiveBaseDirectory);
            Assert.Equal("{\"source\":2}", await File.ReadAllTextAsync(
                Path.Combine(updatedWorkspace.ActiveResourceRoot, "connection.json")));
            Assert.Equal("{\"version\":99}", await File.ReadAllTextAsync(
                Path.Combine(updatedWorkspace.ActiveResourceRoot, "hachimi", "pipelines", "custom.json")));
            byte[] updatedViewImage = await File.ReadAllBytesAsync(
                Path.Combine(updatedWorkspace.ActiveResourceRoot, "hachimi", "templates", "home.png"));
            Assert.True(replacementImageBytes.SequenceEqual(updatedViewImage));
        }
        finally
        {
            PlatformPaths.SetActiveResourceDirectory(sourceRoot);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExternalSaveIsAtomicButDoesNotCreateResourceOverride()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"uma_workspace_{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(tempRoot, "source");
        var userDataRoot = Path.Combine(tempRoot, "user-data");
        var externalPath = Path.Combine(tempRoot, "my-pipeline.json");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            var workspace = await ResourceWorkspace.PrepareAsync(sourceRoot, userDataRoot);
            Assert.False(await workspace.SaveTextAsync(externalPath, "{\"external\":true}"));
            Assert.Equal("{\"external\":true}", await File.ReadAllTextAsync(externalPath));
            Assert.False(Directory.Exists(workspace.OverridesRoot)
                && Directory.EnumerateFiles(workspace.OverridesRoot, "*", SearchOption.AllDirectories).Any());
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
