using System.Security.Cryptography;
using System.Text.Json;
using Umamusume.AppCore.Platform;
using Umamusume.UpdateCore;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Update;

namespace Umamusume.Avalonia.Services;

/// <summary>Persistent immutable resource revisions and an atomically replaced active pointer.</summary>
public sealed class MacResourceStore : IResourceSourceProvider
{
    private sealed record ActivePointer(string Version, string RelativePath);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _userDataRoot;
    private readonly string _revisionsRoot;
    private readonly string _pointerPath;
    private readonly ManifestVerifier _verifier;

    public MacResourceStore(string? userDataRoot = null, ManifestVerifier? verifier = null)
    {
        _userDataRoot = Path.GetFullPath(userDataRoot ?? PlatformPaths.GetDefaultUserDataDirectory());
        _revisionsRoot = Path.Combine(_userDataRoot, "resources", "revisions");
        _pointerPath = Path.Combine(_userDataRoot, "resources", "active.json");
        _verifier = verifier ?? new ManifestVerifier();
    }

    public string ResourcesRoot => Path.GetDirectoryName(_pointerPath)!;
    public string RevisionsRoot => _revisionsRoot;
    public string UserDataRoot => _userDataRoot;

    public async Task<byte[]?> CaptureActivePointerAsync(CancellationToken cancellationToken = default) =>
        File.Exists(_pointerPath)
            ? await File.ReadAllBytesAsync(_pointerPath, cancellationToken).ConfigureAwait(false)
            : null;

    public Task<string?> GetPreferredResourceSourceRootAsync(
        string bundledSourceRoot,
        string userDataRoot,
        CancellationToken cancellationToken = default)
    {
        // Cancel independent resource updates. Startup on both sides is based on the built-in resources
        // of the current installation package; old downloaded resource versions no longer take precedence.
        return Task.FromResult<string?>(null);
    }

    public async Task<string> InstallRevisionAsync(StagedUpdate staged, CancellationToken cancellationToken = default)
    {
        if (staged.Scope != UpdateScope.Resource || !staged.Manifest.Kind.Equals("resource", StringComparison.Ordinal))
            throw new InvalidDataException("Only a signed resource package can be installed as a resource revision.");
        if (!IsSafeVersion(staged.Manifest.Version)
            || !MacManifestCompatibility.IsCompatible(staged.Manifest, MacManifestCompatibility.CurrentAppVersion()))
            throw new InvalidDataException("The resource revision is incompatible with this application or operating system.");
        Directory.CreateDirectory(_revisionsRoot);
        string revisionRoot = Path.Combine(_revisionsRoot, staged.Manifest.Version);
        if (Directory.Exists(revisionRoot))
        {
            var existing = await VerifyRevisionAsync(revisionRoot, cancellationToken).ConfigureAwait(false);
            if (existing is null || !existing.Value.Manifest.Version.Equals(staged.Manifest.Version, StringComparison.Ordinal))
                throw new InvalidDataException("An invalid resource revision already occupies this version.");
            return existing.Value.PayloadRoot;
        }

        string temporaryRoot = Path.Combine(_revisionsRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            string payloadRoot = Path.Combine(temporaryRoot, "payload");
            await CopyTreeAsync(staged.PayloadRoot, payloadRoot, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(temporaryRoot, "manifest.json"),
                await File.ReadAllBytesAsync(staged.ManifestPath, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            File.Copy(staged.SignaturePath, Path.Combine(temporaryRoot, "manifest.sig"), overwrite: false);
            if (await VerifyRevisionAsync(temporaryRoot, cancellationToken).ConfigureAwait(false) is null)
                throw new InvalidDataException("The staged resource revision failed signature or exact inventory validation.");
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(temporaryRoot, revisionRoot);
            return Path.Combine(revisionRoot, "payload");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    public static async Task<string> ReadBundledVersionAsync(CancellationToken cancellationToken = default)
    {
        string? path = MacBundleFiles.FindResourceFile(AppContext.BaseDirectory, "resource-version.txt");
        if (path is null) return "0.0.0.0";
        return (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
    }

    public async Task CommitActiveAsync(string version, CancellationToken cancellationToken = default)
    {
        if (!IsSafeVersion(version)) throw new InvalidDataException("Invalid resource revision.");
        string revisionRoot = Path.Combine(_revisionsRoot, version);
        var verified = await VerifyRevisionAsync(revisionRoot, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The resource revision failed signature or inventory validation.");
        if (!verified.Manifest.Version.Equals(version, StringComparison.Ordinal))
            throw new InvalidDataException("The resource revision version did not match its signed manifest.");
        if (!MacManifestCompatibility.IsCompatible(verified.Manifest, MacManifestCompatibility.CurrentAppVersion()))
            throw new InvalidDataException("The resource revision is incompatible with this application or operating system.");
        Directory.CreateDirectory(ResourcesRoot);
        var pointer = new ActivePointer(version, Path.GetRelativePath(ResourcesRoot, revisionRoot));
        string temporary = _pointerPath + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(pointer, JsonOptions), cancellationToken).ConfigureAwait(false);
        try { File.Move(temporary, _pointerPath, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task RestoreActivePointerAsync(byte[]? snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot is null)
        {
            if (File.Exists(_pointerPath)) File.Delete(_pointerPath);
            return;
        }
        Directory.CreateDirectory(ResourcesRoot);
        string temporary = _pointerPath + ".restore-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(temporary, snapshot, cancellationToken).ConfigureAwait(false);
        try { File.Move(temporary, _pointerPath, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<string> ReadActiveVersionAsync(CancellationToken cancellationToken = default)
    {
        string bundledVersion = await ReadBundledVersionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_pointerPath)) return bundledVersion;
            var pointer = JsonSerializer.Deserialize<ActivePointer>(await File.ReadAllTextAsync(_pointerPath, cancellationToken).ConfigureAwait(false));
            if (pointer is null || pointer.Version is null || pointer.RelativePath is null || !IsSafeVersion(pointer.Version)) return bundledVersion;
            var expectedRoot = Path.GetFullPath(Path.Combine(_revisionsRoot, pointer.Version));
            var actualRoot = Path.GetFullPath(Path.Combine(ResourcesRoot, pointer.RelativePath));
            if (!expectedRoot.Equals(actualRoot, StringComparison.Ordinal)
                || CompareResourceVersions(pointer.Version, bundledVersion) < 0)
                return bundledVersion;
            var revision = await VerifyRevisionAsync(actualRoot, cancellationToken).ConfigureAwait(false);
            if (revision is null || !revision.Value.Manifest.Version.Equals(pointer.Version, StringComparison.Ordinal)
                || !MacManifestCompatibility.IsCompatible(revision.Value.Manifest, MacManifestCompatibility.CurrentAppVersion()))
                return bundledVersion;
            return CompareResourceVersions(pointer.Version, bundledVersion) >= 0 ? pointer.Version : bundledVersion;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or CryptographicException or InvalidDataException or FormatException or ArgumentException)
        {
            return bundledVersion;
        }
    }

    private async Task<(string PayloadRoot, UpdateManifest Manifest)?> VerifyRevisionAsync(string revisionRoot, CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(revisionRoot, "manifest.json");
        string signaturePath = Path.Combine(revisionRoot, "manifest.sig");
        string payloadRoot = Path.Combine(revisionRoot, "payload");
        if (!File.Exists(manifestPath) || !File.Exists(signaturePath) || !Directory.Exists(payloadRoot)) return null;
        byte[] bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        byte[] signature = ManifestVerifier.DecodeSignature(await File.ReadAllTextAsync(signaturePath, cancellationToken).ConfigureAwait(false));
        if (!_verifier.Verify(bytes, signature)) return null;
        var manifest = ManifestVerifier.Deserialize(bytes);
        if (!ManifestVerifier.IsCanonicalManifest(manifest, manifest.ReleaseTag, "macos", "arm64", allowDeltas: false)
            || !manifest.Kind.Equals("resource", StringComparison.Ordinal)
            || !MacManifestCompatibility.IsCompatible(manifest, MacManifestCompatibility.CurrentAppVersion())) return null;
        var full = manifest.Assets.SingleOrDefault(asset => asset.Type.Equals("full", StringComparison.OrdinalIgnoreCase));
        if (full is null || !await HasExactInventoryAsync(payloadRoot, full, cancellationToken).ConfigureAwait(false)
            || !UpdateSelector.VerifyManagedTree(payloadRoot, full, full.TargetTreeSha256)) return null;
        return (payloadRoot, manifest);
    }

    private static async Task<bool> HasExactInventoryAsync(string root, UpdateAsset asset, CancellationToken cancellationToken)
    {
        try
        {
            if (asset.Files.Count > UpdatePathSafety.MaxFiles || asset.Files.Sum(file => file.Size) > UpdatePathSafety.MaxExpandedBytes) return false;
            string[] actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            string[] expected = asset.Files.Select(file => file.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (!actual.SequenceEqual(expected, StringComparer.Ordinal)) return false;
            foreach (var file in asset.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = UpdatePathSafety.ResolveUnderRoot(root, file.Path);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
                using var stream = File.OpenRead(path);
                if (stream.Length != file.Size || !Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                        .Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool IsSafeVersion(string value) => value.Length <= 32 && value.All(character => char.IsAsciiDigit(character) || character == '.');

    private static int CompareResourceVersions(string left, string right)
    {
        var leftParts = left.Split('.').Select(part => int.TryParse(part, out int value) ? value : -1).ToArray();
        var rightParts = right.Split('.').Select(part => int.TryParse(part, out int value) ? value : -1).ToArray();
        for (int i = 0; i < Math.Max(leftParts.Length, rightParts.Length); i++)
        {
            int comparison = (i < leftParts.Length ? leftParts[i] : 0).CompareTo(i < rightParts.Length ? rightParts[i] : 0);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static bool IsInside(string root, string path)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static async Task CopyTreeAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
            if (OperatingSystem.IsMacOS())
                File.SetUnixFileMode(target, File.GetUnixFileMode(file));
        }
    }
}
