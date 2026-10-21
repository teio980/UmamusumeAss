using System.Text.Json;
using Umamusume.Avalonia.Services;
using Xunit;

namespace Umamusume.Avalonia.Tests;

public sealed class MacResourceStoreTests
{
    [Fact]
    public async Task MalformedActivePointerFallsBackToBundledResources()
    {
        string root = NewTemporaryRoot();
        try
        {
            string resources = Path.Combine(root, "resources");
            Directory.CreateDirectory(resources);
            await File.WriteAllTextAsync(Path.Combine(resources, "active.json"), "{");
            var store = new MacResourceStore(root);

            Assert.Null(await store.GetPreferredResourceSourceRootAsync("/bundle/resource", root));
            Assert.Equal(await MacResourceStore.ReadBundledVersionAsync(), await store.ReadActiveVersionAsync());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task InvalidBase64RevisionSignatureFallsBackToBundledResources()
    {
        string root = NewTemporaryRoot();
        const string version = "2026.10.21.1";
        try
        {
            string resources = Path.Combine(root, "resources");
            string revision = Path.Combine(resources, "revisions", version);
            Directory.CreateDirectory(Path.Combine(revision, "payload"));
            await File.WriteAllTextAsync(Path.Combine(revision, "manifest.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(revision, "manifest.sig"), "not base64!");
            await File.WriteAllTextAsync(Path.Combine(resources, "active.json"), JsonSerializer.Serialize(new
            {
                Version = version,
                RelativePath = Path.Combine("revisions", version)
            }));
            var store = new MacResourceStore(root);

            Assert.Null(await store.GetPreferredResourceSourceRootAsync("/bundle/resource", root));
            Assert.Equal(await MacResourceStore.ReadBundledVersionAsync(), await store.ReadActiveVersionAsync());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewTemporaryRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "uma-mac-resource-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
