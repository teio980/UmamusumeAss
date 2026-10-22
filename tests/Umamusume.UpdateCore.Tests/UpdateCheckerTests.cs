using System.Net;
using System.Text;
using System.Text.Json;
using Umamusume.UpdateCore;
using UmamusumeWpfGui.Services.Update;
using Xunit;

namespace Umamusume.UpdateCore.Tests;

public sealed class UpdateCheckerTests
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

    private static GitHubReleaseClient CreateMockClient(string releasesJson)
    {
        var handler = new MockHttpHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(releasesJson, Encoding.UTF8, "application/json")
            };
        });
        return new GitHubReleaseClient(new HttpClient(handler));
    }

    [Fact]
    public async Task FindsUpdateWhenOnlyFullPackageAvailable()
    {
        var releases = new[]
        {
            new
            {
                tag_name = "v0.3.1",
                draft = false,
                prerelease = false,
                html_url = "https://github.com/teio980/UmamusumeAss/releases/tag/v0.3.1",
                assets = new object[]
                {
                    new
                    {
                        name = "UmamusumeAss-v0.3.1-osx-arm64.app.zip",
                        browser_download_url = "https://github.com/teio980/UmamusumeAss/releases/download/v0.3.1/UmamusumeAss-v0.3.1-osx-arm64.app.zip",
                        size = 1000L,
                        digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                    },
                    new
                    {
                        name = "UmamusumeAss-v0.3.1-win-x64-full.zip",
                        browser_download_url = "https://github.com/teio980/UmamusumeAss/releases/download/v0.3.1/UmamusumeAss-v0.3.1-win-x64-full.zip",
                        size = 2000L,
                        digest = "sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"
                    }
                }
            }
        };

        var client = CreateMockClient(JsonSerializer.Serialize(releases));
        var checker = new UpdateChecker(client);

        var osxResult = await checker.CheckForUpdateAsync("0.3.0", platform: "osx-arm64");
        Assert.True(osxResult.HasUpdate);
        Assert.NotNull(osxResult.Update);
        Assert.True(osxResult.CanVerify);
        Assert.Equal("v0.3.1", osxResult.Update!.Tag);
        Assert.Equal("0.3.1", osxResult.Update.Version);
        Assert.Equal("UmamusumeAss-v0.3.1-osx-arm64.app.zip", osxResult.Update.Asset.Name);
        Assert.Equal("https://github.com/teio980/UmamusumeAss/releases/download/v0.3.1/UmamusumeAss-v0.3.1-osx-arm64.app.zip", osxResult.Update.Asset.DownloadUrl);
        Assert.Equal("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", osxResult.Update.ExpectedSha256);

        var winResult = await checker.CheckForUpdateAsync("0.3.0", platform: "windows-x64");
        Assert.True(winResult.HasUpdate);
        Assert.NotNull(winResult.Update);
        Assert.Equal("v0.3.1", winResult.Update!.Tag);
        Assert.Equal("UmamusumeAss-v0.3.1-win-x64-full.zip", winResult.Update.Asset.Name);
    }

    [Fact]
    public async Task IgnoresDraftsPrereleasesAndResourceReleases()
    {
        var releases = new[]
        {
            new
            {
                tag_name = "v0.4.0",
                draft = true,
                prerelease = false,
                assets = new object[]
                {
                    new
                    {
                        name = "UmamusumeAss-v0.4.0-osx-arm64.app.zip",
                        browser_download_url = "https://github.com/teio980/UmamusumeAss/releases/download/v0.4.0/UmamusumeAss-v0.4.0-osx-arm64.app.zip",
                        size = 1000L,
                        digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                    }
                }
            },
            new
            {
                tag_name = "v0.3.9-beta",
                draft = false,
                prerelease = true,
                assets = new object[]
                {
                    new
                    {
                        name = "UmamusumeAss-v0.3.9-osx-arm64.app.zip",
                        browser_download_url = "https://github.com/teio980/UmamusumeAss/releases/download/v0.3.9/UmamusumeAss-v0.3.9-osx-arm64.app.zip",
                        size = 1000L,
                        digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                    }
                }
            },
            new
            {
                tag_name = "resource-v2026.10.22.1",
                draft = false,
                prerelease = false,
                assets = new object[]
                {
                    new
                    {
                        name = "UmamusumeAss-resource-v2026.10.22.1-osx-arm64-full.zip",
                        browser_download_url = "https://github.com/teio980/UmamusumeAss/releases/download/resource-v2026.10.22.1/UmamusumeAss-resource-v2026.10.22.1-osx-arm64-full.zip",
                        size = 1000L,
                        digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                    }
                }
            },
            new
            {
                tag_name = "v0.3.5",
                draft = false,
                prerelease = false,
                assets = new object[]
                {
                    new
                    {
                        name = "UmamusumeAss-v0.3.5-osx-arm64.app.zip",
                        browser_download_url = "https://github.com/teio980/UmamusumeAss/releases/download/v0.3.5/UmamusumeAss-v0.3.5-osx-arm64.app.zip",
                        size = 1000L,
                        digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                    }
                }
            }
        };

        var client = CreateMockClient(JsonSerializer.Serialize(releases));
        var checker = new UpdateChecker(client);

        var result = await checker.CheckForUpdateAsync("0.3.0", platform: "osx-arm64");
        Assert.True(result.HasUpdate);
        Assert.Equal("v0.3.5", result.Update!.Tag);
    }

    [Fact]
    public async Task SkipsReleasesMissingCurrentPlatformPackage()
    {
        var releases = new[]
        {
            new
            {
                tag_name = "v0.3.6",
                draft = false,
                prerelease = false,
                assets = new object[]
                {
                    new
                    {
                        name = "UmamusumeAss-v0.3.6-win-x64-full.zip",
                        browser_download_url = "https://github.com/teio980/UmamusumeAss/releases/download/v0.3.6/UmamusumeAss-v0.3.6-win-x64-full.zip",
                        size = 2000L,
                        digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                    }
                }
            },
            new
            {
                tag_name = "v0.3.5",
                draft = false,
                prerelease = false,
                assets = new object[]
                {
                    new
                    {
                        name = "UmamusumeAss-v0.3.5-osx-arm64.app.zip",
                        browser_download_url = "https://github.com/teio980/UmamusumeAss/releases/download/v0.3.5/UmamusumeAss-v0.3.5-osx-arm64.app.zip",
                        size = 1000L,
                        digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                    }
                }
            }
        };

        var client = CreateMockClient(JsonSerializer.Serialize(releases));
        var checker = new UpdateChecker(client);

        // On macOS, v0.3.6 has no macOS package, so it skips v0.3.6 and selects v0.3.5
        var macResult = await checker.CheckForUpdateAsync("0.3.0", platform: "osx-arm64");
        Assert.True(macResult.HasUpdate);
        Assert.Equal("v0.3.5", macResult.Update!.Tag);

        // On Windows, v0.3.6 has windows package, so it selects v0.3.6
        var winResult = await checker.CheckForUpdateAsync("0.3.0", platform: "windows-x64");
        Assert.True(winResult.HasUpdate);
        Assert.Equal("v0.3.6", winResult.Update!.Tag);
    }

    [Fact]
    public async Task ReportsUpToDateWhenVersionNotElevatedOrSkipped()
    {
        var releases = new[]
        {
            new
            {
                tag_name = "v0.3.5",
                draft = false,
                prerelease = false,
                assets = new object[]
                {
                    new
                    {
                        name = "UmamusumeAss-v0.3.5-osx-arm64.app.zip",
                        browser_download_url = "https://github.com/teio980/UmamusumeAss/releases/download/v0.3.5/UmamusumeAss-v0.3.5-osx-arm64.app.zip",
                        size = 1000L,
                        digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                    }
                }
            }
        };

        var client = CreateMockClient(JsonSerializer.Serialize(releases));
        var checker = new UpdateChecker(client);

        // Equal version
        var equalResult = await checker.CheckForUpdateAsync("0.3.5", platform: "osx-arm64");
        Assert.False(equalResult.HasUpdate);

        // Higher installed version
        var higherResult = await checker.CheckForUpdateAsync("0.4.0", platform: "osx-arm64");
        Assert.False(higherResult.HasUpdate);

        // Skipped version
        var skippedResult = await checker.CheckForUpdateAsync("0.3.0", skippedVersion: "0.3.5", platform: "osx-arm64");
        Assert.False(skippedResult.HasUpdate);
    }

    [Fact]
    public async Task MissingOrInvalidDigestReportedExplicitlyNotUpToDate()
    {
        var releases = new[]
        {
            new
            {
                tag_name = "v0.3.5",
                draft = false,
                prerelease = false,
                assets = new object[]
                {
                    new
                    {
                        name = "UmamusumeAss-v0.3.5-osx-arm64.app.zip",
                        browser_download_url = "https://github.com/teio980/UmamusumeAss/releases/download/v0.3.5/UmamusumeAss-v0.3.5-osx-arm64.app.zip",
                        size = 1000L
                        // Missing digest property!
                    }
                }
            }
        };

        var client = CreateMockClient(JsonSerializer.Serialize(releases));
        var checker = new UpdateChecker(client);

        var result = await checker.CheckForUpdateAsync("0.3.0", platform: "osx-arm64");
        // Must NOT be reported as no update / up to date!
        Assert.True(result.HasUpdate);
        Assert.NotNull(result.Update);
        Assert.False(result.CanVerify);
        Assert.False(result.Update!.HasValidDigest);
        Assert.NotNull(result.Update.VerificationError);
        Assert.Contains("SHA-256", result.Update.VerificationError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedDigestReportedAsInvalid()
    {
        var releases = new[]
        {
            new
            {
                tag_name = "v0.3.5",
                draft = false,
                prerelease = false,
                assets = new object[]
                {
                    new
                    {
                        name = "UmamusumeAss-v0.3.5-osx-arm64.app.zip",
                        browser_download_url = "https://github.com/teio980/UmamusumeAss/releases/download/v0.3.5/UmamusumeAss-v0.3.5-osx-arm64.app.zip",
                        size = 1000L,
                        digest = "sha256:not-a-valid-hex-digest-too-short"
                    }
                }
            }
        };

        var client = CreateMockClient(JsonSerializer.Serialize(releases));
        var checker = new UpdateChecker(client);

        var result = await checker.CheckForUpdateAsync("0.3.0", platform: "osx-arm64");
        Assert.True(result.HasUpdate);
        Assert.NotNull(result.Update);
        Assert.False(result.CanVerify);
        Assert.False(result.Update!.HasValidDigest);
    }
}
