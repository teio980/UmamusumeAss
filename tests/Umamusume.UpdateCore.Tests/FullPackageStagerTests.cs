using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Umamusume.UpdateCore;
using UmamusumeWpfGui.Services.Update;
using Xunit;

namespace Umamusume.UpdateCore.Tests;

public sealed class FullPackageStagerTests
{
    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    private static byte[] CreateZipPackage(string internalVersion, string platform = "osx-arm64", Action<ZipArchive>? customize = null)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            string versionPath = platform.StartsWith("osx", StringComparison.OrdinalIgnoreCase)
                ? "UmamusumeAss.app/Contents/Resources/version.txt"
                : "version.txt";

            var versionEntry = archive.CreateEntry(versionPath);
            using (var s = versionEntry.Open())
            {
                s.Write(Encoding.UTF8.GetBytes(internalVersion));
            }

            var fileEntry = archive.CreateEntry("payload_test.txt");
            using (var s = fileEntry.Open())
            {
                s.Write(Encoding.UTF8.GetBytes("test payload content"));
            }

            customize?.Invoke(archive);
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task StagesValidPackageAndGeneratesLocalInventory()
    {
        string root = Path.Combine(Path.GetTempPath(), "stager-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string version = "0.3.5";
            string tag = "v" + version;
            string assetName = $"UmamusumeAss-v{version}-osx-arm64.app.zip";
            byte[] packageBytes = CreateZipPackage(version, "osx-arm64");
            string packageSha256 = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();

            var handler = new MockHttpHandler(req =>
            {
                string uri = req.RequestUri?.AbsoluteUri ?? string.Empty;
                if (uri.Contains("api.github.com/repos/", StringComparison.Ordinal))
                {
                    var releaseJson = JsonSerializer.Serialize(new
                    {
                        tag_name = tag,
                        draft = false,
                        prerelease = false,
                        assets = new[]
                        {
                            new
                            {
                                name = assetName,
                                browser_download_url = $"https://github.com/teio980/UmamusumeAss/releases/download/{tag}/{assetName}",
                                size = (long)packageBytes.Length,
                                digest = "sha256:" + packageSha256
                            }
                        }
                    });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(packageBytes)
                };
            });

            var client = new GitHubReleaseClient(new HttpClient(handler));
            var stager = new FullPackageStager(client, root);

            var asset = new GitHubAsset(assetName, $"https://github.com/teio980/UmamusumeAss/releases/download/{tag}/{assetName}", packageBytes.Length, "sha256:" + packageSha256);
            var descriptor = new FullUpdateDescriptor(
                version,
                tag,
                "osx-arm64",
                asset,
                packageSha256,
                "https://github.com/teio980/UmamusumeAss/releases/tag/" + tag);

            var staged = await stager.StageAsync(descriptor);
            Assert.NotNull(staged);
            Assert.Equal(version, staged.Version);
            Assert.Equal("osx-arm64", staged.Platform);
            Assert.True(File.Exists(staged.PackagePath));
            Assert.True(File.Exists(staged.InventoryPath));
            Assert.True(Directory.Exists(staged.PayloadRoot));

            // Verify local inventory content
            var inventory = JsonSerializer.Deserialize<LocalFileInventory>(await File.ReadAllTextAsync(staged.InventoryPath));
            Assert.NotNull(inventory);
            Assert.Equal(version, inventory!.Version);
            Assert.Equal("osx-arm64", inventory.Platform);
            Assert.Equal(assetName, inventory.PackageName);
            Assert.Equal(packageBytes.Length, inventory.PackageSize);
            Assert.NotEmpty(inventory.Files);

            // Test restore
            var restored = stager.TryRestoreStaged();
            Assert.NotNull(restored);
            Assert.Equal(staged.OperationId, restored!.OperationId);

            // Test tampering detection
            string testFile = Path.Combine(staged.PayloadRoot, "payload_test.txt");
            await File.WriteAllTextAsync(testFile, "tampered content");
            var tamperedRestore = stager.TryRestoreStaged();
            Assert.Null(tamperedRestore);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task RejectsPackageWithMismatchedInternalVersion()
    {
        string root = Path.Combine(Path.GetTempPath(), "stager-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string version = "0.3.5";
            string tag = "v" + version;
            string assetName = $"UmamusumeAss-v{version}-osx-arm64.app.zip";
            // Package internal version is 0.3.4, but release tag is 0.3.5!
            byte[] packageBytes = CreateZipPackage("0.3.4", "osx-arm64");
            string packageSha256 = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();

            var handler = new MockHttpHandler(req =>
            {
                string uri = req.RequestUri?.AbsoluteUri ?? string.Empty;
                if (uri.Contains("api.github.com/repos/", StringComparison.Ordinal))
                {
                    var releaseJson = JsonSerializer.Serialize(new
                    {
                        tag_name = tag,
                        draft = false,
                        prerelease = false,
                        assets = new[]
                        {
                            new
                            {
                                name = assetName,
                                browser_download_url = $"https://github.com/teio980/UmamusumeAss/releases/download/{tag}/{assetName}",
                                size = (long)packageBytes.Length,
                                digest = "sha256:" + packageSha256
                            }
                        }
                    });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(packageBytes)
                };
            });

            var client = new GitHubReleaseClient(new HttpClient(handler));
            var stager = new FullPackageStager(client, root);

            var asset = new GitHubAsset(assetName, $"https://github.com/teio980/UmamusumeAss/releases/download/{tag}/{assetName}", packageBytes.Length, "sha256:" + packageSha256);
            var descriptor = new FullUpdateDescriptor(
                version,
                tag,
                "osx-arm64",
                asset,
                packageSha256,
                "https://github.com/teio980/UmamusumeAss/releases/tag/" + tag);

            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => stager.StageAsync(descriptor));
            Assert.Contains("does not match", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task RejectsPackageWithMismatchedDigest()
    {
        string root = Path.Combine(Path.GetTempPath(), "stager-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string version = "0.3.5";
            string tag = "v" + version;
            string assetName = $"UmamusumeAss-v{version}-osx-arm64.app.zip";
            byte[] packageBytes = CreateZipPackage(version, "osx-arm64");
            string packageSha256 = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
            string wrongSha256 = new string('0', 64);

            var handler = new MockHttpHandler(req =>
            {
                string uri = req.RequestUri?.AbsoluteUri ?? string.Empty;
                if (uri.Contains("api.github.com/repos/", StringComparison.Ordinal))
                {
                    var releaseJson = JsonSerializer.Serialize(new
                    {
                        tag_name = tag,
                        draft = false,
                        prerelease = false,
                        assets = new[]
                        {
                            new
                            {
                                name = assetName,
                                browser_download_url = $"https://github.com/teio980/UmamusumeAss/releases/download/{tag}/{assetName}",
                                size = (long)packageBytes.Length,
                                digest = "sha256:" + wrongSha256
                            }
                        }
                    });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(packageBytes)
                };
            });

            var client = new GitHubReleaseClient(new HttpClient(handler));
            var stager = new FullPackageStager(client, root);

            var asset = new GitHubAsset(assetName, $"https://github.com/teio980/UmamusumeAss/releases/download/{tag}/{assetName}", packageBytes.Length, "sha256:" + packageSha256);
            var descriptor = new FullUpdateDescriptor(
                version,
                tag,
                "osx-arm64",
                asset,
                packageSha256,
                "https://github.com/teio980/UmamusumeAss/releases/tag/" + tag);

            var ex = await Assert.ThrowsAsync<CryptographicException>(() => stager.StageAsync(descriptor));
            Assert.Contains("SHA-256", ex.Message);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task RejectsZipSlipTraversal()
    {
        string root = Path.Combine(Path.GetTempPath(), "stager-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string version = "0.3.5";
            string tag = "v" + version;
            string assetName = $"UmamusumeAss-v{version}-osx-arm64.app.zip";
            byte[] packageBytes = CreateZipPackage(version, "osx-arm64", archive =>
            {
                var malicious = archive.CreateEntry("../malicious.txt");
                using var s = malicious.Open();
                s.Write(Encoding.UTF8.GetBytes("exploit"));
            });
            string packageSha256 = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();

            var handler = new MockHttpHandler(req =>
            {
                string uri = req.RequestUri?.AbsoluteUri ?? string.Empty;
                if (uri.Contains("api.github.com/repos/", StringComparison.Ordinal))
                {
                    var releaseJson = JsonSerializer.Serialize(new
                    {
                        tag_name = tag,
                        draft = false,
                        prerelease = false,
                        assets = new[]
                        {
                            new
                            {
                                name = assetName,
                                browser_download_url = $"https://github.com/teio980/UmamusumeAss/releases/download/{tag}/{assetName}",
                                size = (long)packageBytes.Length,
                                digest = "sha256:" + packageSha256
                            }
                        }
                    });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(packageBytes)
                };
            });

            var client = new GitHubReleaseClient(new HttpClient(handler));
            var stager = new FullPackageStager(client, root);

            var asset = new GitHubAsset(assetName, $"https://github.com/teio980/UmamusumeAss/releases/download/{tag}/{assetName}", packageBytes.Length, "sha256:" + packageSha256);
            var descriptor = new FullUpdateDescriptor(
                version,
                tag,
                "osx-arm64",
                asset,
                packageSha256,
                "https://github.com/teio980/UmamusumeAss/releases/tag/" + tag);

            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => stager.StageAsync(descriptor));
            Assert.Contains("Illegal update path", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }

    [Fact]
    public void DeterministicOperationIdIsValidGuidFormat()
    {
        string opId = FullPackageStager.GetDeterministicOperationId("v0.3.5", "osx-arm64", "UmamusumeAss-v0.3.5-osx-arm64.app.zip");
        Assert.Equal(32, opId.Length);
        Assert.True(Guid.TryParseExact(opId, "N", out _));

        // Same inputs produce identical ID
        string opId2 = FullPackageStager.GetDeterministicOperationId("v0.3.5", "osx-arm64", "UmamusumeAss-v0.3.5-osx-arm64.app.zip");
        Assert.Equal(opId, opId2);

        // Different tag produces different ID
        string opId3 = FullPackageStager.GetDeterministicOperationId("v0.3.6", "osx-arm64", "UmamusumeAss-v0.3.6-osx-arm64.app.zip");
        Assert.NotEqual(opId, opId3);
    }

    [Fact]
    public async Task ResumesInterruptedDownloadFromPartFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "resume-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string version = "0.3.5";
            string tag = "v" + version;
            string assetName = $"UmamusumeAss-v{version}-osx-arm64.app.zip";
            byte[] packageBytes = CreateZipPackage(version, "osx-arm64");
            string packageSha256 = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();

            // Simulate partial download: write first 10 bytes to .part file
            string opId = FullPackageStager.GetDeterministicOperationId(tag, "osx-arm64", assetName);
            string opRoot = Path.Combine(root, opId);
            Directory.CreateDirectory(opRoot);
            string partPath = Path.Combine(opRoot, assetName + ".part");
            int existingBytes = 10;
            await File.WriteAllBytesAsync(partPath, packageBytes[..existingBytes]);

            bool receivedRangeHeader = false;
            long requestedRangeStart = -1;

            var handler = new MockHttpHandler(req =>
            {
                string uri = req.RequestUri?.AbsoluteUri ?? string.Empty;
                if (uri.Contains("api.github.com/repos/", StringComparison.Ordinal))
                {
                    var releaseJson = JsonSerializer.Serialize(new
                    {
                        tag_name = tag,
                        draft = false,
                        prerelease = false,
                        assets = new[]
                        {
                            new
                            {
                                name = assetName,
                                browser_download_url = $"https://github.com/teio980/UmamusumeAss/releases/download/{tag}/{assetName}",
                                size = (long)packageBytes.Length,
                                digest = "sha256:" + packageSha256
                            }
                        }
                    });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
                    };
                }

                if (req.Headers.Range is not null && req.Headers.Range.Ranges.Count > 0)
                {
                    receivedRangeHeader = true;
                    requestedRangeStart = req.Headers.Range.Ranges.First().From ?? 0;
                    byte[] remainingBytes = packageBytes[(int)requestedRangeStart..];
                    var partialResponse = new HttpResponseMessage(HttpStatusCode.PartialContent)
                    {
                        Content = new ByteArrayContent(remainingBytes)
                    };
                    partialResponse.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(
                        requestedRangeStart, packageBytes.Length - 1, packageBytes.Length);
                    return partialResponse;
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(packageBytes)
                };
            });

            var client = new GitHubReleaseClient(new HttpClient(handler));
            var stager = new FullPackageStager(client, root);

            var asset = new GitHubAsset(assetName, $"https://github.com/teio980/UmamusumeAss/releases/download/{tag}/{assetName}", packageBytes.Length, "sha256:" + packageSha256);
            var descriptor = new FullUpdateDescriptor(
                version,
                tag,
                "osx-arm64",
                asset,
                packageSha256,
                "https://github.com/teio980/UmamusumeAss/releases/tag/" + tag);

            var staged = await stager.StageAsync(descriptor);

            Assert.True(receivedRangeHeader);
            Assert.Equal(existingBytes, requestedRangeStart);
            Assert.NotNull(staged);
            Assert.True(File.Exists(staged.PackagePath));
            Assert.Equal(packageBytes.Length, new FileInfo(staged.PackagePath).Length);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
