using System.Text.Json;
using Umamusume.AppCore.Platform;
using Umamusume.Avalonia.Services;
using Xunit;

namespace Umamusume.Avalonia.Tests;

public sealed class ResourceStartupMigrationTests
{
    [Fact]
    public async Task OldActiveResourcePointerDoesNotOvershadowNewBuiltinResources()
    {
        string root = Path.Combine(Path.GetTempPath(), "uma-resource-migration-" + Guid.NewGuid().ToString("N"));
        try
        {
            string dataRoot = Path.Combine(root, "data");
            string bundledRoot = Path.Combine(root, "bundle", "resource");
            Directory.CreateDirectory(bundledRoot);

            // Shipped in new package
            await File.WriteAllTextAsync(Path.Combine(bundledRoot, "new_official.txt"), "v2 official");
            await File.WriteAllTextAsync(Path.Combine(bundledRoot, "shared.txt"), "v2 official shared");
            await File.WriteAllTextAsync(Path.Combine(bundledRoot, "user_override_target.txt"), "official base");

            // Old downloaded resource revision from v1 in user data directory
            string revisionsRoot = Path.Combine(dataRoot, "resources", "revisions", "2026.10.20.1", "payload");
            Directory.CreateDirectory(revisionsRoot);
            await File.WriteAllTextAsync(Path.Combine(revisionsRoot, "deleted_old_official.txt"), "should not linger");
            await File.WriteAllTextAsync(Path.Combine(revisionsRoot, "shared.txt"), "v1 old official shared");

            // Old active pointer pointing to v1 revision
            string resourcesRoot = Path.Combine(dataRoot, "resources");
            Directory.CreateDirectory(resourcesRoot);
            await File.WriteAllTextAsync(Path.Combine(resourcesRoot, "active.json"), JsonSerializer.Serialize(new
            {
                Version = "2026.10.20.1",
                RelativePath = Path.Combine("revisions", "2026.10.20.1")
            }));

            // User overrides in user data directory
            string overridesRoot = Path.Combine(dataRoot, "resource-overrides");
            Directory.CreateDirectory(overridesRoot);
            await File.WriteAllTextAsync(Path.Combine(overridesRoot, "custom_user.txt"), "user customized file");
            await File.WriteAllTextAsync(Path.Combine(overridesRoot, "user_override_target.txt"), "user override value");

            // User config and tasks in user data directory
            string configJson = JsonSerializer.Serialize(new { AdbPath = "/custom/adb/path", Language = "zh-CN" });
            await File.WriteAllTextAsync(Path.Combine(dataRoot, "config.json"), configJson);
            string tasksJson = JsonSerializer.Serialize(new[] { new { Name = "DailyTask", Enabled = true } });
            await File.WriteAllTextAsync(Path.Combine(dataRoot, "tasks.json"), tasksJson);

            // 1. Verify MacResourceStore returns null (does not prioritize old downloaded pointer)
            var store = new MacResourceStore(dataRoot);
            var preferredSource = await store.GetPreferredResourceSourceRootAsync(bundledRoot, dataRoot);
            Assert.Null(preferredSource);

            // 2. Base resource root used by automation service defaults to bundledRoot
            string effectiveSourceRoot = preferredSource ?? bundledRoot;
            Assert.Equal(bundledRoot, effectiveSourceRoot);

            // 3. Prepare workspace with effective source root
            var workspace = await ResourceWorkspace.PrepareAsync(effectiveSourceRoot, dataRoot);
            string activeRoot = workspace.ActiveResourceRoot;

            // Verify new official files exist with new content
            Assert.True(File.Exists(Path.Combine(activeRoot, "new_official.txt")));
            Assert.Equal("v2 official", await File.ReadAllTextAsync(Path.Combine(activeRoot, "new_official.txt")));

            // Verify shared official file has new package content, not old downloaded content
            Assert.Equal("v2 official shared", await File.ReadAllTextAsync(Path.Combine(activeRoot, "shared.txt")));

            // Verify deleted old official files do NOT linger
            Assert.False(File.Exists(Path.Combine(activeRoot, "deleted_old_official.txt")));

            // Verify user overrides are preserved and win over official files
            Assert.True(File.Exists(Path.Combine(activeRoot, "custom_user.txt")));
            Assert.Equal("user customized file", await File.ReadAllTextAsync(Path.Combine(activeRoot, "custom_user.txt")));
            Assert.Equal("user override value", await File.ReadAllTextAsync(Path.Combine(activeRoot, "user_override_target.txt")));

            // Verify user config and tasks are untouched
            Assert.Equal(configJson, await File.ReadAllTextAsync(Path.Combine(dataRoot, "config.json")));
            Assert.Equal(tasksJson, await File.ReadAllTextAsync(Path.Combine(dataRoot, "tasks.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }
}
