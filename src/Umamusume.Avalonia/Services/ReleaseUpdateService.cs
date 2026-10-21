using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Umamusume.AppCore.Services;

namespace Umamusume.Avalonia.Services;

public sealed record ReleaseDownload(string Version, string PageUrl, string FileName, string DownloadUrl, string? Digest);

public sealed class ReleaseUpdateService
{
    public const string ReleasesUrl = "https://github.com/teio980/UmamusumeAss/releases";
    private static readonly HttpClient SharedHttp = CreateClient();
    private readonly HttpClient _http;
    private readonly ILocalizationService? _localizationService;

    public ReleaseUpdateService(HttpClient? httpClient = null, ILocalizationService? localizationService = null)
    {
        _http = httpClient ?? SharedHttp;
        _localizationService = localizationService;
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("UmamusumeAss-Avalonia/1.0");
        return http;
    }

    public async Task<ReleaseDownload?> FindUpdateAsync(Version currentVersion, string? skipped, CancellationToken cancellationToken = default)
    {
        var releases = await _http.GetFromJsonAsync<Release[]>("https://api.github.com/repos/teio980/UmamusumeAss/releases?per_page=30", cancellationToken) ?? [];
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var platform = OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : "linux";
        foreach (var release in releases.Where(r => !r.Draft && !r.Prerelease).OrderByDescending(r => ParseVersion(r.Tag)))
        {
            if (release.Tag == skipped || release.Tag.TrimStart('v') == skipped || ParseVersion(release.Tag) <= currentVersion) continue;
            var asset = release.Assets.FirstOrDefault(a => a.Name.Contains($"{platform}-{arch}", StringComparison.OrdinalIgnoreCase)
                && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (asset != null && IsGitHubUrl(asset.Url) && IsGitHubUrl(release.Url))
                return new(release.Tag, release.Url, Path.GetFileName(asset.Name), asset.Url, asset.Digest);
        }
        return null;
    }

    public async Task<string> DownloadAsync(ReleaseDownload release, string directory, CancellationToken cancellationToken = default)
    {
        if (!IsGitHubUrl(release.DownloadUrl)) throw new InvalidDataException(UiText.Get(_localizationService, "更新包 URL 无效。", "The update package URL is invalid."));
        if (release.Digest == null || !release.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(UiText.Get(_localizationService, "此发布没有 SHA-256 摘要，请从发布页面下载并核验。", "This release has no SHA-256 digest. Download it from the release page and verify it manually."));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, Path.GetFileName(release.FileName));
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var input = await _http.GetStreamAsync(release.DownloadUrl, cancellationToken))
            await using (var output = File.Create(temporary)) await input.CopyToAsync(output, cancellationToken);
            await using var verify = File.OpenRead(temporary);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken));
            if (!digest.Equals(release.Digest[7..], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(UiText.Get(_localizationService, "更新包 SHA-256 校验失败。", "Update package SHA-256 verification failed."));
            verify.Close();
            File.Move(temporary, target, true);
            return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Version ParseVersion(string tag) => Version.TryParse(tag.TrimStart('v'), out var version) ? version : new Version(0, 0);
    private static bool IsGitHubUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "github.com";
    private sealed record Release(
        [property: JsonPropertyName("tag_name")] string Tag,
        [property: JsonPropertyName("html_url")] string Url,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] Asset[] Assets);
    private sealed record Asset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string Url,
        [property: JsonPropertyName("digest")] string? Digest);
}
