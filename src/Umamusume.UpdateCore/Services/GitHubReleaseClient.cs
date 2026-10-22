using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Umamusume.UpdateCore;

namespace Umamusume.UpdateCore
{
    public sealed record GitHubRelease(string Tag, IReadOnlyList<GitHubAsset> Assets, string HtmlUrl = "");
    public sealed record GitHubAsset(string Name, string DownloadUrl, long Size, string? Digest = null);
}

namespace UmamusumeWpfGui.Services.Update
{
    public sealed class GitHubReleaseClient
    {
        public const string RepositoryOwner = "teio980";
        public const string RepositoryName = "UmamusumeAss";

        private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "github.com",
            "api.github.com",
            "objects.githubusercontent.com",
            "release-assets.githubusercontent.com",
        };

        private readonly HttpClient _http;
        private readonly string _releaseCacheDirectory;

        public GitHubReleaseClient(HttpClient? httpClient = null, string? releaseCacheDirectory = null)
        {
            _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            _releaseCacheDirectory = Path.GetFullPath(releaseCacheDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UmamusumeAss", "update-checks"));
            if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
                _http.DefaultRequestHeaders.UserAgent.ParseAdd("UmamusumeAss-Updater/0.2");
        }

        public async Task<IReadOnlyList<GitHubRelease>> GetStableReleasesAsync(
            CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases?per_page=100");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            var cachePath = Path.Combine(_releaseCacheDirectory, "github-releases.json");
            var etagPath = Path.Combine(_releaseCacheDirectory, "github-releases.etag");
            if (File.Exists(etagPath))
            {
                var etag = await File.ReadAllTextAsync(etagPath, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(etag))
                    request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag.Trim()));
            }
            using var response = await SendApprovedAsync(request, cancellationToken)
                .ConfigureAwait(false);
            byte[] responseBytes;
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (!File.Exists(cachePath))
                    throw new InvalidDataException("GitHub returned 304 without a release cache.");
                responseBytes = await File.ReadAllBytesAsync(cachePath, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                response.EnsureSuccessStatusCode();
                responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
                Directory.CreateDirectory(_releaseCacheDirectory);
                var temporary = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
                await File.WriteAllBytesAsync(temporary, responseBytes, cancellationToken)
                    .ConfigureAwait(false);
                File.Move(temporary, cachePath, overwrite: true);
                if (response.Headers.ETag is not null)
                    await File.WriteAllTextAsync(
                            etagPath, response.Headers.ETag.Tag, cancellationToken)
                        .ConfigureAwait(false);
            }
            using var document = JsonDocument.Parse(responseBytes);

            var result = new List<GitHubRelease>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var tag = item.GetProperty("tag_name").GetString() ?? string.Empty;
                var draft = item.GetProperty("draft").GetBoolean();
                var prerelease = item.GetProperty("prerelease").GetBoolean();
                var htmlUrl = item.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? string.Empty : string.Empty;
                if (draft || prerelease || !IsSupportedTag(tag))
                    continue;
                var assets = new List<GitHubAsset>();
                foreach (var asset in item.GetProperty("assets").EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? string.Empty;
                    var url = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
                    string? digest = null;
                    if (asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String)
                        digest = d.GetString();
                    if (name.Length > 0 && IsAllowedUri(url))
                        assets.Add(new GitHubAsset(name, url, asset.GetProperty("size").GetInt64(), digest));
                }
                result.Add(new GitHubRelease(tag, assets, htmlUrl));
            }
            return result;
        }

        public async Task<GitHubAsset?> RefreshAssetAsync(
            string tag,
            string assetName,
            CancellationToken cancellationToken = default)
        {
            var tagNormalized = tag.StartsWith('v') ? tag : "v" + tag;
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/tags/{tagNormalized}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await SendApprovedAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            JsonElement targetRelease = root;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in root.EnumerateArray())
                {
                    if (r.TryGetProperty("tag_name", out var t) &&
                        (string.Equals(t.GetString(), tagNormalized, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(t.GetString(), tag.TrimStart('v'), StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(t.GetString(), "v" + tag.TrimStart('v'), StringComparison.OrdinalIgnoreCase)))
                    {
                        targetRelease = r;
                        break;
                    }
                }
            }

            if (targetRelease.ValueKind == JsonValueKind.Object && targetRelease.TryGetProperty("assets", out var assetsElem))
            {
                foreach (var asset in assetsElem.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? string.Empty;
                    if (!name.Equals(assetName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var url = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
                    var size = asset.GetProperty("size").GetInt64();
                    string? digest = null;
                    if (asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String)
                        digest = d.GetString();
                    return new GitHubAsset(name, url, size, digest);
                }
            }
            return null;
        }

        public async Task<byte[]> DownloadAssetAsync(
            GitHubAsset asset,
            string destination,
            IProgress<long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsAllowedUri(asset.DownloadUrl))
                throw new InvalidDataException("Release asset URL is not an approved GitHub URL.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var partial = destination + ".part";
            long existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
            if (existing > 0)
                request.Headers.Range = new RangeHeaderValue(existing, null);
            using var response = await SendApprovedAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (existing > 0 && response.StatusCode == HttpStatusCode.OK)
            {
                existing = 0;
                File.Delete(partial);
            }
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(
                partial,
                existing > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                long total = existing;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    total += read;
                    progress?.Report(total);
                }
            }
            File.Move(partial, destination, overwrite: true);
            return await File.ReadAllBytesAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        public async Task DownloadAssetFileAsync(
            GitHubAsset asset,
            string destination,
            IProgress<long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsAllowedUri(asset.DownloadUrl))
                throw new InvalidDataException("Release asset URL is not an approved GitHub URL.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var partial = destination + ".part";
            long existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
            if (existing > 0)
                request.Headers.Range = new RangeHeaderValue(existing, null);
            using var response = await SendApprovedAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (existing > 0 && response.StatusCode == HttpStatusCode.OK)
            {
                existing = 0;
                File.Delete(partial);
            }
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(
                partial,
                existing > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                long total = existing;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    total += read;
                    progress?.Report(total);
                }
            }
            File.Move(partial, destination, overwrite: true);
        }

        public static bool IsSupportedTag(string tag) =>
            IsProgramTag(tag) || IsResourceTag(tag);

        public static bool IsProgramTag(string tag)
        {
            if (!tag.StartsWith('v') || tag.StartsWith("resource-v", StringComparison.Ordinal)) return false;
            return SemVersion.TryParse(tag[1..], out _);
        }

        public static bool IsResourceTag(string tag)
        {
            if (!tag.StartsWith("resource-v", StringComparison.Ordinal)) return false;
            var parts = tag[10..].Split('.');
            return parts.Length == 4
                && parts[0].Length == 4 && parts[1].Length == 2 && parts[2].Length == 2
                && parts.All(part => part.All(char.IsDigit));
        }

        public static bool IsAllowedUri(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && AllowedHosts.Contains(uri.Host);
        }

        private async Task<HttpResponseMessage> SendApprovedAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var current = request;
            for (var redirect = 0; redirect <= 5; redirect++)
            {
                var response = await _http.SendAsync(
                    current, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotModified)
                    return response;
                if ((int)response.StatusCode is < 300 or >= 400)
                    return response;
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || !IsAllowedUri(location.IsAbsoluteUri
                        ? location.AbsoluteUri
                        : new Uri(current.RequestUri!, location).AbsoluteUri))
                    throw new InvalidDataException("GitHub redirected the update request to an unapproved host.");
                if (redirect == 5)
                    throw new InvalidDataException("Too many GitHub redirects.");
                var next = new HttpRequestMessage(current.Method, location);
                foreach (var header in current.Headers)
                    next.Headers.TryAddWithoutValidation(header.Key, header.Value);
                current = next;
            }
            throw new InvalidDataException("GitHub redirect validation failed.");
        }
    }
}
