using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Umamusume.UpdateCore;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Update;

namespace Umamusume.UpdateCore;

public sealed class FullPackageStager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly GitHubReleaseClient _releases;
    private readonly string _updatesRoot;

    public FullPackageStager(
        GitHubReleaseClient? releases = null,
        string? updatesRoot = null)
    {
        _releases = releases ?? new GitHubReleaseClient();
        _updatesRoot = Path.GetFullPath(updatesRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UmamusumeAss", "updates"));
    }

    public string UpdatesRoot => _updatesRoot;

    public async Task<StagedFullUpdate> StageAsync(
        FullUpdateDescriptor descriptor,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.HasValidDigest || string.IsNullOrWhiteSpace(descriptor.ExpectedSha256))
            throw new InvalidOperationException(descriptor.VerificationError ?? "The update package lacks a valid SHA-256 digest.");

        // 1. Refresh asset info before downloading
        progress?.Report(new UpdateProgress("Refreshing asset information", 0, 0));
        var freshAsset = await _releases.RefreshAssetAsync(
            descriptor.Tag, descriptor.Asset.Name, cancellationToken).ConfigureAwait(false)
            ?? descriptor.Asset;

        if (!UpdatePlatformHelper.TryParseSha256Digest(freshAsset.Digest, out var expectedSha256))
        {
            // Fall back to descriptor's expected sha256 if refreshed did not parse
            if (!string.IsNullOrWhiteSpace(descriptor.ExpectedSha256))
                expectedSha256 = descriptor.ExpectedSha256;
            else
                throw new InvalidDataException("Refreshed asset on GitHub lacks a valid SHA-256 digest.");
        }

        var operationId = GetDeterministicOperationId(descriptor.Tag, descriptor.Platform, freshAsset.Name);
        var operationRoot = Path.Combine(_updatesRoot, operationId);
        var payloadRoot = Path.Combine(operationRoot, "payload");
        Directory.CreateDirectory(payloadRoot);

        try
        {
            var packagePath = Path.Combine(operationRoot, freshAsset.Name);
            var partialPath = packagePath + ".part";
            long existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            progress?.Report(new UpdateProgress("Downloading update", existing, freshAsset.Size));

            var downloadProgress = progress is null
                ? null
                : new Progress<long>(bytes => progress.Report(new UpdateProgress("Downloading update", bytes, freshAsset.Size)));

            bool needDownload = true;
            if (File.Exists(packagePath))
            {
                var existingInfo = new FileInfo(packagePath);
                if ((freshAsset.Size <= 0 || existingInfo.Length == freshAsset.Size)
                    && ComputeSha256File(packagePath).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    needDownload = false;
                    progress?.Report(new UpdateProgress("Update package already downloaded", existingInfo.Length, existingInfo.Length));
                }
                else
                {
                    try { File.Delete(packagePath); } catch { }
                }
            }

            if (needDownload)
            {
                await _releases.DownloadAssetFileAsync(
                    freshAsset,
                    packagePath,
                    downloadProgress,
                    cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new UpdateProgress("Verifying update", 0, 0));

            // Verify size
            var downloadedInfo = new FileInfo(packagePath);
            if (freshAsset.Size > 0 && downloadedInfo.Length != freshAsset.Size)
                throw new InvalidDataException($"Downloaded package size ({downloadedInfo.Length}) does not match expected size ({freshAsset.Size}).");

            // Verify SHA-256
            var actualSha256 = ComputeSha256File(packagePath);
            if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException($"Downloaded package SHA-256 ({actualSha256}) did not match GitHub digest ({expectedSha256}).");

            // Safe extract
            progress?.Report(new UpdateProgress("Extracting update", 0, 0));
            bool isMac = descriptor.Platform.Contains("osx", StringComparison.OrdinalIgnoreCase)
                || descriptor.Platform.Contains("mac", StringComparison.OrdinalIgnoreCase);

            await ExtractSafeAsync(packagePath, payloadRoot, isMac, cancellationToken).ConfigureAwait(false);

            // Check package internal version matches Release tag
            var internalVersion = TryReadInternalPackageVersion(payloadRoot);
            if (!string.IsNullOrWhiteSpace(internalVersion))
            {
                var cleanInternal = internalVersion.Trim().TrimStart('v', 'V');
                var cleanExpected = descriptor.Version.Trim().TrimStart('v', 'V');
                if (!cleanInternal.Equals(cleanExpected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Package internal version '{internalVersion}' does not match release version '{descriptor.Version}'.");
            }

            // Generate local file inventory from extracted payload
            var inventory = GenerateLocalInventory(payloadRoot, descriptor.Version, descriptor.Platform, freshAsset.Name, actualSha256, downloadedInfo.Length);
            var inventoryPath = Path.Combine(operationRoot, "local-inventory.json");
            await File.WriteAllTextAsync(inventoryPath, JsonSerializer.Serialize(inventory, JsonOptions), cancellationToken).ConfigureAwait(false);

            // Write staged update marker
            var markerPath = Path.Combine(operationRoot, "staged-update.json");
            var marker = new
            {
                OperationId = operationId,
                Version = descriptor.Version,
                Tag = descriptor.Tag,
                Platform = descriptor.Platform,
                PackageName = freshAsset.Name,
                PackagePath = packagePath,
                PayloadRoot = payloadRoot,
                InventoryPath = inventoryPath,
                CreatedUtc = DateTime.UtcNow
            };
            await File.WriteAllTextAsync(markerPath, JsonSerializer.Serialize(marker, JsonOptions), cancellationToken).ConfigureAwait(false);

            return new StagedFullUpdate(
                operationId,
                descriptor.Version,
                descriptor.Tag,
                descriptor.Platform,
                packagePath,
                payloadRoot,
                inventoryPath,
                inventory);
        }
        catch
        {
            if (Directory.Exists(operationRoot))
            {
                try { Directory.Delete(operationRoot, recursive: true); }
                catch { }
            }
            throw;
        }
    }

    public static string? TryReadInternalPackageVersion(string payloadRoot)
    {
        // 1. app-version.json
        var appVersionPath = Path.Combine(payloadRoot, "app-version.json");
        if (File.Exists(appVersionPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(appVersionPath));
                if (doc.RootElement.TryGetProperty("version", out var prop) && prop.GetString() is { } v)
                    return v;
            }
            catch { }
        }

        // 2. version.txt
        var versionTxtPath = Path.Combine(payloadRoot, "version.txt");
        if (File.Exists(versionTxtPath))
        {
            try
            {
                var txt = File.ReadAllText(versionTxtPath).Trim();
                if (!string.IsNullOrWhiteSpace(txt)) return txt;
            }
            catch { }
        }

        // 3. macOS bundle Contents/Resources/version.txt
        var bundleVersionTxt = Path.Combine(payloadRoot, "UmamusumeAss.app", "Contents", "Resources", "version.txt");
        if (File.Exists(bundleVersionTxt))
        {
            try
            {
                var txt = File.ReadAllText(bundleVersionTxt).Trim();
                if (!string.IsNullOrWhiteSpace(txt)) return txt;
            }
            catch { }
        }

        // 4. macOS bundle Contents/Info.plist
        var plistPath = Path.Combine(payloadRoot, "UmamusumeAss.app", "Contents", "Info.plist");
        if (File.Exists(plistPath))
        {
            try
            {
                var doc = XDocument.Load(plistPath);
                var key = doc.Descendants("key").FirstOrDefault(e => e.Value == "CFBundleShortVersionString");
                var val = key?.ElementsAfterSelf("string").FirstOrDefault()?.Value;
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
            catch { }
        }

        return null;
    }

    public static LocalFileInventory GenerateLocalInventory(
        string payloadRoot,
        string version,
        string platform,
        string packageName,
        string packageSha256,
        long packageSize)
    {
        var inventory = new LocalFileInventory
        {
            SchemaVersion = 2,
            Version = version,
            Platform = platform,
            PackageName = packageName,
            PackageSha256 = packageSha256,
            PackageSize = packageSize,
        };

        foreach (var file in Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            var relPath = Path.GetRelativePath(payloadRoot, file).Replace('\\', '/');
            var info = new FileInfo(file);
            inventory.Files.Add(new LocalFileEntry
            {
                Path = relPath,
                Size = info.Length,
                Sha256 = ComputeSha256File(file)
            });
        }

        return inventory;
    }

    public static string GetDeterministicOperationId(string tag, string platform, string assetName)
    {
        var bytes = Encoding.UTF8.GetBytes($"{tag}:{platform}:{assetName}".ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(bytes))[..32].ToLowerInvariant();
    }

    public StagedFullUpdate? TryRestoreStaged(string? updatesRoot = null)
    {
        var root = updatesRoot ?? _updatesRoot;
        if (!Directory.Exists(root)) return null;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var inventoryPath = Path.Combine(dir, "local-inventory.json");
            var markerPath = Path.Combine(dir, "staged-update.json");
            var payloadRoot = Path.Combine(dir, "payload");

            if (!File.Exists(inventoryPath) || !File.Exists(markerPath) || !Directory.Exists(payloadRoot))
                continue;

            try
            {
                var inventory = JsonSerializer.Deserialize<LocalFileInventory>(File.ReadAllText(inventoryPath));
                if (inventory is null || inventory.SchemaVersion != 2 || string.IsNullOrWhiteSpace(inventory.Version))
                    continue;

                // Validate payload files
                if (!VerifyPayloadFiles(payloadRoot, inventory))
                    continue;

                using var markerDoc = JsonDocument.Parse(File.ReadAllBytes(markerPath));
                var mRoot = markerDoc.RootElement;
                var opId = mRoot.GetProperty("OperationId").GetString() ?? Path.GetFileName(dir);
                var tag = mRoot.GetProperty("Tag").GetString() ?? ("v" + inventory.Version);
                var platform = mRoot.GetProperty("Platform").GetString() ?? inventory.Platform;
                var pkgPath = mRoot.GetProperty("PackagePath").GetString() ?? Path.Combine(dir, inventory.PackageName);

                return new StagedFullUpdate(
                    opId,
                    inventory.Version,
                    tag,
                    platform,
                    pkgPath,
                    payloadRoot,
                    inventoryPath,
                    inventory);
            }
            catch
            {
                // Ignore invalid staging entry
            }
        }

        return null;
    }

    public static bool VerifyPayloadFiles(string payloadRoot, LocalFileInventory inventory)
    {
        if (inventory.Files.Count > UpdatePathSafety.MaxFiles) return false;
        long totalSize = 0;
        foreach (var entry in inventory.Files)
        {
            var filePath = UpdatePathSafety.ResolveUnderRoot(payloadRoot, entry.Path);
            if (!File.Exists(filePath)) return false;
            var info = new FileInfo(filePath);
            if (info.Length != entry.Size) return false;
            totalSize += info.Length;
            if (totalSize > UpdatePathSafety.MaxExpandedBytes) return false;
            if (!ComputeSha256File(filePath).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    public async Task ClearUpdateCachesAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_updatesRoot)) return;

        foreach (var dir in Directory.EnumerateDirectories(_updatesRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Don't delete if an incomplete installation journal exists
            var journalPath = Path.Combine(dir, "journal.json");
            if (File.Exists(journalPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(journalPath, cancellationToken).ConfigureAwait(false));
                    var stage = doc.RootElement.GetProperty("Stage").GetString();
                    if (stage is "swapping" or "swapped" or "health-check")
                        continue; // Keep for recovery!
                }
                catch { }
            }

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup
            }
        }
    }

    public static string ComputeSha256File(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 128 * 1024);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static async Task<HashSet<string>> ExtractSafeAsync(
        string packagePath,
        string destination,
        bool preserveUnixPermissions,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var extractedPaths = new HashSet<string>(StringComparer.Ordinal);
        var occupiedPaths = new Dictionary<string, (string Spelling, bool IsDirectory)>(StringComparer.OrdinalIgnoreCase);
        var explicitEntries = new HashSet<string>(StringComparer.Ordinal);

        if (archive.Entries.Count > UpdatePathSafety.MaxFiles)
            throw new InvalidDataException("Update package contains too many files.");

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            var fileType = unixMode & 0xF000;
            var isDirectory = name.EndsWith('/');
            var normalizedName = isDirectory ? name.TrimEnd('/') : name;
            UpdatePathSafety.ValidateRelative(normalizedName);

            var components = normalizedName.Split('/');
            var prefix = string.Empty;
            for (var index = 0; index < components.Length; index++)
            {
                prefix = prefix.Length == 0 ? components[index] : prefix + "/" + components[index];
                var componentIsDirectory = index < components.Length - 1 || isDirectory;
                var canonicalPrefix = string.Join('/', components.Take(index + 1)
                    .Select(component => component.Normalize(NormalizationForm.FormD)));
                if (occupiedPaths.TryGetValue(canonicalPrefix, out var existing))
                {
                    if (!existing.Spelling.Equals(prefix, StringComparison.Ordinal))
                        throw new InvalidDataException($"Update package contains case-colliding paths: {name}");
                    if (existing.IsDirectory != componentIsDirectory)
                        throw new InvalidDataException($"Update package has a file/directory path conflict: {name}");
                }
                else
                {
                    occupiedPaths.Add(canonicalPrefix, (prefix, componentIsDirectory));
                }
            }

            if (!explicitEntries.Add(normalizedName))
                throw new InvalidDataException($"Update package contains duplicate path: {name}");

            if (isDirectory)
            {
                if (preserveUnixPermissions && fileType is not (0 or 0x4000))
                    throw new InvalidDataException($"Update package contains a non-directory entry: {name}");
                Directory.CreateDirectory(UpdatePathSafety.ResolveUnderRoot(destination, normalizedName));
                continue;
            }

            if (preserveUnixPermissions && fileType is not (0 or 0x8000))
                throw new InvalidDataException($"Update package contains a symlink or special file: {name}");

            if (entry.Length > UpdatePathSafety.MaxFileBytes)
                throw new InvalidDataException("Update package contains an oversized file.");

            total = checked(total + entry.Length);
            if (total > UpdatePathSafety.MaxExpandedBytes)
                throw new InvalidDataException("Update package expands beyond the safety limit.");

            var target = UpdatePathSafety.ResolveUnderRoot(destination, name);
            if (!extractedPaths.Add(name))
                throw new InvalidDataException($"Update package contains duplicate file: {name}");

            if (preserveUnixPermissions && (unixMode & 0x0E00) != 0)
                throw new InvalidDataException($"Update package contains unsafe file permissions: {name}");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

            if (preserveUnixPermissions)
            {
                var safeMode = (UnixFileMode)(unixMode & 0x01FF);
                if (safeMode == 0)
                    safeMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                        | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(target, safeMode);
                }
            }
        }
        return extractedPaths;
    }
}
