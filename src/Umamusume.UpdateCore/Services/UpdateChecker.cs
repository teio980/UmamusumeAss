using System.Net.Http;
using System.Runtime.InteropServices;
using Umamusume.UpdateCore;
using UmamusumeWpfGui.Services.Update;

namespace Umamusume.UpdateCore;

public static class UpdatePlatformHelper
{
    public static string GetCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
            return "windows-x64";
        if (OperatingSystem.IsMacOS())
            return "osx-arm64";
        return "linux-x64";
    }

    public static string GetExpectedPackageName(string platform, string version)
    {
        var cleanVersion = version.TrimStart('v', 'V');
        if (platform.StartsWith("win", StringComparison.OrdinalIgnoreCase))
            return $"UmamusumeAss-v{cleanVersion}-win-x64-full.zip";
        if (platform.StartsWith("osx", StringComparison.OrdinalIgnoreCase) || platform.StartsWith("mac", StringComparison.OrdinalIgnoreCase))
            return $"UmamusumeAss-v{cleanVersion}-osx-arm64.app.zip";
        throw new PlatformNotSupportedException($"Unsupported platform: {platform}");
    }

    public static bool TryParseSha256Digest(string? digest, out string sha256Hex)
    {
        sha256Hex = string.Empty;
        if (string.IsNullOrWhiteSpace(digest)) return false;
        var trimmed = digest.Trim();
        if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[7..].Trim();
        if (trimmed.Length == 64 && trimmed.All(Uri.IsHexDigit))
        {
            sha256Hex = trimmed.ToUpperInvariant();
            return true;
        }
        return false;
    }
}

public sealed class UpdateChecker
{
    private readonly GitHubReleaseClient _releases;

    public UpdateChecker(GitHubReleaseClient? releases = null)
    {
        _releases = releases ?? new GitHubReleaseClient();
    }

    public async Task<FullUpdateCheckResult> CheckForUpdateAsync(
        string currentVersion,
        string? skippedVersion = null,
        string? platform = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        if (!SemVersion.TryParse(currentVersion, out var current))
            throw new ArgumentException($"Invalid current version: {currentVersion}", nameof(currentVersion));

        var targetPlatform = platform ?? UpdatePlatformHelper.GetCurrentPlatform();

        IReadOnlyList<GitHubRelease> releases;
        try
        {
            releases = await _releases.GetStableReleasesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new FullUpdateCheckResult(null, ex.Message);
        }

        // Only formal vX.Y.Z program releases, ordered descending
        var candidates = releases
            .Where(r => GitHubReleaseClient.IsProgramTag(r.Tag))
            .Select(r => (Release: r, Success: SemVersion.TryParse(r.Tag, out var v), Version: v))
            .Where(c => c.Success)
            .OrderByDescending(c => c.Version)
            .ToList();

        foreach (var candidate in candidates)
        {
            if (candidate.Version <= current)
                break; // Because candidates are sorted descending, subsequent ones are also <= current

            var versionString = candidate.Version.ToString();
            var releaseTag = candidate.Release.Tag;

            // Check if skipped
            if (!string.IsNullOrWhiteSpace(skippedVersion))
            {
                var cleanSkipped = skippedVersion.Trim().TrimStart('v', 'V');
                if (cleanSkipped.Equals(versionString, StringComparison.OrdinalIgnoreCase)
                    || skippedVersion.Trim().Equals(releaseTag, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            var expectedFileName = UpdatePlatformHelper.GetExpectedPackageName(targetPlatform, versionString);
            var matchingAsset = candidate.Release.Assets.FirstOrDefault(a =>
                a.Name.Equals(expectedFileName, StringComparison.OrdinalIgnoreCase));

            // If machine package is missing from this release, skip and check next candidate
            if (matchingAsset is null)
                continue;

            // Package found. Check for valid GitHub SHA-256 digest
            var releasePageUrl = !string.IsNullOrWhiteSpace(candidate.Release.HtmlUrl)
                ? candidate.Release.HtmlUrl
                : $"https://github.com/{GitHubReleaseClient.RepositoryOwner}/{GitHubReleaseClient.RepositoryName}/releases/tag/{releaseTag}";

            if (!UpdatePlatformHelper.TryParseSha256Digest(matchingAsset.Digest, out var sha256Hex))
            {
                var verificationError = "选中包缺少有效 GitHub SHA-256 摘要，无法自动校验。可重试或从发布页面手动下载。";
                var invalidDescriptor = new FullUpdateDescriptor(
                    versionString,
                    releaseTag,
                    targetPlatform,
                    matchingAsset,
                    ExpectedSha256: null,
                    releasePageUrl,
                    VerificationError: verificationError);

                return new FullUpdateCheckResult(invalidDescriptor, verificationError);
            }

            var descriptor = new FullUpdateDescriptor(
                versionString,
                releaseTag,
                targetPlatform,
                matchingAsset,
                ExpectedSha256: sha256Hex,
                releasePageUrl,
                VerificationError: null);

            return new FullUpdateCheckResult(descriptor);
        }

        return new FullUpdateCheckResult(null);
    }
}
