using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Umamusume.Avalonia.Services;
using Umamusume.UpdateCore;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Update;
using Xunit;

namespace Umamusume.Avalonia.Tests;

public sealed class MacUpdateServiceTests
{
    private static readonly JsonSerializerOptions CamelCaseJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public async Task FindsSignsStagesAndRestoresMacProgramUsingExactReleaseAssets()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            return;

        string root = Path.Combine(Path.GetTempPath(), "uma-mac-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = new ManifestVerifier(signer.ExportSubjectPublicKeyInfo());
        const string version = "0.3.1";
        const string appFile = "UmamusumeAss.app/Contents/MacOS/Umamusume.Avalonia";
        byte[] executable = Encoding.UTF8.GetBytes("macOS ARM64 app fixture");
        string packageName = $"UmamusumeAss-v{version}-osx-arm64.app.zip";
        string manifestName = MacUpdateService.ProgramManifestName(version);
        Assert.Equal("osx-arm64-program-manifest-0.3.1.json", manifestName);
        Assert.Equal("osx-arm64-resource-manifest-2026.10.21.1.json",
            MacUpdateService.ResourceManifestName("2026.10.21.1"));

        try
        {
            byte[] packageBytes = CreateExecutablePackage(root, appFile, executable);
            var fileEntry = new UpdateFileEntry
            {
                Path = appFile,
                Size = executable.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(executable)),
            };
            var asset = new UpdateAsset
            {
                Type = "full",
                AssetName = packageName,
                Size = packageBytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(packageBytes)),
                TargetTreeSha256 = ComputeTreeHash(appFile, executable),
                Files = [fileEntry],
            };
            var manifest = new UpdateManifest
            {
                Kind = "program",
                TargetOs = "macos",
                TargetArch = "arm64",
                MinOsVersion = "14.0",
                Version = version,
                ReleaseTag = "v" + version,
                BundledResourceVersion = "2026.10.21.1",
                MinAppVersion = "0.3.0",
                Assets = [asset],
            };
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, CamelCaseJson);
            byte[] signature = signer.SignData(manifestBytes, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            const string tag = "v0.3.1";
            const string baseUrl = "https://github.com/teio980/UmamusumeAss/releases/download/" + tag + "/";
            var signatureName = Path.ChangeExtension(manifestName, ".sig");
            var responseAssets = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [packageName] = packageBytes,
                [manifestName] = manifestBytes,
                [signatureName] = Encoding.UTF8.GetBytes(Convert.ToBase64String(signature)),
            };
            var latestWindowsOnlyAsset = new
            {
                name = "UmamusumeAss-v0.3.2-win-x64-full.zip",
                browser_download_url = "https://github.com/teio980/UmamusumeAss/releases/download/v0.3.2/windows.zip",
                size = 8L,
            };
            var macAssets = responseAssets.Select(pair => new
            {
                name = pair.Key,
                browser_download_url = baseUrl + Uri.EscapeDataString(pair.Key),
                size = (long)pair.Value.Length,
            }).ToArray();
            string releasesJson = JsonSerializer.Serialize(new object[]
            {
                new { tag_name = "v0.3.2", draft = false, prerelease = false, assets = new[] { latestWindowsOnlyAsset } },
                new { tag_name = tag, draft = false, prerelease = false, assets = macAssets },
            });
            var handler = new ReleaseAssetHandler(releasesJson, responseAssets);
            var service = new MacUpdateService(new HttpClient(handler), root, verifier);

            var check = await service.CheckAsync("0.3.0", "2026.10.21.1");
            Assert.Equal(version, check.ProgramManifest?.Version);
            Assert.Equal(tag, check.ProgramRelease?.Tag);
            Assert.Equal("osx-arm64-program-manifest-0.3.1.json", check.ProgramRelease?.Assets
                .Single(item => item.Name == manifestName).Name);
            Assert.Contains(check.ProgramRelease!.Assets, item => item.Name == signatureName);

            var staged = await service.DownloadProgramAsync(check);
            service.RememberStaged(staged);
            var restored = service.RestoreStaged(UpdateScope.Program, "0.3.0", "2026.10.21.1");

            Assert.NotNull(restored);
            string stagedExecutable = Path.Combine(restored!.PayloadRoot, appFile.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(executable, await File.ReadAllBytesAsync(stagedExecutable));
            Assert.True((File.GetUnixFileMode(stagedExecutable) & UnixFileMode.UserExecute) != 0);
            Assert.Contains("osx-arm64-program-manifest-$v.json",
                await File.ReadAllTextAsync(FindSolutionFile(".github/workflows/release.yml")), StringComparison.Ordinal);
            Assert.Contains("osx-arm64-resource-manifest-$rv.json",
                await File.ReadAllTextAsync(FindSolutionFile(".github/workflows/release.yml")), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MacInventoryRejectsCaseUnicodeAndFileDirectoryCollisions()
    {
        string[][] collisions =
        [
            ["A/x", "a/y"],
            ["folder/Foo.json", "folder/foo.json"],
            ["a", "a/child"],
            ["é/file", "e\u0301/other"],
        ];

        foreach (string[] paths in collisions)
            Assert.Throws<InvalidDataException>(() => UpdatePathSafety.ValidateUniquePaths(paths, macOS: true));
    }

    [Fact]
    public async Task MacPackageStagingRejectsSymlinkZipEntries()
    {
        string root = Path.Combine(Path.GetTempPath(), "uma-mac-symlink-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = new ManifestVerifier(signer.ExportSubjectPublicKeyInfo());
        byte[] linkTarget = Encoding.UTF8.GetBytes("../outside");
        string linkPath = "UmamusumeAss.app/Contents/MacOS/launch";
        string packagePath = Path.Combine(root, "symlink.zip");
        try
        {
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(linkPath, CompressionLevel.Optimal);
                entry.ExternalAttributes = unchecked((0xA1FF) << 16);
                using var stream = entry.Open();
                stream.Write(linkTarget);
            }
            byte[] package = await File.ReadAllBytesAsync(packagePath);
            var asset = new UpdateAsset
            {
                Type = "full",
                AssetName = "UmamusumeAss-v0.3.1-osx-arm64.app.zip",
                Size = package.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(package)),
                TargetTreeSha256 = new string('A', 64),
                Files = [new UpdateFileEntry
                {
                    Path = linkPath,
                    Size = linkTarget.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(linkTarget)),
                }],
            };
            var manifest = new UpdateManifest
            {
                Kind = "program", TargetOs = "macos", TargetArch = "arm64", MinOsVersion = "14.0",
                Version = "0.3.1", ReleaseTag = "v0.3.1", MinAppVersion = "0.3.0", Assets = [asset],
            };
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, CamelCaseJson);
            byte[] signature = signer.SignData(manifestBytes, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var stager = new PackageStager(new GitHubReleaseClient(new HttpClient(new ByteAssetHandler(package))), verifier,
                Path.Combine(root, "updates"));
            var githubAsset = new GitHubAsset(asset.AssetName,
                "https://github.com/teio980/UmamusumeAss/releases/download/v0.3.1/package.zip", package.Length);

            await Assert.ThrowsAsync<InvalidDataException>(() => stager.StageAsync(
                new UpdatePlan(manifest, asset, manifest.ReleaseTag), githubAsset, UpdateScope.Program,
                manifestBytes, signature, expectedTargetOs: "macos", expectedTargetArch: "arm64", allowDeltas: false));
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, "updates")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] CreateExecutablePackage(string root, string path, byte[] content)
    {
        string pathName = Path.Combine(root, "package.zip");
        using var archive = ZipFile.Open(pathName, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.ExternalAttributes = unchecked((0x81ED) << 16);
        using (var output = entry.Open()) output.Write(content);
        archive.Dispose();
        return File.ReadAllBytes(pathName);
    }

    private static string ComputeTreeHash(string path, byte[] content)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(path));
        hash.AppendData([0]);
        hash.AppendData(content);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string FindSolutionFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "CMakePresets.json")))
            current = current.Parent;
        return Path.Combine(current?.FullName ?? throw new DirectoryNotFoundException("Solution root was not found."), relativePath);
    }

    private sealed class ReleaseAssetHandler(string releasesJson, IReadOnlyDictionary<string, byte[]> assets) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (uri.Contains("api.github.com/repos/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releasesJson, Encoding.UTF8, "application/json"),
                });
            string name = Uri.UnescapeDataString(Path.GetFileName(request.RequestUri?.AbsolutePath ?? string.Empty));
            if (!assets.TryGetValue(name, out var content))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
        }
    }

    private sealed class ByteAssetHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}
