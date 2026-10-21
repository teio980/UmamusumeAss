using System.Text.Json;
using System.Text.RegularExpressions;
using Umamusume.AppCore.Platform;

namespace Umamusume.Avalonia.Services;

public enum MacUpdateCleanupStage
{
    Scanning,
    RemovingDownloads,
    RemovingResourceRevisions,
    RemovingResourceViews,
}

public sealed record MacUpdateCleanupResult(
    int RemovedOperations,
    int RemovedRevisions,
    int RemovedViews,
    int PreservedOperations,
    bool InstallerRunning,
    bool ActivePointerInvalid,
    bool MaintenanceRestartRequired = false);

public interface IMacUpdateCacheCleanupService
{
    Task<MacUpdateCleanupResult> ClearAsync(
        ResourceWorkspace? currentWorkspace,
        IProgress<MacUpdateCleanupStage>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Removes only updater-owned caches and inactive resource workspaces.</summary>
public sealed class MacUpdateCacheCleanupService : IMacUpdateCacheCleanupService
{
    private sealed record ActivePointer(string? Version, string? RelativePath);
    private sealed record StagedReference(string? OperationId);

    private static readonly Regex ResourceVersionPattern = new(@"^\d{4}\.\d{2}\.\d{2}\.\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex LegacyPackagePattern = new(
        @"^UmamusumeAss-.*osx-arm64.*\.zip(?:\.(?:[0-9a-f]{32}\.)?part)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly SemaphoreSlim CleanupGate = new(1, 1);
    private readonly string _userDataRoot;
    private readonly string _updatesRoot;
    private readonly string _resourcesRoot;
    private readonly string _revisionsRoot;
    private readonly string _viewsRoot;
    private readonly string _applicationBaseDirectory;
    private readonly string? _applicationUpdateLockPath;

    public MacUpdateCacheCleanupService(
        string? userDataRoot = null,
        string? applicationBaseDirectory = null,
        string? applicationUpdateLockPath = null)
    {
        _userDataRoot = Path.GetFullPath(userDataRoot ?? PlatformPaths.GetDefaultUserDataDirectory());
        _updatesRoot = Path.Combine(_userDataRoot, "updates");
        _resourcesRoot = Path.Combine(_userDataRoot, "resources");
        _revisionsRoot = Path.Combine(_resourcesRoot, "revisions");
        _viewsRoot = Path.Combine(_userDataRoot, "runtime", "views");
        _applicationBaseDirectory = Path.GetFullPath(applicationBaseDirectory ?? AppContext.BaseDirectory);
        _applicationUpdateLockPath = applicationUpdateLockPath ?? FindApplicationUpdateLock(_applicationBaseDirectory);
    }

    public async Task<MacUpdateCleanupResult> ClearAsync(
        ResourceWorkspace? currentWorkspace,
        IProgress<MacUpdateCleanupStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await CleanupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ClearCore(currentWorkspace, progress, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally { CleanupGate.Release(); }
    }

    private MacUpdateCleanupResult ClearCore(
        ResourceWorkspace? currentWorkspace,
        IProgress<MacUpdateCleanupStage>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(MacUpdateCleanupStage.Scanning);
        cancellationToken.ThrowIfCancellationRequested();

        if (_applicationUpdateLockPath is not null && IsLockHeld(_applicationUpdateLockPath))
        {
            return new MacUpdateCleanupResult(0, 0, 0, CountOperationDirectories(),
                InstallerRunning: true, ActivePointerInvalid: false);
        }

        var protectedOperations = new HashSet<string>(PathComparer);
        foreach (string entry in EnumerateEntriesIfManaged(_updatesRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(entry);
            if (!Guid.TryParseExact(name, "N", out _)) continue;
            if (IsInUse(entry, currentWorkspace) || HasUnfinishedJournal(entry))
                protectedOperations.Add(Path.GetFullPath(entry));
        }

        progress?.Report(MacUpdateCleanupStage.RemovingDownloads);
        int removedOperations = 0;
        int preservedOperations = protectedOperations.Count;
        foreach (string entry in EnumerateEntriesIfManaged(_updatesRoot).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(entry);
            if (name is "checks" or "release-cache")
            {
                if (IsInUse(entry, currentWorkspace)) continue;
                DeleteManagedEntry(_updatesRoot, entry, cancellationToken);
                removedOperations++;
                continue;
            }

            if (LegacyPackagePattern.IsMatch(name) && !Directory.Exists(entry))
            {
                DeleteManagedEntry(_updatesRoot, entry, cancellationToken);
                removedOperations++;
                continue;
            }

            if (name is "staged-program.json" or "staged-resource.json")
            {
                string? operationId = ReadStagedOperationId(entry);
                string operationRoot = operationId is not null && Guid.TryParseExact(operationId, "N", out _)
                    ? Path.GetFullPath(Path.Combine(_updatesRoot, operationId))
                    : string.Empty;
                if (operationRoot.Length > 0 && protectedOperations.Contains(operationRoot)) continue;
                DeleteManagedEntry(_updatesRoot, entry, cancellationToken);
                continue;
            }

            if (name.StartsWith("staged-program.json.tmp-", StringComparison.Ordinal)
                || name.StartsWith("staged-resource.json.tmp-", StringComparison.Ordinal))
            {
                DeleteManagedEntry(_updatesRoot, entry, cancellationToken);
                continue;
            }

            if (!Guid.TryParseExact(name, "N", out _)) continue;
            if (protectedOperations.Contains(Path.GetFullPath(entry))) continue;
            DeleteManagedEntry(_updatesRoot, entry, cancellationToken);
            removedOperations++;
        }

        progress?.Report(MacUpdateCleanupStage.RemovingResourceRevisions);
        bool activePointerInvalid = !TryGetActiveRevision(out string? activeRevision);
        var preservedRevisions = new HashSet<string>(PathComparer);
        if (activeRevision is not null) preservedRevisions.Add(Path.GetFullPath(activeRevision));
        if (currentWorkspace is not null && GetRevisionAncestor(currentWorkspace.SourceResourceRoot) is { } workspaceRevision)
            preservedRevisions.Add(Path.GetFullPath(workspaceRevision));

        int removedRevisions = 0;
        if (!activePointerInvalid)
        {
            foreach (string entry in EnumerateEntriesIfManaged(_revisionsRoot).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(entry);
                bool knownRevision = ResourceVersionPattern.IsMatch(name);
                bool knownStaging = name.StartsWith(".staging-", StringComparison.Ordinal)
                    && Guid.TryParseExact(name[9..], "N", out _);
                if ((!knownRevision && !knownStaging) || preservedRevisions.Contains(Path.GetFullPath(entry))
                    || IsInUse(entry, currentWorkspace)) continue;
                DeleteManagedEntry(_revisionsRoot, entry, cancellationToken);
                if (knownRevision) removedRevisions++;
            }
        }

        progress?.Report(MacUpdateCleanupStage.RemovingResourceViews);
        int removedViews = 0;
        string? activeView = currentWorkspace is null
            ? null
            : Path.GetFullPath(Directory.GetParent(currentWorkspace.ActiveResourceRoot)?.FullName ?? string.Empty);
        foreach (string entry in EnumerateEntriesIfManaged(_viewsRoot).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((activeView is not null && PathEquals(entry, activeView)) || IsInUse(entry, currentWorkspace)) continue;
            DeleteManagedEntry(_viewsRoot, entry, cancellationToken);
            removedViews++;
        }

        return new MacUpdateCleanupResult(removedOperations, removedRevisions, removedViews,
            preservedOperations, InstallerRunning: false, ActivePointerInvalid: activePointerInvalid);
    }

    private bool TryGetActiveRevision(out string? revisionRoot)
    {
        revisionRoot = null;
        string pointerPath = Path.Combine(_resourcesRoot, "active.json");
        try
        {
            if (!HasManagedAncestors(pointerPath)) return false;
            if (Directory.Exists(pointerPath)) return false;
            if (!File.Exists(pointerPath)) return true;
            var pointer = JsonSerializer.Deserialize<ActivePointer>(File.ReadAllText(pointerPath));
            if (pointer is null || string.IsNullOrWhiteSpace(pointer.Version)
                || string.IsNullOrWhiteSpace(pointer.RelativePath)
                || !ResourceVersionPattern.IsMatch(pointer.Version)) return false;
            string expected = Path.GetFullPath(Path.Combine(_revisionsRoot, pointer.Version));
            string actual = Path.GetFullPath(Path.Combine(_resourcesRoot, pointer.RelativePath));
            if (!PathEquals(expected, actual) || !IsWithinOrEqual(_revisionsRoot, actual)
                || !Directory.Exists(actual) || !HasManagedAncestors(actual)) return false;
            revisionRoot = expected;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private string? GetRevisionAncestor(string sourceRoot)
    {
        string relative = Path.GetRelativePath(_revisionsRoot, Path.GetFullPath(sourceRoot));
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, PathComparison)) return null;
        string firstComponent = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (!ResourceVersionPattern.IsMatch(firstComponent)) return null;
        string candidate = Path.GetFullPath(Path.Combine(_revisionsRoot, firstComponent));
        return IsWithinOrEqual(_revisionsRoot, candidate) ? candidate : null;
    }

    private static bool HasUnfinishedJournal(string operationRoot)
    {
        if (IsReparsePoint(operationRoot)) return true;
        string journalPath = Path.Combine(operationRoot, "journal.json");
        if (IsReparsePoint(journalPath) || Directory.Exists(journalPath)) return true;
        try
        {
            string startedPath = Path.Combine(operationRoot, "helper-started.json");
            bool hasJournal = File.Exists(journalPath);
            bool hasStartedRecord = File.Exists(startedPath);
            if (!hasJournal && !hasStartedRecord && !IsReparsePoint(startedPath) && !Directory.Exists(startedPath)) return false;
            if (!hasJournal && (IsReparsePoint(startedPath) || Directory.Exists(startedPath))) return true;
            using var document = JsonDocument.Parse(File.ReadAllBytes(hasJournal ? journalPath : startedPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("OperationId", out var id)
                || id.ValueKind != JsonValueKind.String
                || !string.Equals(id.GetString(), Path.GetFileName(operationRoot), StringComparison.Ordinal)
                || !root.TryGetProperty("Nonce", out var nonce)
                || nonce.ValueKind != JsonValueKind.String
                || nonce.GetString() is not { Length: 64 }) return true;
            bool? expectedHealth = null;
            if (hasJournal)
            {
                if (!root.TryGetProperty("Stage", out var stage) || stage.ValueKind != JsonValueKind.String
                    || stage.GetString() is not ("healthy" or "rolled-back")) return true;
                expectedHealth = stage.GetString() == "healthy";
            }
            string resultPath = Path.Combine(operationRoot, "result.json");
            if (IsReparsePoint(resultPath) || !File.Exists(resultPath)) return true;
            using var result = JsonDocument.Parse(File.ReadAllBytes(resultPath));
            var outcome = result.RootElement;
            return !outcome.TryGetProperty("OperationId", out var resultId)
                || resultId.ValueKind != JsonValueKind.String || resultId.GetString() != id.GetString()
                || !outcome.TryGetProperty("Nonce", out var resultNonce)
                || resultNonce.ValueKind != JsonValueKind.String || resultNonce.GetString() != nonce.GetString()
                || !outcome.TryGetProperty("Healthy", out var healthy)
                || healthy.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || (expectedHealth.HasValue && healthy.GetBoolean() != expectedHealth.Value)
                || (!hasJournal && healthy.GetBoolean());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return true;
        }
    }

    private static string? ReadStagedOperationId(string referencePath)
    {
        try
        {
            if (IsReparsePoint(referencePath)) return null;
            return JsonSerializer.Deserialize<StagedReference>(File.ReadAllText(referencePath))?.OperationId;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private int CountOperationDirectories() => EnumerateEntriesIfManaged(_updatesRoot)
        .Count(entry => Guid.TryParseExact(Path.GetFileName(entry), "N", out _));

    private string[] EnumerateEntriesIfManaged(string root)
    {
        if (!Directory.Exists(root) || !HasManagedAncestors(root)) return [];
        return Directory.EnumerateFileSystemEntries(root).ToArray();
    }

    private void DeleteManagedEntry(string managedRoot, string path, CancellationToken cancellationToken)
    {
        string fullRoot = Path.GetFullPath(managedRoot);
        string fullPath = Path.GetFullPath(path);
        if (PathEquals(fullRoot, fullPath) || !IsWithinOrEqual(fullRoot, fullPath))
            throw new IOException("Refusing to remove a path outside the managed cache directory.");
        if (!HasManagedAncestors(fullRoot))
            throw new IOException("Refusing to follow a linked cache directory.");
        DeleteEntryNoFollow(fullPath, cancellationToken);
    }

    private bool HasManagedAncestors(string path)
    {
        if (!IsWithinOrEqual(_userDataRoot, path)) return false;
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (IsReparsePoint(current)) return false;
            if (PathEquals(current, _userDataRoot)) return true;
        }
        return false;
    }

    private bool IsInUse(string entry, ResourceWorkspace? workspace) =>
        IsWithinOrEqual(entry, _applicationBaseDirectory)
        || (workspace is not null && (IsWithinOrEqual(entry, workspace.ActiveResourceRoot)
            || IsWithinOrEqual(entry, workspace.SourceResourceRoot)));

    private static void DeleteEntryNoFollow(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path);
            else File.Delete(path);
            return;
        }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(path).ToArray())
                DeleteEntryNoFollow(child, cancellationToken);
            Directory.Delete(path);
            return;
        }
        File.Delete(path);
    }

    private static bool IsLockHeld(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return true; }
    }

    private static string? FindApplicationUpdateLock(string applicationBaseDirectory)
    {
        for (DirectoryInfo? directory = new(Path.GetFullPath(applicationBaseDirectory)); directory is not null; directory = directory.Parent)
        {
            if (!directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) continue;
            return Path.Combine(directory.Parent?.FullName ?? directory.FullName, ".umamusume-update.lock");
        }
        return null;
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool IsWithinOrEqual(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(fullRoot, fullPath);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, PathComparison));
    }

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
