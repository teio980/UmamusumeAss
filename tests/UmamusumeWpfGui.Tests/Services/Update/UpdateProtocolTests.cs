using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UmamusumeWpfGui;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Update;

namespace UmamusumeWpfGui.Tests.Services.Update;

public sealed class UpdateProtocolTests
{
    [Fact]
    public void SelectorRequiresBothSignedSourceHashesForDelta()
    {
        var manifest = new UpdateManifest
        {
            Version = "0.3.0",
            ReleaseTag = "v0.3.0",
            Assets =
            [
                new UpdateAsset
                {
                    Type = "delta", FromVersion = "0.2.0", AssetName = "delta.zip",
                    SourceTreeSha256 = "ABC", SourceManifestSha256 = "DEF",
                },
                new UpdateAsset { Type = "full", AssetName = "full.zip" },
            ],
        };

        var delta = UpdateSelector.Select(manifest, "0.2.0", "ABC", "DEF", true);
        Assert.NotNull(delta);
        Assert.Equal("delta.zip", delta!.Asset.AssetName);

        var missingManifestHash = UpdateSelector.Select(manifest, "0.2.0", "ABC", null, true);
        Assert.NotNull(missingManifestHash);
        Assert.Equal("full.zip", missingManifestHash!.Asset.AssetName);

        var wrongTree = UpdateSelector.Select(manifest, "0.2.0", "WRONG", "DEF", true);
        Assert.NotNull(wrongTree);
        Assert.Equal("full.zip", wrongTree!.Asset.AssetName);
    }

    [Fact]
    public void UpdateStateRetainsProgramBaselineWhenResourceChannelCommits()
    {
        var root = Path.Combine(Path.GetTempPath(), "UmaUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new UpdateStateStore(root);
            store.Save(new UpdateState
            {
                CurrentManifestSigned = true,
                ManifestSha256 = "PROGRAM-MANIFEST",
                CurrentTreeSha256 = "PROGRAM-TREE",
                SignedManifestPath = "program.json",
                SignedSignaturePath = "program.sig",
            });
            store.Update(state =>
            {
                state.Scope = "resource";
                state.ResourceManifestSha256 = "RESOURCE-MANIFEST";
                state.ResourceManifestPath = "resource.json";
                state.ResourceSignaturePath = "resource.sig";
            });

            var retained = store.Load();
            Assert.True(retained.CurrentManifestSigned);
            Assert.Equal("PROGRAM-MANIFEST", retained.ManifestSha256);
            Assert.Equal("PROGRAM-TREE", retained.CurrentTreeSha256);
            Assert.Equal("RESOURCE-MANIFEST", retained.ResourceManifestSha256);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProgramHealthAckPersistsManifestHashAndClearsForceFull()
    {
        var root = Path.Combine(Path.GetTempPath(), "UmaUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var manifestPath = Path.Combine(root, "manifest.json");
        var signaturePath = Path.Combine(root, "manifest.sig");
        var manifest = new UpdateManifest
        {
            Version = "0.3.0",
            ReleaseTag = "v0.3.0",
            Assets =
            [
                new UpdateAsset
                {
                    Type = "full",
                    AssetName = "program-full.zip",
                    TargetTreeSha256 = new string('A', 64),
                },
            ],
        };
        await File.WriteAllBytesAsync(
            manifestPath,
            JsonSerializer.SerializeToUtf8Bytes(manifest));
        await File.WriteAllTextAsync(signaturePath, "signature");

        try
        {
            var store = new UpdateStateStore(root);
            store.Save(new UpdateState
            {
                ForceFull = true,
                ResourceManifestSha256 = "RESOURCE-MANIFEST",
                ResourceManifestPath = "resource.json",
                ResourceSignaturePath = "resource.sig",
            });

            Bootstrapper.PersistProgramHealthState(
                store,
                manifestPath,
                signaturePath,
                manifest);

            var state = store.Load();
            Assert.False(state.ForceFull);
            Assert.True(state.CurrentManifestSigned);
            Assert.Equal("health-succeeded", state.Stage);
            Assert.Equal(ManifestVerifier.Sha256File(manifestPath), state.ManifestSha256);
            Assert.Equal(new string('A', 64), state.CurrentTreeSha256);
            Assert.Equal("RESOURCE-MANIFEST", state.ResourceManifestSha256);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ResourceManifestAcceptsNumericRevisionDelta()
    {
        var manifest = new UpdateManifest
        {
            Kind = "resource",
            Version = "2026.09.11.1",
            ReleaseTag = "resource-v2026.09.11.1",
            Assets =
            [
                new UpdateAsset
                {
                    Type = "delta", FromVersion = "2026.09.10.10", AssetName = "delta.zip",
                    Sha256 = new string('A', 64), TargetTreeSha256 = new string('B', 64),
                },
                new UpdateAsset
                {
                    Type = "full", AssetName = "full.zip",
                    Sha256 = new string('C', 64), TargetTreeSha256 = new string('D', 64),
                },
            ],
        };

        Assert.True(ManifestVerifier.IsCanonicalManifest(manifest, manifest.ReleaseTag));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("C:/absolute")]
    [InlineData("/absolute")]
    [InlineData("folder/./file")]
    public void UpdatePathsCannotEscapeRoot(string path)
    {
        Assert.Throws<InvalidDataException>(() => UpdatePathSafety.ValidateRelative(path));
    }

    [Fact]
    public async Task ResourceOverrideRebuildsCompositeWithoutChangingBase()
    {
        var root = Path.Combine(Path.GetTempPath(), "UmaUpdateTests", Guid.NewGuid().ToString("N"));
        var bundled = Path.Combine(root, "install", "resource");
        var appData = Path.Combine(root, "appdata");
        Directory.CreateDirectory(Path.Combine(bundled, "templates"));
        await File.WriteAllTextAsync(Path.Combine(bundled, "config.json"), "base");
        await File.WriteAllTextAsync(Path.Combine(bundled, "templates", "sibling.json"), "sibling");

        try
        {
            using var store = new ResourceStore(appData);
            await store.InitializeAsync(bundled);
            await using (var content = new MemoryStream(Encoding.UTF8.GetBytes("override")))
                await store.WriteOverrideAsync("resource/config.json", content);

            Assert.Equal("override", await File.ReadAllTextAsync(store.Resolve("resource/config.json")));
            Assert.Equal("sibling", await File.ReadAllTextAsync(store.Resolve("resource/templates/sibling.json")));
            Assert.Equal("base", await File.ReadAllTextAsync(
                Path.Combine(appData, "resources", "bases", "0.2.0", "resource", "config.json")));
        }
        finally
        {
            ResourcePathRuntime.SetBaseDirectory(AppContext.BaseDirectory);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ResourceFullThenDeltaAppliesAgainstTrustedBaseAndDeletesFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "UmaUpdateTests", Guid.NewGuid().ToString("N"));
        var bundled = Path.Combine(root, "install", "resource");
        var appData = Path.Combine(root, "appdata");
        var fullPayload = Path.Combine(root, "full-payload");
        var deltaPayload = Path.Combine(root, "delta-payload");
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(fullPayload);
        Directory.CreateDirectory(deltaPayload);
        await File.WriteAllTextAsync(Path.Combine(bundled, "seed.txt"), "seed");
        await File.WriteAllTextAsync(Path.Combine(fullPayload, "keep.txt"), "old");
        await File.WriteAllTextAsync(Path.Combine(fullPayload, "delete.txt"), "remove");
        await File.WriteAllTextAsync(Path.Combine(deltaPayload, "keep.txt"), "new");
        try
        {
            using var store = new ResourceStore(appData);
            await store.InitializeAsync(bundled);
            var fullHash = await ComputeTreeHashAsync(fullPayload);
            var full = await store.PrepareAsync("2026.09.10.1", fullPayload, false, null, fullHash);
            await store.CommitAsync(full);

            var expectedDeltaRoot = Path.Combine(root, "expected");
            Directory.CreateDirectory(expectedDeltaRoot);
            await File.WriteAllTextAsync(Path.Combine(expectedDeltaRoot, "keep.txt"), "new");
            var deltaHash = await ComputeTreeHashAsync(expectedDeltaRoot);
            var delta = await store.PrepareAsync(
                "2026.09.11.1", deltaPayload, true, ["delete.txt"], deltaHash);
            Assert.Equal(deltaHash, delta.Revision.BaseTreeSha256);
            Assert.Equal("new", await File.ReadAllTextAsync(
                Path.Combine(delta.CompositeDirectory, "resource", "keep.txt")));
            Assert.False(File.Exists(Path.Combine(delta.CompositeDirectory, "resource", "delete.txt")));
            await store.CommitAsync(delta);
            Assert.Equal("2026.09.11.1", store.Active.Version);
        }
        finally
        {
            ResourcePathRuntime.SetBaseDirectory(AppContext.BaseDirectory);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task<string> ComputeTreeHashAsync(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            using var stream = File.OpenRead(file);
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer.AsSpan(0, read));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
