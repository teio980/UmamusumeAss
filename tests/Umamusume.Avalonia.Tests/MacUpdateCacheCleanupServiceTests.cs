using System.Text.Json;
using Umamusume.AppCore.Platform;
using Umamusume.Avalonia.Services;
using Xunit;

namespace Umamusume.Avalonia.Tests;

public sealed class MacUpdateCacheCleanupServiceTests
{
    [Fact]
    public async Task RemovesOnlyOldUpdaterCachesAndInactiveResourceCopies()
    {
        string root = NewTemporaryRoot();
        try
        {
            string updates = Path.Combine(root, "updates");
            string checks = Path.Combine(updates, "checks");
            string releaseCache = Path.Combine(updates, "release-cache");
            Directory.CreateDirectory(checks);
            Directory.CreateDirectory(releaseCache);
            await File.WriteAllTextAsync(Path.Combine(checks, "manifest.json"), "check");
            await File.WriteAllTextAsync(Path.Combine(releaseCache, "releases.json"), "cache");

            string operationId = Guid.NewGuid().ToString("N");
            string operationRoot = Path.Combine(updates, operationId);
            Directory.CreateDirectory(operationRoot);
            await File.WriteAllTextAsync(Path.Combine(operationRoot, "package.zip"), "download");
            await File.WriteAllTextAsync(Path.Combine(updates, "UmamusumeAss-v0.3.4-osx-arm64.app.zip"), "legacy zip");
            await File.WriteAllTextAsync(Path.Combine(updates, "UmamusumeAss-v0.3.4-osx-arm64.app.zip.0123456789abcdef0123456789abcdef.part"), "partial");
            await File.WriteAllTextAsync(Path.Combine(updates, "staged-program.json"),
                JsonSerializer.Serialize(new { OperationId = operationId }));

            string revisions = Path.Combine(root, "resources", "revisions");
            const string activeRevision = "2026.10.20.1";
            const string workspaceRevision = "2026.10.20.2";
            const string oldRevision = "2026.10.19.1";
            string activePayload = await CreateRevisionAsync(revisions, activeRevision);
            string workspacePayload = await CreateRevisionAsync(revisions, workspaceRevision);
            string oldPayload = await CreateRevisionAsync(revisions, oldRevision);
            await File.WriteAllTextAsync(Path.Combine(root, "resources", "active.json"), JsonSerializer.Serialize(new
            {
                Version = activeRevision,
                RelativePath = Path.Combine("revisions", activeRevision),
            }));

            string config = Path.Combine(root, "config", "settings.json");
            string log = Path.Combine(root, "logs", "app.log");
            string draft = Path.Combine(root, "drafts", "pipeline.json");
            string overridePath = Path.Combine(root, "resource-overrides", "custom.json");
            await WriteTextAsync(config, "user settings");
            await WriteTextAsync(log, "user logs");
            await WriteTextAsync(draft, "unsaved draft");
            await WriteTextAsync(overridePath, "user override");

            ResourceWorkspace currentWorkspace = await ResourceWorkspace.PrepareAsync(workspacePayload, root);
            ResourceWorkspace oldWorkspace = await ResourceWorkspace.PrepareAsync(oldPayload, root);
            string currentView = currentWorkspace.ActiveBaseDirectory;
            string oldView = oldWorkspace.ActiveBaseDirectory;

            string appParent = Path.Combine(root, "Applications");
            string appBase = Path.Combine(appParent, "UmamusumeAss.app", "Contents", "MacOS");
            string appBackup = Path.Combine(appParent, "UmamusumeAss.app.previous");
            await WriteTextAsync(Path.Combine(appBase, "Umamusume.Avalonia"), "installed app");
            await WriteTextAsync(Path.Combine(appBackup, "Contents", "MacOS", "Umamusume.Avalonia"), "rollback backup");

            var service = new MacUpdateCacheCleanupService(root, appBase,
                Path.Combine(appParent, ".umamusume-update.lock"));
            MacUpdateCleanupResult result = await service.ClearAsync(currentWorkspace);

            Assert.False(result.InstallerRunning);
            Assert.False(result.ActivePointerInvalid);
            Assert.False(Directory.Exists(operationRoot));
            Assert.False(Directory.Exists(checks));
            Assert.False(Directory.Exists(releaseCache));
            Assert.False(File.Exists(Path.Combine(updates, "UmamusumeAss-v0.3.4-osx-arm64.app.zip")));
            Assert.False(File.Exists(Path.Combine(updates, "UmamusumeAss-v0.3.4-osx-arm64.app.zip.0123456789abcdef0123456789abcdef.part")));
            Assert.False(File.Exists(Path.Combine(updates, "staged-program.json")));

            Assert.True(Directory.Exists(Path.Combine(revisions, activeRevision)));
            Assert.True(Directory.Exists(Path.Combine(revisions, workspaceRevision)));
            Assert.False(Directory.Exists(Path.Combine(revisions, oldRevision)));
            Assert.True(Directory.Exists(currentView));
            Assert.False(Directory.Exists(oldView));
            Assert.True(File.Exists(config));
            Assert.True(File.Exists(log));
            Assert.True(File.Exists(draft));
            Assert.True(File.Exists(overridePath));
            Assert.True(File.Exists(Path.Combine(appBackup, "Contents", "MacOS", "Umamusume.Avalonia")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task KeepsIncompleteOrAmbiguousHelperJournalsAndRemovesOnlyMatchedTerminalResults()
    {
        string root = NewTemporaryRoot();
        try
        {
            string updates = Path.Combine(root, "updates");
            var preserved = new List<string>();
            var removed = new List<string>();

            foreach (string stage in new[] { "prepared", "swapping", "swapped", "health-check", "unknown" })
            {
                string id = Guid.NewGuid().ToString("N");
                preserved.Add(id);
                string operation = CreateOperation(updates, id);
                string nonce = new('a', 64);
                await File.WriteAllTextAsync(Path.Combine(operation, "journal.json"), JsonSerializer.Serialize(new
                {
                    OperationId = id,
                    Nonce = nonce,
                    Stage = stage,
                }));
                await WriteResultAsync(operation, id, nonce, healthy: stage == "healthy");
            }

            string healthyId = Guid.NewGuid().ToString("N");
            removed.Add(healthyId);
            string healthyOperation = CreateOperation(updates, healthyId);
            string healthyNonce = new('b', 64);
            await WriteJournalAsync(healthyOperation, healthyId, healthyNonce, "healthy");
            await WriteResultAsync(healthyOperation, healthyId, healthyNonce, healthy: true);

            string rolledBackId = Guid.NewGuid().ToString("N");
            removed.Add(rolledBackId);
            string rolledBackOperation = CreateOperation(updates, rolledBackId);
            string rolledBackNonce = new('c', 64);
            await WriteJournalAsync(rolledBackOperation, rolledBackId, rolledBackNonce, "rolled-back");
            await WriteResultAsync(rolledBackOperation, rolledBackId, rolledBackNonce, healthy: false);

            string missingResultId = Guid.NewGuid().ToString("N");
            preserved.Add(missingResultId);
            string missingResultOperation = CreateOperation(updates, missingResultId);
            await WriteJournalAsync(missingResultOperation, missingResultId, new string('d', 64), "healthy");

            string wrongNonceId = Guid.NewGuid().ToString("N");
            preserved.Add(wrongNonceId);
            string wrongNonceOperation = CreateOperation(updates, wrongNonceId);
            await WriteJournalAsync(wrongNonceOperation, wrongNonceId, new string('e', 64), "rolled-back");
            await WriteResultAsync(wrongNonceOperation, wrongNonceId, new string('f', 64), healthy: false);

            string wrongHealthId = Guid.NewGuid().ToString("N");
            preserved.Add(wrongHealthId);
            string wrongHealthOperation = CreateOperation(updates, wrongHealthId);
            await WriteJournalAsync(wrongHealthOperation, wrongHealthId, new string('1', 64), "healthy");
            await WriteResultAsync(wrongHealthOperation, wrongHealthId, new string('1', 64), healthy: false);

            string malformedId = Guid.NewGuid().ToString("N");
            preserved.Add(malformedId);
            string malformedOperation = CreateOperation(updates, malformedId);
            await File.WriteAllTextAsync(Path.Combine(malformedOperation, "journal.json"), "not json");

            var service = new MacUpdateCacheCleanupService(root, applicationBaseDirectory: root,
                applicationUpdateLockPath: Path.Combine(root, "missing.lock"));
            await service.ClearAsync(currentWorkspace: null);

            foreach (string id in preserved) Assert.True(Directory.Exists(Path.Combine(updates, id)), $"Expected operation {id} to be preserved.");
            foreach (string id in removed) Assert.False(Directory.Exists(Path.Combine(updates, id)), $"Expected operation {id} to be removed.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HelperStartedWithoutJournalRequiresMatchingFailedResultBeforeCleanup()
    {
        string root = NewTemporaryRoot();
        try
        {
            string updates = Path.Combine(root, "updates");
            string failedId = Guid.NewGuid().ToString("N");
            string failedOperation = CreateOperation(updates, failedId);
            string failedNonce = new('a', 64);
            await File.WriteAllTextAsync(Path.Combine(failedOperation, "helper-started.json"), JsonSerializer.Serialize(new
            {
                OperationId = failedId,
                Nonce = failedNonce,
                Started = true,
            }));
            await WriteResultAsync(failedOperation, failedId, failedNonce, healthy: false);

            string pendingId = Guid.NewGuid().ToString("N");
            string pendingOperation = CreateOperation(updates, pendingId);
            string pendingNonce = new('b', 64);
            await File.WriteAllTextAsync(Path.Combine(pendingOperation, "helper-started.json"), JsonSerializer.Serialize(new
            {
                OperationId = pendingId,
                Nonce = pendingNonce,
                Started = true,
            }));

            string healthyWithoutJournalId = Guid.NewGuid().ToString("N");
            string healthyWithoutJournalOperation = CreateOperation(updates, healthyWithoutJournalId);
            string healthyNonce = new('c', 64);
            await File.WriteAllTextAsync(Path.Combine(healthyWithoutJournalOperation, "helper-started.json"), JsonSerializer.Serialize(new
            {
                OperationId = healthyWithoutJournalId,
                Nonce = healthyNonce,
                Started = true,
            }));
            await WriteResultAsync(healthyWithoutJournalOperation, healthyWithoutJournalId, healthyNonce, healthy: true);

            string wrongNonceId = Guid.NewGuid().ToString("N");
            string wrongNonceOperation = CreateOperation(updates, wrongNonceId);
            string actualNonce = new('d', 64);
            await File.WriteAllTextAsync(Path.Combine(wrongNonceOperation, "helper-started.json"), JsonSerializer.Serialize(new
            {
                OperationId = wrongNonceId,
                Nonce = actualNonce,
                Started = true,
            }));
            await WriteResultAsync(wrongNonceOperation, wrongNonceId, new string('e', 64), healthy: false);

            var service = new MacUpdateCacheCleanupService(root, applicationBaseDirectory: root,
                applicationUpdateLockPath: Path.Combine(root, "missing.lock"));
            await service.ClearAsync(currentWorkspace: null);

            Assert.False(Directory.Exists(failedOperation));
            Assert.True(Directory.Exists(pendingOperation));
            Assert.True(Directory.Exists(healthyWithoutJournalOperation));
            Assert.True(Directory.Exists(wrongNonceOperation));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidActivePointerConservativelyPreservesEveryResourceRevision()
    {
        string root = NewTemporaryRoot();
        try
        {
            string revisions = Path.Combine(root, "resources", "revisions");
            string first = Path.Combine(revisions, "2026.10.20.1");
            string second = Path.Combine(revisions, "2026.10.21.2");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            await File.WriteAllTextAsync(Path.Combine(first, "marker"), "first");
            await File.WriteAllTextAsync(Path.Combine(second, "marker"), "second");
            await WriteTextAsync(Path.Combine(root, "resources", "active.json"), "{");

            var service = new MacUpdateCacheCleanupService(root, applicationBaseDirectory: root,
                applicationUpdateLockPath: Path.Combine(root, "missing.lock"));
            MacUpdateCleanupResult result = await service.ClearAsync(currentWorkspace: null);

            Assert.True(result.ActivePointerInvalid);
            Assert.True(File.Exists(Path.Combine(first, "marker")));
            Assert.True(File.Exists(Path.Combine(second, "marker")));

            File.Delete(Path.Combine(root, "resources", "active.json"));
            Directory.CreateDirectory(Path.Combine(root, "resources", "active.json"));
            result = await service.ClearAsync(currentWorkspace: null);

            Assert.True(result.ActivePointerInvalid);
            Assert.True(File.Exists(Path.Combine(first, "marker")));
            Assert.True(File.Exists(Path.Combine(second, "marker")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HeldApplicationUpdateLockPreventsEveryCleanupPass()
    {
        string root = NewTemporaryRoot();
        try
        {
            string updates = Path.Combine(root, "updates");
            string operation = CreateOperation(updates, Guid.NewGuid().ToString("N"));
            string activeRevision = Path.Combine(root, "resources", "revisions", "2026.10.21.1");
            Directory.CreateDirectory(activeRevision);
            await File.WriteAllTextAsync(Path.Combine(activeRevision, "marker"), "resource");
            await WriteTextAsync(Path.Combine(root, "resources", "active.json"), JsonSerializer.Serialize(new
            {
                Version = "2026.10.21.1",
                RelativePath = Path.Combine("revisions", "2026.10.21.1"),
            }));
            string lockPath = Path.Combine(root, "app.lock");
            await using var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            var service = new MacUpdateCacheCleanupService(root, applicationBaseDirectory: root,
                applicationUpdateLockPath: lockPath);
            MacUpdateCleanupResult result = await service.ClearAsync(currentWorkspace: null);

            Assert.True(result.InstallerRunning);
            Assert.True(Directory.Exists(operation));
            Assert.True(File.Exists(Path.Combine(activeRevision, "marker")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelingAfterDownloadCleanupLeavesCurrentAndUnvisitedResourceRevisionsIntact()
    {
        string root = NewTemporaryRoot();
        try
        {
            string updates = Path.Combine(root, "updates");
            string oldOperation = CreateOperation(updates, Guid.NewGuid().ToString("N"));
            string revisions = Path.Combine(root, "resources", "revisions");
            string active = Path.Combine(revisions, "2026.10.21.1");
            string unused = Path.Combine(revisions, "2026.10.20.1");
            Directory.CreateDirectory(active);
            Directory.CreateDirectory(unused);
            await WriteTextAsync(Path.Combine(active, "marker"), "active");
            await WriteTextAsync(Path.Combine(unused, "marker"), "unused");
            await WriteTextAsync(Path.Combine(root, "resources", "active.json"), JsonSerializer.Serialize(new
            {
                Version = "2026.10.21.1",
                RelativePath = Path.Combine("revisions", "2026.10.21.1"),
            }));

            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<MacUpdateCleanupStage>(stage =>
            {
                if (stage == MacUpdateCleanupStage.RemovingResourceRevisions) cancellation.Cancel();
            });
            var service = new MacUpdateCacheCleanupService(root, applicationBaseDirectory: root,
                applicationUpdateLockPath: Path.Combine(root, "missing.lock"));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ClearAsync(currentWorkspace: null, progress, cancellation.Token));

            Assert.False(Directory.Exists(oldOperation));
            Assert.True(File.Exists(Path.Combine(active, "marker")));
            Assert.True(File.Exists(Path.Combine(unused, "marker")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> CreateRevisionAsync(string revisionsRoot, string version)
    {
        string payload = Path.Combine(revisionsRoot, version, "payload");
        await WriteTextAsync(Path.Combine(payload, "resource.json"), version);
        return payload;
    }

    private static string CreateOperation(string updatesRoot, string operationId)
    {
        string path = Path.Combine(updatesRoot, operationId);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "fixture"), "operation");
        return path;
    }

    private static Task WriteJournalAsync(string operationRoot, string operationId, string nonce, string stage) =>
        File.WriteAllTextAsync(Path.Combine(operationRoot, "journal.json"), JsonSerializer.Serialize(new
        {
            OperationId = operationId,
            Nonce = nonce,
            Stage = stage,
        }));

    private static Task WriteResultAsync(string operationRoot, string operationId, string nonce, bool healthy) =>
        File.WriteAllTextAsync(Path.Combine(operationRoot, "result.json"), JsonSerializer.Serialize(new
        {
            OperationId = operationId,
            Nonce = nonce,
            Healthy = healthy,
        }));

    private static async Task WriteTextAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private static string NewTemporaryRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "uma-mac-update-cleanup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
