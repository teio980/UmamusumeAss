using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.Services.Update;

public sealed class PackageStager
{
    private readonly GitHubReleaseClient _releases;
    private readonly ManifestVerifier _verifier;
    private readonly string _updatesRoot;

    public PackageStager(
        GitHubReleaseClient releases,
        ManifestVerifier verifier,
        string? updatesRoot = null)
    {
        _releases = releases ?? throw new ArgumentNullException(nameof(releases));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _updatesRoot = Path.GetFullPath(updatesRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UmamusumeAss", "updates"));
    }

    public async Task<StagedUpdate> StageAsync(
        UpdatePlan plan,
        GitHubAsset asset,
        UpdateScope scope,
        ReadOnlyMemory<byte> manifestBytes,
        ReadOnlyMemory<byte> signature,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(asset);
        if (!_verifier.Verify(manifestBytes.Span, signature.Span))
            throw new CryptographicException("Release manifest signature is invalid.");
        var signedManifest = ManifestVerifier.Deserialize(manifestBytes.Span);
        var signedAsset = signedManifest.Assets.FirstOrDefault(item =>
            item.AssetName.Equals(plan.Asset.AssetName, StringComparison.Ordinal));
        var expectedKind = scope == UpdateScope.Resource ? "resource" : "program";
        if (!signedManifest.ReleaseTag.Equals(plan.ReleaseTag, StringComparison.Ordinal)
            || !signedManifest.Version.Equals(plan.Manifest.Version, StringComparison.Ordinal)
            || !signedManifest.Kind.Equals(expectedKind, StringComparison.Ordinal)
            || !ManifestVerifier.IsCanonicalManifest(signedManifest, plan.ReleaseTag)
            || signedAsset is null
            || !asset.Name.Equals(signedAsset.AssetName, StringComparison.Ordinal)
            || asset.Size != signedAsset.Size
            || !JsonSerializer.Serialize(signedAsset).Equals(
                JsonSerializer.Serialize(plan.Asset), StringComparison.Ordinal))
            throw new InvalidDataException("Selected update asset does not match signed manifest.");
        // All later operations use the signed object, never a caller-supplied
        // copy that only happened to share its name and package hash.
        plan = plan with { Asset = signedAsset };
        var operationId = Guid.NewGuid().ToString("N");
        var operationRoot = Path.Combine(_updatesRoot, operationId);
        var payloadRoot = Path.Combine(operationRoot, "payload");
        Directory.CreateDirectory(payloadRoot);

        var packagePath = Path.Combine(operationRoot, asset.Name);
        var partialPath = packagePath + ".part";
        progress?.Report(new UpdateProgress(
            "Downloading update",
            File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0,
            asset.Size));
        var downloadProgress = progress is null
            ? null
            : new ForwardingProgress(progress, asset.Size);
        await _releases.DownloadAssetFileAsync(
                asset,
                packagePath,
                progress: downloadProgress,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new UpdateProgress("Verifying update", 0, 0));
        if (!string.IsNullOrWhiteSpace(plan.Asset.Sha256)
            && !plan.Asset.Sha256.Equals(ManifestVerifier.Sha256File(packagePath), StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("Release package SHA-256 did not match its manifest.");
        if (plan.Asset.Size > 0 && new FileInfo(packagePath).Length != plan.Asset.Size)
            throw new InvalidDataException("Release package size did not match its manifest.");

        var extractedPaths = await ExtractSafeAsync(packagePath, payloadRoot, cancellationToken)
            .ConfigureAwait(false);
        var expectedPaths = plan.Asset.Files.Select(file => file.Path)
            .ToHashSet(StringComparer.Ordinal);
        if (!extractedPaths.SetEquals(expectedPaths))
            throw new InvalidDataException("Update package file set does not match the signed manifest.");
        foreach (var file in plan.Asset.Files)
        {
            var source = UpdatePathSafety.ResolveUnderRoot(payloadRoot, file.Path);
            if (!File.Exists(source))
                throw new InvalidDataException($"Package is missing manifest file: {file.Path}");
            var info = new FileInfo(source);
            if (info.Length != file.Size || !file.Sha256.Equals(
                    ManifestVerifier.Sha256File(source), StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException($"Package file hash mismatch: {file.Path}");
        }
        foreach (var deleted in plan.Asset.Deletes)
            UpdatePathSafety.ValidateRelative(deleted);

        var manifestPath = Path.Combine(operationRoot, "manifest.json");
        var signaturePath = Path.Combine(operationRoot, "manifest.sig");
        await File.WriteAllBytesAsync(manifestPath, manifestBytes.ToArray(), cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(signaturePath, Convert.ToBase64String(signature.ToArray()), cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new UpdateProgress("Update ready", asset.Size, asset.Size));
        return new StagedUpdate(
            operationId,
            scope,
            plan.Manifest,
            plan.Asset,
            packagePath,
            payloadRoot,
            manifestPath,
            signaturePath);
    }

    public StagedUpdate? TryRestore(
        string operationId,
        UpdateScope scope,
        string assetName)
    {
        if (!Guid.TryParseExact(operationId, "N", out _)
            || scope == UpdateScope.All
            || string.IsNullOrWhiteSpace(assetName))
            return null;

        try
        {
            var operationRoot = Path.Combine(_updatesRoot, operationId);
            var manifestPath = Path.Combine(operationRoot, "manifest.json");
            var signaturePath = Path.Combine(operationRoot, "manifest.sig");
            if (!File.Exists(manifestPath) || !File.Exists(signaturePath))
                return null;

            var manifestBytes = File.ReadAllBytes(manifestPath);
            var signature = ManifestVerifier.DecodeSignature(File.ReadAllText(signaturePath));
            if (!_verifier.Verify(manifestBytes, signature))
                return null;

            var manifest = ManifestVerifier.Deserialize(manifestBytes);
            var expectedKind = scope == UpdateScope.Resource ? "resource" : "program";
            if (!manifest.Kind.Equals(expectedKind, StringComparison.Ordinal)
                || !ManifestVerifier.IsCanonicalManifest(manifest, manifest.ReleaseTag))
                return null;

            var asset = manifest.Assets.FirstOrDefault(item =>
                item.AssetName.Equals(assetName, StringComparison.Ordinal));
            if (asset is null)
                return null;

            var packagePath = Path.Combine(operationRoot, asset.AssetName);
            var payloadRoot = Path.Combine(operationRoot, "payload");
            if (!File.Exists(packagePath)
                || !Directory.Exists(payloadRoot)
                || new FileInfo(packagePath).Length != asset.Size
                || !ManifestVerifier.Sha256File(packagePath).Equals(
                    asset.Sha256, StringComparison.OrdinalIgnoreCase)
                || !VerifyPayload(payloadRoot, asset))
                return null;

            return new StagedUpdate(
                operationId,
                scope,
                manifest,
                asset,
                packagePath,
                payloadRoot,
                manifestPath,
                signaturePath);
        }
        catch (Exception)
        {
            // A cache is only an optimization. Any incomplete, stale, or
            // tampered entry is ignored and the normal download path remains.
            return null;
        }
    }

    private static bool VerifyPayload(string payloadRoot, UpdateAsset asset)
    {
        var expectedPaths = asset.Files
            .Select(file => file.Path)
            .ToHashSet(StringComparer.Ordinal);
        var actualPaths = Directory
            .EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(payloadRoot, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        if (!actualPaths.SetEquals(expectedPaths))
            return false;

        foreach (var file in asset.Files)
        {
            var path = UpdatePathSafety.ResolveUnderRoot(payloadRoot, file.Path);
            var info = new FileInfo(path);
            if (!info.Exists
                || info.Length != file.Size
                || !ManifestVerifier.Sha256File(path).Equals(
                    file.Sha256, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        foreach (var deleted in asset.Deletes)
            UpdatePathSafety.ValidateRelative(deleted);
        return true;
    }

    private sealed class ForwardingProgress(
        IProgress<UpdateProgress> target,
        long total) : IProgress<long>
    {
        public void Report(long completed) => target.Report(
            new UpdateProgress("Downloading update", completed, total));
    }

    private static async Task<HashSet<string>> ExtractSafeAsync(
        string packagePath,
        string destination,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var extractedPaths = new HashSet<string>(StringComparer.Ordinal);
        if (archive.Entries.Count > UpdatePathSafety.MaxFiles)
            throw new InvalidDataException("Update package contains too many files.");
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/'))
            {
                UpdatePathSafety.ValidateRelative(name.TrimEnd('/'));
                Directory.CreateDirectory(UpdatePathSafety.ResolveUnderRoot(destination, name.TrimEnd('/')));
                continue;
            }
            UpdatePathSafety.ValidateRelative(name);
            if (entry.Length > UpdatePathSafety.MaxFileBytes)
                throw new InvalidDataException("Update package contains an oversized file.");
            total = checked(total + entry.Length);
            if (total > UpdatePathSafety.MaxExpandedBytes)
                throw new InvalidDataException("Update package expands beyond the safety limit.");
            var target = UpdatePathSafety.ResolveUnderRoot(destination, name);
            if (!extractedPaths.Add(name))
                throw new InvalidDataException($"Update package contains duplicate file: {name}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
        return extractedPaths;
    }
}
