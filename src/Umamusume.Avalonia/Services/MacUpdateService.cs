using System.Net.Http;
using System.Text;
using System.Text.Json;
using Umamusume.AppCore.Platform;
using Umamusume.UpdateCore;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Update;

namespace Umamusume.Avalonia.Services;

public sealed record MacUpdateCheck(
    string CurrentProgramVersion,
    string CurrentResourceVersion,
    UpdateManifest? ProgramManifest,
    GitHubRelease? ProgramRelease,
    UpdateManifest? ResourceManifest,
    GitHubRelease? ResourceRelease,
    string? Error = null)
{
    public bool HasProgramUpdate => ProgramManifest is not null && ProgramRelease is not null;
    public bool HasResourceUpdate => ResourceManifest is not null && ResourceRelease is not null;
}

/// <summary>Signed full-only update discovery and staging for macOS arm64.</summary>
public sealed class MacUpdateService
{
    private readonly GitHubReleaseClient _releases;
    private readonly ManifestVerifier _verifier;
    private readonly PackageStager _stager;
    private readonly string _userDataRoot;
    private readonly string _updatesRoot;
    private sealed record StagedReference(string OperationId, string Scope, string AssetName);

    public MacUpdateService(HttpClient? httpClient = null, string? userDataRoot = null)
        : this(httpClient, userDataRoot, new ManifestVerifier())
    {
    }

    internal MacUpdateService(HttpClient? httpClient, string? userDataRoot, ManifestVerifier verifier)
    {
        _userDataRoot = Path.GetFullPath(userDataRoot ?? PlatformPaths.GetDefaultUserDataDirectory());
        _updatesRoot = Path.Combine(_userDataRoot, "updates");
        _releases = new GitHubReleaseClient(httpClient, Path.Combine(_updatesRoot, "release-cache"));
        _verifier = verifier;
        _stager = new PackageStager(_releases, _verifier, _updatesRoot);
    }

    internal static string ProgramManifestName(string version) => $"osx-arm64-program-manifest-{version}.json";
    internal static string ResourceManifestName(string revision) => $"osx-arm64-resource-manifest-{revision}.json";

    public async Task<MacUpdateCheck> CheckAsync(
        string currentProgramVersion,
        string currentResourceVersion,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.Arm64)
            throw new PlatformNotSupportedException("Signed automatic updates are supported only on macOS arm64.");
        var releases = await _releases.GetStableReleasesAsync(cancellationToken).ConfigureAwait(false);
        var programCandidates = releases
            .Where(release => GitHubReleaseClient.IsProgramTag(release.Tag))
            .OrderByDescending(release => SemVersion.TryParse(release.Tag[1..], out var v) ? v : default)
            .ToArray();
        var resourceCandidates = releases
            .Where(release => GitHubReleaseClient.IsResourceTag(release.Tag))
            .OrderByDescending(release => release.Tag, ResourceTagComparer.Instance)
            .ToArray();

        UpdateManifest? program = null;
        GitHubRelease? programRelease = null;
        foreach (var candidate in programCandidates)
        {
            program = await ReadManifestAsync(candidate, ProgramManifestName(candidate.Tag[1..]),
                currentProgramVersion, cancellationToken).ConfigureAwait(false);
            if (program is not null && CompareVersions(program.Version, currentProgramVersion) > 0)
            { programRelease = candidate; break; }
            program = null;
        }
        UpdateManifest? resource = null;
        GitHubRelease? resourceRelease = null;
        foreach (var candidate in resourceCandidates)
        {
            resource = await ReadManifestAsync(candidate, ResourceManifestName(candidate.Tag[10..]),
                currentProgramVersion, cancellationToken).ConfigureAwait(false);
            if (resource is not null && CompareResourceVersions(resource.Version, currentResourceVersion) > 0)
            { resourceRelease = candidate; break; }
            resource = null;
        }

        return new MacUpdateCheck(currentProgramVersion, currentResourceVersion,
            program, programRelease, resource, resourceRelease);
    }

    public Task<StagedUpdate> DownloadProgramAsync(
        MacUpdateCheck check,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default) => DownloadAsync(
            check.ProgramManifest ?? throw new InvalidOperationException("No program update is available."),
            check.ProgramRelease ?? throw new InvalidOperationException("No program release is available."),
            UpdateScope.Program, progress, cancellationToken);

    public Task<StagedUpdate> DownloadResourceAsync(
        MacUpdateCheck check,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default) => DownloadAsync(
            check.ResourceManifest ?? throw new InvalidOperationException("No resource update is available."),
            check.ResourceRelease ?? throw new InvalidOperationException("No resource release is available."),
            UpdateScope.Resource, progress, cancellationToken);

    public StagedUpdate? RestoreStaged(UpdateScope scope, string currentProgramVersion, string currentResourceVersion)
    {
        string referencePath = GetReferencePath(scope);
        // This is also called after cache cleanup. Do not let staged-reference
        // recovery follow an updates root redirected through a symbolic link.
        if (!HasNoLinkedManagedAncestors(referencePath)) return null;
        bool restored = false;
        try
        {
            if (!File.Exists(referencePath)) return null;
            var reference = JsonSerializer.Deserialize<StagedReference>(File.ReadAllText(referencePath));
            if (reference is null || reference.Scope is null
                || !reference.Scope.Equals(scope.ToString(), StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(reference.OperationId)
                || string.IsNullOrWhiteSpace(reference.AssetName)) return null;
            string operationRoot = Path.Combine(_updatesRoot, reference.OperationId);
            if (!HasNoLinkedManagedAncestors(operationRoot)) return null;
            var staged = _stager.TryRestore(reference.OperationId, scope, reference.AssetName,
                "macos", "arm64", allowDeltas: false);
            if (staged is null
                || !MacManifestCompatibility.IsCompatible(staged.Manifest, currentProgramVersion)
                || (scope == UpdateScope.Program
                    ? CompareVersions(staged.Manifest.Version, currentProgramVersion) <= 0
                    : CompareResourceVersions(staged.Manifest.Version, currentResourceVersion) <= 0))
                return null;
            restored = true;
            return staged;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or FormatException or ArgumentException or InvalidDataException)
        {
            return null;
        }
        finally
        {
            if (!restored)
            {
                try { if (File.Exists(referencePath)) File.Delete(referencePath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public void RememberStaged(StagedUpdate staged)
    {
        if (staged.Scope is not (UpdateScope.Program or UpdateScope.Resource))
            throw new ArgumentException("Only program and resource packages can be restored.", nameof(staged));
        Directory.CreateDirectory(_updatesRoot);
        string path = GetReferencePath(staged.Scope);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(new StagedReference(
            staged.OperationId, staged.Scope.ToString(), staged.Asset.AssetName)));
        File.Move(temporary, path, overwrite: true);
    }

    public void ForgetStaged(UpdateScope scope)
    {
        string path = GetReferencePath(scope);
        if (File.Exists(path)) File.Delete(path);
    }

    private string GetReferencePath(UpdateScope scope) => Path.Combine(_updatesRoot,
        scope == UpdateScope.Program ? "staged-program.json" : "staged-resource.json");

    private bool HasNoLinkedManagedAncestors(string path)
    {
        string fullRoot = Path.GetFullPath(_userDataRoot);
        string fullPath = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, PathComparison)) return false;

        for (string? current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            if (PathEquals(current, fullRoot)) return true;
        }
        return false;
    }

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private async Task<UpdateManifest?> ReadManifestAsync(
        GitHubRelease release,
        string manifestName,
        string appVersion,
        CancellationToken cancellationToken)
    {
        var manifestAsset = release.Assets.SingleOrDefault(asset => asset.Name.Equals(manifestName, StringComparison.Ordinal));
        string signatureName = Path.ChangeExtension(manifestName, ".sig");
        var signatureAsset = release.Assets.SingleOrDefault(asset => asset.Name.Equals(signatureName, StringComparison.Ordinal));
        if (manifestAsset is null || signatureAsset is null) return null;
        var manifestBytes = await _releases.DownloadAssetAsync(manifestAsset, Path.Combine(_updatesRoot, "checks", manifestName), cancellationToken: cancellationToken).ConfigureAwait(false);
        var signatureBytes = await _releases.DownloadAssetAsync(signatureAsset, Path.Combine(_updatesRoot, "checks", signatureName), cancellationToken: cancellationToken).ConfigureAwait(false);
        string signatureText = Encoding.UTF8.GetString(signatureBytes);
        byte[] signature = ManifestVerifier.DecodeSignature(signatureText);
        if (!_verifier.Verify(manifestBytes, signature)) return null;
        var manifest = ManifestVerifier.Deserialize(manifestBytes);
        if (!ManifestVerifier.IsCanonicalManifest(manifest, release.Tag, "macos", "arm64", allowDeltas: false)
            || !MacManifestCompatibility.IsCompatible(manifest, appVersion))
            return null;
        return manifest;
    }

    private async Task<StagedUpdate> DownloadAsync(
        UpdateManifest manifest,
        GitHubRelease release,
        UpdateScope scope,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!ManifestVerifier.IsCanonicalManifest(manifest, release.Tag, "macos", "arm64", allowDeltas: false))
            throw new InvalidDataException("The signed update manifest is not a macOS arm64 full package.");
        var package = manifest.Assets.SingleOrDefault(asset => asset.Type.Equals("full", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The signed manifest has no full package.");
        var githubAsset = release.Assets.SingleOrDefault(asset => asset.Name.Equals(package.AssetName, StringComparison.Ordinal))
            ?? throw new InvalidDataException("The signed package is missing from its GitHub release.");
        var manifestFileName = scope == UpdateScope.Program
            ? ProgramManifestName(manifest.Version)
            : ResourceManifestName(manifest.Version);
        var manifestAsset = release.Assets.Single(asset => asset.Name.Equals(manifestFileName, StringComparison.Ordinal));
        var signatureName = Path.ChangeExtension(manifestFileName, ".sig");
        var signatureAsset = release.Assets.Single(asset => asset.Name.Equals(signatureName, StringComparison.Ordinal));
        var manifestBytes = await _releases.DownloadAssetAsync(manifestAsset,
            Path.Combine(_updatesRoot, "checks", manifestFileName), cancellationToken: cancellationToken).ConfigureAwait(false);
        var signatureText = await _releases.DownloadAssetAsync(signatureAsset,
            Path.Combine(_updatesRoot, "checks", signatureName), cancellationToken: cancellationToken).ConfigureAwait(false);
        return await _stager.StageAsync(
            new UpdatePlan(manifest, package, release.Tag), githubAsset, scope,
            manifestBytes, Encoding.UTF8.GetString(signatureText) is { } text
                ? Convert.FromBase64String(text.Trim()) : [],
            progress, "macos", "arm64", allowDeltas: false, cancellationToken).ConfigureAwait(false);
    }

    private static int CompareVersions(string left, string right) =>
        SemVersion.TryParse(left, out var l) && SemVersion.TryParse(right, out var r) ? l.CompareTo(r) : string.CompareOrdinal(left, right);

    private static int CompareResourceVersions(string left, string right)
    {
        var a = left.Split('.').Select(part => int.TryParse(part, out int number) ? number : -1).ToArray();
        var b = right.Split('.').Select(part => int.TryParse(part, out int number) ? number : -1).ToArray();
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int result = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
            if (result != 0) return result;
        }
        return 0;
    }

    private sealed class ResourceTagComparer : IComparer<string>
    {
        public static ResourceTagComparer Instance { get; } = new();
        public int Compare(string? x, string? y) => CompareResourceVersions(
            x?.StartsWith("resource-v", StringComparison.Ordinal) == true ? x[10..] : x ?? string.Empty,
            y?.StartsWith("resource-v", StringComparison.Ordinal) == true ? y[10..] : y ?? string.Empty);
    }
}
