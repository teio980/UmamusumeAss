using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Update;

namespace UmamusumeWpfGui.Tests.Services.Update;

public sealed class UpdateProtocolTests
{
    private static readonly JsonSerializerOptions CamelCaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task VerifiedDownloadedProgramCanBeRestoredAfterRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "UmaUpdateTests", Guid.NewGuid().ToString("N"));
        var updatesRoot = Path.Combine(root, "updates");
        var operationId = Guid.NewGuid().ToString("N");
        var operationRoot = Path.Combine(updatesRoot, operationId);
        var sourceRoot = Path.Combine(root, "source");
        var payloadRoot = Path.Combine(operationRoot, "payload");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(payloadRoot);

        try
        {
            var sourceFile = Path.Combine(sourceRoot, "app.dll");
            await File.WriteAllTextAsync(sourceFile, "cached program payload");
            File.Copy(sourceFile, Path.Combine(payloadRoot, "app.dll"));
            var packagePath = Path.Combine(operationRoot, "program-full.zip");
            ZipFile.CreateFromDirectory(sourceRoot, packagePath);

            var manifest = new UpdateManifest
            {
                Version = "9.9.9",
                ReleaseTag = "v9.9.9",
                Assets =
                [
                    new UpdateAsset
                    {
                        Type = "full",
                        AssetName = "program-full.zip",
                        Size = new FileInfo(packagePath).Length,
                        Sha256 = ManifestVerifier.Sha256File(packagePath),
                        TargetTreeSha256 = new string('A', 64),
                        Files =
                        [
                            new UpdateFileEntry
                            {
                                Path = "app.dll",
                                Size = new FileInfo(sourceFile).Length,
                                Sha256 = ManifestVerifier.Sha256File(sourceFile),
                            },
                        ],
                    },
                ],
            };
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, CamelCaseJsonOptions);
            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var signature = signer.SignData(
                manifestBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            await File.WriteAllBytesAsync(Path.Combine(operationRoot, "manifest.json"), manifestBytes);
            await File.WriteAllTextAsync(
                Path.Combine(operationRoot, "manifest.sig"),
                Convert.ToBase64String(signature));

            var verifier = new ManifestVerifier(signer.ExportSubjectPublicKeyInfo());
            var stager = new PackageStager(new GitHubReleaseClient(), verifier, updatesRoot);

            var restored = stager.TryRestore(
                operationId,
                UpdateScope.Program,
                "program-full.zip");

            Assert.NotNull(restored);
            Assert.Equal("9.9.9", restored!.Manifest.Version);
            Assert.Equal(packagePath, restored.PackagePath);

            await File.WriteAllTextAsync(Path.Combine(payloadRoot, "app.dll"), "tampered payload");
            Assert.Null(stager.TryRestore(
                operationId,
                UpdateScope.Program,
                "program-full.zip"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CopyIfDifferentSkipsAlreadyStagedManifestPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "UmaUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var manifestPath = Path.Combine(root, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, "signed-manifest");

        try
        {
            UpdateCoordinator.CopyIfDifferent(manifestPath, manifestPath);

            Assert.Equal("signed-manifest", await File.ReadAllTextAsync(manifestPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ManualCacheDeletionRemovesEntireDirectoryTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "UmaUpdateTests", Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(root, "updates");
        var nested = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N"), "payload");
        Directory.CreateDirectory(nested);
        var cachedPackage = Path.Combine(nested, "package.zip");
        await File.WriteAllTextAsync(cachedPackage, "cache");
        File.SetAttributes(cachedPackage, FileAttributes.ReadOnly);

        try
        {
            UpdateCoordinator.DeleteCacheDirectory(cacheRoot);

            Assert.False(Directory.Exists(cacheRoot));
        }
        finally
        {
            if (File.Exists(cachedPackage))
                File.SetAttributes(cachedPackage, FileAttributes.Normal);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

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
                StagedProgramOperationId = "program-operation",
                StagedProgramVersion = "0.3.0",
                StagedProgramAssetName = "program-full.zip",
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
            Assert.Equal("program-operation", retained.StagedProgramOperationId);
            Assert.Equal("0.3.0", retained.StagedProgramVersion);
            Assert.Equal("program-full.zip", retained.StagedProgramAssetName);
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

    [Fact]
    public void DeserializeReadsCamelCaseReleaseManifestAndPreservesSignedBytes()
    {
        var source = new UpdateManifest
        {
            Version = "0.3.0",
            ReleaseTag = "v0.3.0",
            BundledResourceVersion = "2026.09.11.1",
            Assets =
            [
                new UpdateAsset
                {
                    Type = "full",
                    AssetName = "program-full.zip",
                    Size = 123,
                    Sha256 = new string('A', 64),
                    TargetTreeSha256 = new string('B', 64),
                    Files =
                    [
                        new UpdateFileEntry
                        {
                            Path = "app.dll",
                            Size = 42,
                            Sha256 = new string('C', 64),
                        },
                    ],
                },
            ],
        };
        var manifestJson = JsonSerializer.Serialize(
            source,
            CamelCaseJsonOptions);
        Assert.Contains("\"schemaVersion\"", manifestJson);
        Assert.Contains("\"assetName\"", manifestJson);
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);

        var manifest = ManifestVerifier.Deserialize(manifestBytes);

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("0.3.0", manifest.Version);
        Assert.Equal("v0.3.0", manifest.ReleaseTag);
        var asset = Assert.Single(manifest.Assets);
        Assert.Equal("program-full.zip", asset.AssetName);
        Assert.Equal(123, asset.Size);
        var file = Assert.Single(asset.Files);
        Assert.Equal("app.dll", file.Path);
        Assert.Equal(42, file.Size);
        Assert.True(ManifestVerifier.IsCanonicalManifest(manifest, "v0.3.0"));

        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = new ManifestVerifier(signer.ExportSubjectPublicKeyInfo());
        var signature = signer.SignData(
            manifestBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Assert.True(verifier.Verify(manifestBytes, signature));
        Assert.False(verifier.Verify(JsonSerializer.SerializeToUtf8Bytes(manifest), signature));
    }

    [Fact]
    public void ManagedSignerAndNativeUpdaterEmbedTheSamePublicKey()
    {
        var root = FindSolutionRoot();
        var expected = File.ReadAllText(Path.Combine(root, "update-public.txt")).Trim();
        Assert.False(string.IsNullOrWhiteSpace(expected));

        var verifierSource = File.ReadAllText(Path.Combine(
            root, "src", "UmamusumeWpfGui", "Services", "Update", "ManifestVerifier.cs"));
        var signerSource = File.ReadAllText(Path.Combine(
            root, "tools", "UpdateSigner", "Program.cs"));
        Assert.Equal(expected, ExtractConstant(verifierSource, "EmbeddedPublicKey"));
        Assert.Equal(expected, ExtractConstant(signerSource, "expectedPublicKey"));

        var nativeSource = File.ReadAllText(Path.Combine(
            root, "src", "UmamusumeAssUpdater", "main.cpp"));
        var nativeX = ExtractNativeBytes(nativeSource, "kPublicX");
        var nativeY = ExtractNativeBytes(nativeSource, "kPublicY");
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(expected), out _);
        var parameters = ecdsa.ExportParameters(false);
        Assert.Equal(parameters.Q.X, nativeX);
        Assert.Equal(parameters.Q.Y, nativeY);
    }

    [Fact]
    public void NativeUpdaterUsesMaaStyleDirectCacheCleanup()
    {
        var root = FindSolutionRoot();
        var nativeSource = File.ReadAllText(Path.Combine(
            root, "src", "UmamusumeAssUpdater", "main.cpp"));
        var coordinatorSource = File.ReadAllText(Path.Combine(
            root, "src", "UmamusumeWpfGui", "Services", "Update",
            "UpdateCoordinatorService.cs"));
        var githubClientSource = File.ReadAllText(Path.Combine(
            root, "src", "UmamusumeWpfGui", "Services", "Update",
            "GitHubReleaseClient.cs"));
        var bootstrapperSource = File.ReadAllText(Path.Combine(
            root, "src", "UmamusumeWpfGui", "Bootstrapper.cs"));

        Assert.Contains(
            "ForceRemoveDirectoryRecursive(updatesRoot);",
            nativeSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"updates\", \"checks\"",
            coordinatorSource + githubClientSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("health.ack", nativeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CompleteHealthAckAsync", bootstrapperSource, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "powershell.exe",
            nativeSource,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "ScheduleOperationCleanup",
            nativeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Path.Combine(_appDataRoot, \"updater\", \"UmamusumeAss.Updater.exe\")",
            coordinatorSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Path.Combine(operationRoot, \"UmamusumeAss.Updater.exe\")",
            coordinatorSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UpdaterPlanUsesNativeProtocolFieldNames()
    {
        var plan = new
        {
            manifest = new
            {
                selectedAsset = new UpdateAsset
                {
                    Type = "full",
                    AssetName = "program-full.zip",
                },
            },
            replace = new[]
            {
                new UpdateFileEntry { Path = "app.dll", Size = 42 },
            },
        };

        using var document = JsonDocument.Parse(UpdateCoordinator.SerializeUpdaterPlan(plan));
        var asset = document.RootElement
            .GetProperty("manifest")
            .GetProperty("selectedAsset");
        Assert.Equal("full", asset.GetProperty("type").GetString());
        Assert.Equal("program-full.zip", asset.GetProperty("assetName").GetString());
        Assert.False(asset.TryGetProperty("fromVersion", out _));
        Assert.False(asset.TryGetProperty("AssetName", out _));
        Assert.Equal(
            "app.dll",
            document.RootElement.GetProperty("replace")[0].GetProperty("path").GetString());
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

    private static string ExtractConstant(string source, string name)
    {
        var declaration = Regex.Match(
            source,
            Regex.Escape(name) + @"\s*=\s*(?<value>(?:""[^""]*""\s*\+?\s*)+);",
            RegexOptions.CultureInvariant);
        Assert.True(declaration.Success, $"Could not locate {name} in source.");
        return string.Concat(
            Regex.Matches(declaration.Groups["value"].Value, "\"([^\"]*)\"")
                .Select(match => match.Groups[1].Value));
    }

    private static byte[] ExtractNativeBytes(string source, string name)
    {
        var declaration = Regex.Match(
            source,
            $@"{Regex.Escape(name)}\s*=\s*\{{(?<value>[^}}]*)\}};",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);
        Assert.True(declaration.Success, $"Could not locate {name} in native source.");
        return Regex.Matches(declaration.Groups["value"].Value, @"0x([0-9a-fA-F]{2})")
            .Select(match => Convert.ToByte(match.Groups[1].Value, 16))
            .ToArray();
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "update-public.txt"))
                && File.Exists(Path.Combine(
                    directory.FullName, "src", "UmamusumeAssUpdater", "main.cpp")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate solution root from {AppContext.BaseDirectory}");
    }
}
