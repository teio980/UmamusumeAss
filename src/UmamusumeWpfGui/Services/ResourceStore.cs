using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Update;

namespace UmamusumeWpfGui.Services;

public interface IResourceStore
{
    ResourceRevision Active { get; }
    string ActiveBaseDirectory { get; }
    string ActiveCompositeBaseDirectory { get; }
    bool RequiresFullResource { get; }
    string Resolve(string relativePath);
    Task InitializeAsync(string bundledResourceDirectory, CancellationToken cancellationToken = default);
    Task UseBundledFallbackAsync(string bundledResourceDirectory, CancellationToken cancellationToken = default);
    Task<ResourceCandidate> PrepareAsync(string version, string payloadRoot, CancellationToken cancellationToken = default);
    Task<ResourceCandidate> PrepareAsync(
        string version,
        string payloadRoot,
        bool isDelta,
        IEnumerable<string>? deletes,
        string? expectedTargetTreeSha256,
        CancellationToken cancellationToken = default);
    Task CommitAsync(ResourceCandidate candidate, CancellationToken cancellationToken = default);
    Task RollbackAsync(CancellationToken cancellationToken = default);
    Task WriteOverrideAsync(string relativePath, Stream content, CancellationToken cancellationToken = default);
}

public sealed class ResourceStore : IResourceStore, IDisposable
{
    private sealed class ActivePointer
    {
        public string Version { get; set; } = string.Empty;
        public string BaseTreeSha256 { get; set; } = string.Empty;
        public string ViewTreeSha256 { get; set; } = string.Empty;
        public string ViewDirectory { get; set; } = string.Empty;
        public bool RequiresFullResource { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _appDataRoot;
    private readonly string _basesRoot;
    private readonly string _overridesRoot;
    private readonly string _viewsRoot;
    private readonly string _activePointerPath;
    private readonly string _bundledFingerprintPath;
    private ResourceRevision _active = new("uninitialized", string.Empty, string.Empty);
    private string _activeDirectory = AppContext.BaseDirectory;
    private bool _initialized;
    private string? _previousDirectory;
    private ResourceRevision? _previousRevision;
    public bool RequiresFullResource { get; private set; }
    private int _disposed;

    public ResourceStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UmamusumeAss"))
    {
    }

    public ResourceStore(string appDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        _appDataRoot = Path.GetFullPath(appDataRoot);
        _basesRoot = Path.Combine(_appDataRoot, "resources", "bases");
        _overridesRoot = Path.Combine(_appDataRoot, "resources", "overrides");
        _viewsRoot = Path.Combine(_appDataRoot, "resources", "views");
        _activePointerPath = Path.Combine(_appDataRoot, "resources", "active.json");
        _bundledFingerprintPath = Path.Combine(_appDataRoot, "resources", "bundled.fingerprint");
        ResourcePathRuntime.SetOverrideRoot(_overridesRoot);
    }

    public ResourceRevision Active => _active;
    // The exposed root is the resource tree itself, matching resource asset
    // inventory paths (foo/bar) and ResourceStore's base-tree hash.
    public string ActiveBaseDirectory => Path.Combine(_basesRoot, _active.Version, "resource");
    public string ActiveCompositeBaseDirectory => _activeDirectory;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _gate.Dispose();
    }

    public string Resolve(string relativePath) =>
        UpdatePathSafety.ResolveUnderRoot(Path.Combine(_activeDirectory, "resource"),
            relativePath.StartsWith("resource/", StringComparison.OrdinalIgnoreCase)
                ? relativePath["resource/".Length..]
                : relativePath);

    public async Task InitializeAsync(
        string bundledResourceDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledResourceDirectory);
        var bundled = Path.GetFullPath(bundledResourceDirectory);
        if (!Directory.Exists(bundled))
            throw new DirectoryNotFoundException($"Bundled resource directory not found: {bundled}");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_basesRoot);
            Directory.CreateDirectory(_overridesRoot);
            Directory.CreateDirectory(_viewsRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(_activePointerPath)!);

            var bundledVersion = "bundled-" + UpdateVersionInfo.CurrentVersion;
            string? staleBundledVersion = null;
            if (File.Exists(_activePointerPath))
            {
                try
                {
                    var pointer = JsonSerializer.Deserialize<ActivePointer>(
                        await File.ReadAllTextAsync(_activePointerPath, cancellationToken).ConfigureAwait(false),
                        JsonOptions);
                    if (pointer is not null
                        && Directory.Exists(pointer.ViewDirectory)
                        && Directory.Exists(Path.Combine(pointer.ViewDirectory, "resource")))
                    {
                        var isBundledRevision = pointer.Version.Equals(
                                UpdateVersionInfo.CurrentVersion,
                                StringComparison.Ordinal)
                            || pointer.Version.Equals(bundledVersion, StringComparison.Ordinal);
                        var bundledHashMatches = !isBundledRevision;
                        string? bundledFingerprint = null;
                        if (isBundledRevision)
                        {
                            // The bundled tree is immutable for a published
                            // build. Checking file metadata is enough to
                            // detect local edits, while avoiding a 150+ MB
                            // content hash on every launch. The full hash is
                            // retained as a fallback for old installations
                            // and for metadata-only changes.
                            bundledFingerprint = await ComputeTreeFingerprintAsync(
                                    bundled,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            var cachedFingerprint = await ReadBundledFingerprintAsync(
                                    cancellationToken)
                                .ConfigureAwait(false);
                            bundledHashMatches = string.Equals(
                                cachedFingerprint,
                                bundledFingerprint,
                                StringComparison.OrdinalIgnoreCase);
                            if (!bundledHashMatches)
                            {
                                bundledHashMatches = pointer.BaseTreeSha256.Equals(
                                    await ComputeTreeHashAsync(bundled, cancellationToken)
                                        .ConfigureAwait(false),
                                    StringComparison.OrdinalIgnoreCase);
                            }
                        }
                        if (bundledHashMatches)
                        {
                            _active = new ResourceRevision(
                                pointer.Version,
                                pointer.BaseTreeSha256,
                                pointer.ViewTreeSha256);
                            RequiresFullResource = pointer.RequiresFullResource;
                            _activeDirectory = pointer.ViewDirectory;
                            ResourcePathRuntime.SetBaseDirectory(_activeDirectory);
                            _initialized = true;
                            if (isBundledRevision && bundledFingerprint is not null)
                                await WriteBundledFingerprintAsync(
                                        bundledFingerprint,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            return;
                        }

                        // A same-version bundled resource can change during
                        // local development. Rebuild only that bundled slot;
                        // downloaded resource revisions remain untouched.
                        staleBundledVersion = pointer.Version;
                    }
                }
                catch (JsonException)
                {
                    // A corrupt pointer is replaced by a new atomic pointer below.
                }
            }

            var version = staleBundledVersion ?? UpdateVersionInfo.CurrentVersion;
            var baseDirectory = Path.Combine(_basesRoot, version);
            if (staleBundledVersion is not null && Directory.Exists(baseDirectory))
                Directory.Delete(baseDirectory, recursive: true);
            if (!Directory.Exists(Path.Combine(baseDirectory, "resource")))
            {
                var temporary = baseDirectory + ".tmp-" + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(temporary);
                await CopyDirectoryAsync(bundled, Path.Combine(temporary, "resource"), cancellationToken)
                    .ConfigureAwait(false);
                Directory.Move(temporary, baseDirectory);
                RequiresFullResource = await ValidateBundledInventoryAsync(
                    bundled, cancellationToken).ConfigureAwait(false);
                if (RequiresFullResource)
                    await PreserveBundledMismatchesAsync(bundled, cancellationToken).ConfigureAwait(false);
            }

            var candidate = await BuildCandidateAsync(version, baseDirectory, cancellationToken)
                .ConfigureAwait(false);
            await CommitPointerAsync(candidate, initial: true, cancellationToken).ConfigureAwait(false);
            await CacheBundledFingerprintAsync(bundled, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ResourceCandidate> PrepareAsync(
        string version,
        string payloadRoot,
        CancellationToken cancellationToken = default)
        => await PrepareAsync(version, payloadRoot, false, null, null, cancellationToken)
            .ConfigureAwait(false);

    public async Task<ResourceCandidate> PrepareAsync(
        string version,
        string payloadRoot,
        bool isDelta,
        IEnumerable<string>? deletes,
        string? expectedTargetTreeSha256,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadRoot);
        ValidateSlotVersion(version);
        var source = Path.GetFullPath(payloadRoot);
        var sourceResource = Directory.Exists(Path.Combine(source, "resource"))
            ? Path.Combine(source, "resource")
            : source;
        if (!Directory.Exists(sourceResource))
            throw new DirectoryNotFoundException("Resource package does not contain a resource directory.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var baseDirectory = Path.Combine(_basesRoot, version);
            var baseResource = Path.Combine(baseDirectory, "resource");
            if (Directory.Exists(baseResource)
                && !string.IsNullOrWhiteSpace(expectedTargetTreeSha256)
                && !expectedTargetTreeSha256.Equals(
                    await ComputeTreeHashAsync(baseResource, cancellationToken).ConfigureAwait(false),
                    StringComparison.OrdinalIgnoreCase))
            {
                // A failed or interrupted previous operation must not poison a
                // later full fallback. This path is a version slot generated
                // exclusively below _basesRoot.
                Directory.Delete(baseDirectory, recursive: true);
            }
            if (!Directory.Exists(baseResource))
            {
                var temporary = baseDirectory + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    Directory.CreateDirectory(temporary);
                    var targetResource = Path.Combine(temporary, "resource");
                    if (isDelta)
                    {
                        var currentBase = Path.Combine(_basesRoot, _active.Version, "resource");
                        if (!Directory.Exists(currentBase)
                            || !string.Equals(
                                _active.BaseTreeSha256,
                                await ComputeTreeHashAsync(currentBase, cancellationToken)
                                    .ConfigureAwait(false),
                                StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("A resource delta requires a trusted current base.");
                        await CopyDirectoryAsync(currentBase, targetResource, cancellationToken)
                            .ConfigureAwait(false);
                        await ApplyDeltaAsync(sourceResource, targetResource, deletes, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await CopyDirectoryAsync(sourceResource, targetResource, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    if (!string.IsNullOrWhiteSpace(expectedTargetTreeSha256)
                        && !expectedTargetTreeSha256.Equals(
                            await ComputeTreeHashAsync(targetResource, cancellationToken)
                                .ConfigureAwait(false),
                            StringComparison.OrdinalIgnoreCase))
                        throw new CryptographicException("Resource target tree hash did not match its manifest.");
                    Directory.Move(temporary, baseDirectory);
                }
                catch
                {
                    if (Directory.Exists(temporary))
                        Directory.Delete(temporary, recursive: true);
                    throw;
                }
            }
            var candidate = await BuildCandidateAsync(version, baseDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(expectedTargetTreeSha256)
                && !candidate.Revision.BaseTreeSha256.Equals(
                    expectedTargetTreeSha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Resource target tree hash did not match its manifest.");
            return candidate;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UseBundledFallbackAsync(
        string bundledResourceDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledResourceDirectory);
        var bundled = Path.GetFullPath(bundledResourceDirectory);
        if (!Directory.Exists(bundled))
            throw new DirectoryNotFoundException(bundled);
        EnsureInitialized();
        var version = "bundled-" + UpdateVersionInfo.CurrentVersion;
        ValidateSlotVersion(version);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var baseDirectory = Path.Combine(_basesRoot, version);
            if (Directory.Exists(baseDirectory))
                Directory.Delete(baseDirectory, recursive: true);
            var temporary = baseDirectory + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await CopyDirectoryAsync(bundled, Path.Combine(temporary, "resource"), cancellationToken)
                    .ConfigureAwait(false);
                Directory.Move(temporary, baseDirectory);
            }
            catch
            {
                if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
                throw;
            }
            var candidate = await BuildCandidateAsync(version, baseDirectory, cancellationToken)
                .ConfigureAwait(false);
            RequiresFullResource = true;
            await CommitPointerAsync(candidate, initial: false, cancellationToken).ConfigureAwait(false);
            await CacheBundledFingerprintAsync(bundled, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CommitAsync(ResourceCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        EnsureInitialized();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequiresFullResource = false;
            await CommitPointerAsync(candidate, initial: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_previousDirectory is null || _previousRevision is null
                || !Directory.Exists(_previousDirectory))
                return;
            var candidate = new ResourceCandidate(
                _previousRevision,
                Path.Combine(_basesRoot, _previousRevision.Version),
                _previousDirectory,
                _activePointerPath);
            await CommitPointerAsync(candidate, initial: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteOverrideAsync(
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        EnsureInitialized();
        var normalized = relativePath.Replace('\\', '/');
        if (!normalized.StartsWith("resource/", StringComparison.OrdinalIgnoreCase))
            normalized = "resource/" + normalized.TrimStart('/');
        var destination = UpdatePathSafety.ResolveUnderRoot(_overridesRoot, normalized);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, destination, overwrite: true);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var baseDirectory = Path.Combine(_basesRoot, _active.Version);
            var candidate = await BuildCandidateAsync(_active.Version, baseDirectory, cancellationToken).ConfigureAwait(false);
            await CommitPointerAsync(candidate, initial: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ResourceCandidate> BuildCandidateAsync(
        string version,
        string baseDirectory,
        CancellationToken cancellationToken)
    {
        var baseResource = Path.Combine(baseDirectory, "resource");
        if (!Directory.Exists(baseResource))
            throw new DirectoryNotFoundException(baseResource);
        var baseHash = await ComputeTreeHashAsync(baseResource, cancellationToken).ConfigureAwait(false);
        var viewDirectory = Path.Combine(_viewsRoot, $"{version}-{baseHash[..Math.Min(16, baseHash.Length)]}-{Guid.NewGuid():N}");
        var viewResource = Path.Combine(viewDirectory, "resource");
        await CopyDirectoryAsync(baseResource, viewResource, cancellationToken).ConfigureAwait(false);

        if (Directory.Exists(_overridesRoot))
            await OverlayDirectoryAsync(_overridesRoot, viewDirectory, cancellationToken).ConfigureAwait(false);
        var viewHash = await ComputeTreeHashAsync(viewResource, cancellationToken).ConfigureAwait(false);
        return new ResourceCandidate(
            new ResourceRevision(version, baseHash, viewHash),
            baseDirectory,
            viewDirectory,
            _activePointerPath);
    }

    private async Task CommitPointerAsync(
        ResourceCandidate candidate,
        bool initial,
        CancellationToken cancellationToken)
    {
        var pointer = new ActivePointer
        {
            Version = candidate.Revision.Version,
            BaseTreeSha256 = candidate.Revision.BaseTreeSha256,
            ViewTreeSha256 = candidate.Revision.ViewTreeSha256,
            ViewDirectory = candidate.CompositeDirectory,
            RequiresFullResource = RequiresFullResource,
        };
        var temporary = _activePointerPath + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(pointer, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        if (initial && !File.Exists(_activePointerPath))
            File.Move(temporary, _activePointerPath);
        else
            File.Replace(temporary, _activePointerPath, null, ignoreMetadataErrors: true);
        if (_initialized && !_activeDirectory.Equals(candidate.CompositeDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _previousDirectory = _activeDirectory;
            _previousRevision = _active;
        }
        _active = candidate.Revision;
        _activeDirectory = candidate.CompositeDirectory;
        ResourcePathRuntime.SetBaseDirectory(_activeDirectory);
        _initialized = true;
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("Resource store has not been initialized.");
    }

    private void ValidateSlotVersion(string version)
    {
        if (version.Contains('/', StringComparison.Ordinal)
            || version.Contains('\\', StringComparison.Ordinal)
            || version.Contains(':', StringComparison.Ordinal)
            || version is "." or "..")
            throw new InvalidDataException("Resource version is not a safe slot name.");
        var candidate = Path.GetFullPath(Path.Combine(_basesRoot, version));
        var root = Path.GetFullPath(_basesRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Resource version escaped the slot root.");
    }

    private static Task CopyDirectoryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!TryCreateHardLink(target, file))
                File.Copy(file, target, overwrite: true);
        }
        return Task.CompletedTask;
    }

    private static Task OverlayDirectoryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            if (relative.EndsWith(".tombstone", StringComparison.OrdinalIgnoreCase))
            {
                var targetRelative = relative[..^".tombstone".Length];
                var target = Path.Combine(destination, targetRelative);
                if (File.Exists(target)) File.Delete(target);
                continue;
            }
            var targetPath = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            if (File.Exists(targetPath)) File.Delete(targetPath);
            if (!TryCreateHardLink(targetPath, file))
                File.Copy(file, targetPath, overwrite: true);
        }
        return Task.CompletedTask;
    }

    private static Task ApplyDeltaAsync(
        string sourceResource,
        string targetResource,
        IEnumerable<string>? deletes,
        CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(sourceResource, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceResource, file);
            var target = UpdatePathSafety.ResolveUnderRoot(targetResource, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target)) File.Delete(target);
            if (!TryCreateHardLink(target, file)) File.Copy(file, target, overwrite: true);
        }
        foreach (var deleted in deletes ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = deleted.Replace('\\', '/');
            if (normalized.StartsWith("resource/", StringComparison.OrdinalIgnoreCase))
                normalized = normalized["resource/".Length..];
            var target = UpdatePathSafety.ResolveUnderRoot(targetResource, normalized);
            if (File.Exists(target)) File.Delete(target);
        }
        return Task.CompletedTask;
    }

    private static async Task<string> ComputeTreeHashAsync(string root, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(relative);
            hash.AppendData(nameBytes);
            hash.AppendData([0]);
            await using var stream = File.OpenRead(file);
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                hash.AppendData(buffer.AsSpan(0, read));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task<string?> ReadBundledFingerprintAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_bundledFingerprintPath))
                return null;

            return (await File.ReadAllTextAsync(
                    _bundledFingerprintPath,
                    cancellationToken)
                .ConfigureAwait(false)).Trim();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task CacheBundledFingerprintAsync(
        string bundled,
        CancellationToken cancellationToken)
    {
        try
        {
            var fingerprint = await ComputeTreeFingerprintAsync(
                    bundled,
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteBundledFingerprintAsync(fingerprint, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The cache is an optimization only. A later launch will rebuild
            // it after falling back to the full content hash.
        }
        catch (UnauthorizedAccessException)
        {
            // See the IOException comment above.
        }
    }

    private async Task WriteBundledFingerprintAsync(
        string fingerprint,
        CancellationToken cancellationToken)
    {
        try
        {
            await File.WriteAllTextAsync(
                    _bundledFingerprintPath,
                    fingerprint,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Non-fatal cache write; correctness is preserved by the full
            // hash fallback on the next launch.
        }
        catch (UnauthorizedAccessException)
        {
            // Non-fatal cache write; see the IOException comment above.
        }
    }

    private static Task<string> ComputeTreeFingerprintAsync(
        string root,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var info = new FileInfo(file);
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(info.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture)));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(info.LastWriteTimeUtc.Ticks.ToString(
                System.Globalization.CultureInfo.InvariantCulture)));
            hash.AppendData([0]);
        }

        return Task.FromResult(Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static async Task<bool> ValidateBundledInventoryAsync(
        string bundled,
        CancellationToken cancellationToken)
    {
        var inventoryPath = Path.Combine(Directory.GetParent(bundled)!.FullName, "resource.inventory.json");
        if (!File.Exists(inventoryPath)) return false;
        try
        {
            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(inventoryPath, cancellationToken).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("files", out var files)
                || files.ValueKind != JsonValueKind.Array)
                return true;
            var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in files.EnumerateArray())
            {
                var relative = item.GetProperty("path").GetString() ?? string.Empty;
                expectedPaths.Add(relative.Replace('\\', '/'));
                var expected = item.GetProperty("sha256").GetString() ?? string.Empty;
                var path = UpdatePathSafety.ResolveUnderRoot(bundled, relative);
                if (!File.Exists(path)
                    || !ManifestVerifier.Sha256File(path).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            foreach (var actual in Directory.EnumerateFiles(bundled, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(bundled, actual).Replace('\\', '/');
                if (!expectedPaths.Contains(relative)) return true;
            }
        }
        catch (Exception)
        {
            return true;
        }
        return false;
    }

    private async Task PreserveBundledMismatchesAsync(
        string bundled,
        CancellationToken cancellationToken)
    {
        var inventoryPath = Path.Combine(Directory.GetParent(bundled)!.FullName, "resource.inventory.json");
        if (!File.Exists(inventoryPath)) return;
        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(inventoryPath, cancellationToken).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("files", out var files)) return;
        foreach (var item in files.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = item.GetProperty("path").GetString() ?? string.Empty;
            var expected = item.GetProperty("sha256").GetString() ?? string.Empty;
            var source = UpdatePathSafety.ResolveUnderRoot(bundled, relative);
            if (!File.Exists(source)
                || ManifestVerifier.Sha256File(source).Equals(expected, StringComparison.OrdinalIgnoreCase))
                continue;
            var destination = UpdatePathSafety.ResolveUnderRoot(_overridesRoot, "resource/" + relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = File.OpenRead(source);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
        var expectedPaths = new HashSet<string>(
            files.EnumerateArray()
                .Select(item => (item.GetProperty("path").GetString() ?? string.Empty)
                    .Replace('\\', '/')),
            StringComparer.OrdinalIgnoreCase);
        foreach (var source in Directory.EnumerateFiles(bundled, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(bundled, source).Replace('\\', '/');
            if (expectedPaths.Contains(relative)) continue;
            var destination = UpdatePathSafety.ResolveUnderRoot(_overridesRoot, "resource/" + relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = File.OpenRead(source);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TryCreateHardLink(string destination, string source)
    {
        try
        {
            return CreateHardLink(destination, source, IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("Kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}

public static class UpdateVersionInfo
{
    public static string CurrentVersion { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "app-version.json"),
            Path.Combine(AppContext.BaseDirectory, "version.txt"),
        };
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var text = File.ReadAllText(candidate).Trim();
                if (candidate.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    text = JsonDocument.Parse(text).RootElement.GetProperty("version").GetString() ?? text;
                if (Version.TryParse(text, out _)) return text;
            }
            catch (Exception) when (candidate.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
            }
        }
        return "0.2.0";
    }
}
