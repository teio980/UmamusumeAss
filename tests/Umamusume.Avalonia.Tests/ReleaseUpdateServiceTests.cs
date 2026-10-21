using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Umamusume.Avalonia.Services;
using Xunit;

namespace Umamusume.Avalonia.Tests;

public sealed class ReleaseUpdateServiceTests
{
    [Fact]
    public async Task UpdateSelectionIgnoresDraftPrereleaseWrongPlatformAndSkippedVersion()
    {
        var platform = OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : "linux";
        var suffix = $"{platform}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
        object Release(string tag, bool draft = false, bool prerelease = false, string? assetSuffix = null) => new
        {
            tag_name = tag, html_url = "https://github.com/teio980/UmamusumeAss/releases/tag/" + tag, draft, prerelease,
            assets = new[] { new { name = $"UmamusumeAss-{assetSuffix ?? suffix}.app.zip", browser_download_url = "https://github.com/teio980/UmamusumeAss/releases/download/test/app.zip", digest = "sha256:abc" } }
        };
        var handler = new StubHandler(JsonSerializer.Serialize(new[] { Release("v8.0.0", true), Release("v7.0.0", prerelease: true), Release("v6.0.0", assetSuffix: "other-platform"), Release("v5.0.0"), Release("v4.0.0"), Release("v1.0.0") }));
        var updates = new ReleaseUpdateService(new HttpClient(handler));
        Assert.Equal("v4.0.0", (await updates.FindUpdateAsync(new Version(2, 0, 0), "v5.0.0"))?.Version);
        Assert.Null(await updates.FindUpdateAsync(new Version(5, 0, 0), null));
    }

    [Fact]
    public async Task FailedIntegrityCheckNeverLeavesAnInstallableArchive()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uma-update-test-" + Guid.NewGuid().ToString("N"));
        var updates = new ReleaseUpdateService(new HttpClient(new StubHandler("corrupted")));
        try
        {
            var release = new ReleaseDownload("v2.0.0", "https://github.com/teio980/UmamusumeAss/releases", "app.zip", "https://github.com/teio980/UmamusumeAss/releases/download/v2.0.0/app.zip", "sha256:" + new string('0', 64));
            await Assert.ThrowsAsync<InvalidDataException>(() => updates.DownloadAsync(release, directory));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task VerifiedArchiveIsSavedAfterHashCheck()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uma-update-test-" + Guid.NewGuid().ToString("N"));
        var bytes = Encoding.UTF8.GetBytes("archive fixture");
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        var updates = new ReleaseUpdateService(new HttpClient(new StubHandler("archive fixture")));
        try
        {
            var path = await updates.DownloadAsync(new("v2.0.0", "https://github.com/teio980/UmamusumeAss/releases", "app.zip", "https://github.com/teio980/UmamusumeAss/releases/download/v2.0.0/app.zip", "sha256:" + digest), directory);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MissingDigestAndUntrustedDownloadAreRejectedBeforeNetworkAccess()
    {
        var handler = new StubHandler("");
        var updates = new ReleaseUpdateService(new HttpClient(handler));
        await Assert.ThrowsAsync<InvalidDataException>(() => updates.DownloadAsync(new("v2", "https://github.com", "app.zip", "https://example.org/app.zip", "sha256:abc"), Path.GetTempPath()));
        await Assert.ThrowsAsync<InvalidDataException>(() => updates.DownloadAsync(new("v2", "https://github.com", "app.zip", "https://github.com/app.zip", null), Path.GetTempPath()));
        Assert.Equal(0, handler.RequestCount);
    }

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") });
        }
    }
}
