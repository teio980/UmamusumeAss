using System.Text.Json;
using Umamusume.Avalonia.Services;
using Xunit;

namespace Umamusume.Avalonia.Tests;

public sealed class MacBundleAndHelperResultTests
{
    [Fact]
    public void FindsBundleResourcesAndKeepsLegacyLayoutSupport()
    {
        string root = Path.Combine(Path.GetTempPath(), "uma-mac-bundle-path-test-" + Guid.NewGuid().ToString("N"));
        string macOs = Path.Combine(root, "Contents", "MacOS");
        string resources = Path.Combine(root, "Contents", "Resources");
        Directory.CreateDirectory(macOs);
        Directory.CreateDirectory(resources);
        try
        {
            string resourceFile = Path.Combine(resources, "version.txt");
            File.WriteAllText(resourceFile, "0.3.0");
            Assert.Equal(resourceFile, MacBundleFiles.FindResourceFile(macOs, "version.txt"));

            string legacyFile = Path.Combine(macOs, "version.txt");
            File.WriteAllText(legacyFile, "0.2.0");
            Assert.Equal(legacyFile, MacBundleFiles.FindResourceFile(macOs, "version.txt"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IgnoresStaleHelperResultAndThrowsForCurrentRequestFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "uma-mac-helper-result-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string resultPath = Path.Combine(root, "result.json");
        try
        {
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new
            {
                OperationId = "operation-1",
                Nonce = "old-nonce",
                Error = "stale failure",
            }));

            await MacProgramInstaller.ThrowIfHelperFailedAsync(
                resultPath, "operation-1", "current-nonce", CancellationToken.None);

            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new
            {
                OperationId = "operation-1",
                Nonce = "current-nonce",
                Error = "current failure",
            }));

            var exception = await Assert.ThrowsAsync<IOException>(() => MacProgramInstaller.ThrowIfHelperFailedAsync(
                resultPath, "operation-1", "current-nonce", CancellationToken.None));
            Assert.Equal("current failure", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
