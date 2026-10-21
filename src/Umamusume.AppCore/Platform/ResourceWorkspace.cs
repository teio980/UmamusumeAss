using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Umamusume.AppCore.Platform;

/// <summary>Chooses a validated, versioned resource source before the writable view is prepared.</summary>
public interface IResourceSourceProvider
{
    Task<string?> GetPreferredResourceSourceRootAsync(
        string bundledSourceRoot,
        string userDataRoot,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A writable, versioned copy of the shipped resource tree. Native resource
/// writes are redirected into per-user overrides so an app bundle stays intact.
/// </summary>
public sealed class ResourceWorkspace
{
    private sealed record ViewFingerprint(string Source, string Overrides);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly SemaphoreSlim PrepareGate = new(1, 1);

    private ResourceWorkspace(string sourceResourceRoot, string resourceRoot, string overridesRoot, ViewFingerprint fingerprint)
    {
        SourceResourceRoot = Path.GetFullPath(sourceResourceRoot);
        ActiveResourceRoot = Path.GetFullPath(resourceRoot);
        OverridesRoot = Path.GetFullPath(overridesRoot);
        SourceFingerprint = fingerprint.Source;
        OverridesFingerprint = fingerprint.Overrides;
    }

    /// <summary>The active resource directory itself, suitable for asset reads.</summary>
    public string ActiveResourceRoot { get; }

    public string SourceResourceRoot { get; }

    /// <summary>The parent directory passed to CoreBridge.InitializeAsync / ReloadResourceAsync.</summary>
    public string ActiveBaseDirectory =>
        Directory.GetParent(ActiveResourceRoot)?.FullName ?? throw new InvalidOperationException("Resource view has no parent directory.");

    public string OverridesRoot { get; }

    public string SourceFingerprint { get; }

    public string OverridesFingerprint { get; }

    /// <summary>Creates or reuses a complete immutable-source view with user overrides applied.</summary>
    public static async Task<ResourceWorkspace> PrepareAsync(
        string sourceResourceRoot,
        string userDataRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceResourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataRoot);
        var sourceRoot = Path.GetFullPath(sourceResourceRoot);
        var dataRoot = Path.GetFullPath(userDataRoot);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Resource source directory not found: {sourceRoot}");

        await PrepareGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runtimeRoot = Path.Combine(dataRoot, "runtime");
            var viewsRoot = Path.Combine(runtimeRoot, "views");
            var overridesRoot = Path.Combine(dataRoot, "resource-overrides");
            Directory.CreateDirectory(viewsRoot);
            Directory.CreateDirectory(overridesRoot);

            var sourceFingerprint = await ComputeTreeFingerprintAsync(sourceRoot, cancellationToken)
                .ConfigureAwait(false);
            var overridesFingerprint = await ComputeTreeFingerprintAsync(overridesRoot, cancellationToken)
                .ConfigureAwait(false);
            var fingerprint = new ViewFingerprint(sourceFingerprint, overridesFingerprint);
            var viewName = $"{sourceFingerprint}-{overridesFingerprint}";
            var viewRoot = Path.Combine(viewsRoot, viewName);
            var resourceRoot = Path.Combine(viewRoot, "resource");
            var markerPath = Path.Combine(viewRoot, "fingerprint.json");

            if (await HasMatchingMarkerAsync(markerPath, fingerprint, cancellationToken).ConfigureAwait(false)
                && Directory.Exists(resourceRoot))
            {
                return new ResourceWorkspace(sourceRoot, resourceRoot, overridesRoot, fingerprint);
            }

            if (Directory.Exists(viewRoot))
                Directory.Delete(viewRoot, recursive: true);

            var stagingRoot = Path.Combine(viewsRoot, $".staging-{Guid.NewGuid():N}");
            try
            {
                var stagingResourceRoot = Path.Combine(stagingRoot, "resource");
                await CopyTreeAsync(sourceRoot, stagingResourceRoot, cancellationToken).ConfigureAwait(false);
                await CopyTreeAsync(overridesRoot, stagingResourceRoot, cancellationToken).ConfigureAwait(false);
                Directory.CreateDirectory(stagingRoot);
                await File.WriteAllTextAsync(
                    Path.Combine(stagingRoot, "fingerprint.json"),
                    JsonSerializer.Serialize(fingerprint, JsonOptions),
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    Directory.Move(stagingRoot, viewRoot);
                }
                catch (IOException)
                {
                    if (!Directory.Exists(viewRoot)
                        || !await HasMatchingMarkerAsync(markerPath, fingerprint, cancellationToken).ConfigureAwait(false))
                        throw;

                    // Another application instance prepared this exact immutable view.
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }
            finally
            {
                if (Directory.Exists(stagingRoot))
                    Directory.Delete(stagingRoot, recursive: true);
            }

            return new ResourceWorkspace(sourceRoot, resourceRoot, overridesRoot, fingerprint);
        }
        finally
        {
            PrepareGate.Release();
        }
    }

    /// <summary>
    /// Atomically writes a text file. Paths inside the active resource tree are
    /// also stored in the persistent override tree; other paths are saved once.
    /// Returns true when an override was recorded.
    /// </summary>
    public async Task<bool> SaveTextAsync(
        string path,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);
        return await SaveBytesAsync(
            path,
            new UTF8Encoding(false).GetBytes(text),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically writes bytes, persisting files within the active resource
    /// tree to the override tree as well. Returns true when an override was recorded.
    /// </summary>
    public async Task<bool> SaveBytesAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var payload = bytes.ToArray();
        var fullPath = Path.GetFullPath(path);
        if (!TryGetRelativePath(ActiveResourceRoot, fullPath, out var relativePath))
        {
            await WriteAtomicallyAsync(fullPath, payload, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var overridePath = Path.GetFullPath(Path.Combine(OverridesRoot, relativePath));
        if (!IsWithinRoot(OverridesRoot, overridePath))
            throw new IOException("The resource override path escapes its storage directory.");

        // Persist the override first; a failed view write will be repaired the next time the workspace opens.
        await WriteAtomicallyAsync(overridePath, payload, cancellationToken).ConfigureAwait(false);
        await WriteAtomicallyAsync(fullPath, payload, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public static Task SaveFileAtomicallyAsync(
        string path,
        string text,
        CancellationToken cancellationToken = default) =>
        WriteAtomicallyAsync(path, text, cancellationToken);

    public static Task SaveFileBytesAtomicallyAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default) =>
        WriteAtomicallyAsync(path, bytes.ToArray(), cancellationToken);

    private static async Task<bool> HasMatchingMarkerAsync(
        string markerPath,
        ViewFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(markerPath))
            return false;
        try
        {
            var json = await File.ReadAllTextAsync(markerPath, cancellationToken).ConfigureAwait(false);
            var marker = JsonSerializer.Deserialize<ViewFingerprint>(json);
            return marker == fingerprint;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task<string> ComputeTreeFingerprintAsync(
        string root,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal)
            .ToArray();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData([0]);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                hash.AppendData(buffer, 0, read);
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task CopyTreeAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationRoot);
        if (!Directory.Exists(sourceRoot))
            return;

        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, sourcePath);
            var destinationPath = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        string text,
        CancellationToken cancellationToken)
    {
        await WriteAtomicallyAsync(path, new UTF8Encoding(false).GetBytes(text), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new IOException("The target path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static bool TryGetRelativePath(string root, string path, out string relativePath)
    {
        relativePath = Path.GetRelativePath(root, path);
        if (relativePath == "." || Path.IsPathRooted(relativePath))
            return false;
        if (relativePath == ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            return false;
        return IsWithinRoot(root, path);
    }

    private static bool IsWithinRoot(string root, string path)
    {
        var relativePath = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (relativePath == ".")
            return true;
        if (Path.IsPathRooted(relativePath) || relativePath == "..")
            return false;
        if (relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            return false;
        return true;
    }
}
