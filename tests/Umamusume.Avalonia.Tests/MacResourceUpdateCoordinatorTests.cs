using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Platform;
using Umamusume.AppCore.Services;
using Umamusume.Avalonia.Services;
using Umamusume.CoreBridge;
using Umamusume.UpdateCore;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Update;
using Xunit;

namespace Umamusume.Avalonia.Tests;

public sealed class MacResourceUpdateCoordinatorTests
{
    [Fact]
    public async Task SuccessfulApplyCommitsPointerAndKeepsOverridesInTemporaryViews()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = await ResourceFixture.CreateAsync(FailureMode.None);

        string version = await fixture.Coordinator.ApplyAsync(fixture.Staged, fixture.Automation);

        Assert.Equal(fixture.Staged.Manifest.Version, version);
        Assert.Equal(version, await fixture.Store.ReadActiveVersionAsync());
        Assert.Equal(fixture.Automation.ActiveResourceWorkspace!.ActiveBaseDirectory, fixture.Core.ResourcePath);
        Assert.Equal("preserved override", await File.ReadAllTextAsync(
            Path.Combine(fixture.Automation.ActiveResourceWorkspace.ActiveResourceRoot, "override.txt")));
        Assert.StartsWith(Path.Combine(fixture.Root, "data", "runtime", "views"),
            fixture.Automation.ActiveResourceWorkspace.ActiveResourceRoot, StringComparison.Ordinal);
        Assert.NotNull(await fixture.Store.GetPreferredResourceSourceRootAsync(fixture.BundledRoot, fixture.DataRoot));
    }

    [Theory]
    [InlineData(FailureMode.Reload)]
    [InlineData(FailureMode.Catalog)]
    public async Task FailedReloadOrCatalogRestoresNativeWorkspacePointerAndOverrides(FailureMode failure)
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = await ResourceFixture.CreateAsync(failure);
        string oldBase = fixture.Automation.ActiveResourceWorkspace!.ActiveBaseDirectory;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.ApplyAsync(fixture.Staged, fixture.Automation));

        Assert.Equal(oldBase, fixture.Core.ResourcePath);
        Assert.Equal(oldBase, fixture.Automation.ActiveResourceWorkspace!.ActiveBaseDirectory);
        Assert.Equal(fixture.OldVersion, await fixture.Store.ReadActiveVersionAsync());
        byte[]? restoredPointer = await fixture.Store.CaptureActivePointerAsync();
        Assert.NotNull(restoredPointer);
        Assert.Equal(fixture.OldPointer, restoredPointer);
        Assert.Equal(fixture.OldPayloadRoot,
            await fixture.Store.GetPreferredResourceSourceRootAsync(fixture.BundledRoot, fixture.DataRoot));
        Assert.Equal("preserved override", await File.ReadAllTextAsync(
            Path.Combine(fixture.Automation.ActiveResourceWorkspace.ActiveResourceRoot, "override.txt")));
        Assert.False(fixture.Automation.RequiresRestartForMaintenance);
    }

    public enum FailureMode { None, Reload, Catalog }

    private sealed class ResourceFixture : IDisposable
    {
        private ResourceFixture(string root, string dataRoot, string bundledRoot, string oldVersion,
            string oldPayloadRoot, byte[] oldPointer, MacResourceStore store, FakeAutomation automation, StagedUpdate staged)
        {
            Root = root;
            DataRoot = dataRoot;
            BundledRoot = bundledRoot;
            OldVersion = oldVersion;
            OldPayloadRoot = oldPayloadRoot;
            OldPointer = oldPointer;
            Store = store;
            Automation = automation;
            Staged = staged;
            Coordinator = new MacResourceUpdateCoordinator(store);
        }

        public string Root { get; }
        public string DataRoot { get; }
        public string BundledRoot { get; }
        public string OldVersion { get; }
        public string OldPayloadRoot { get; }
        public byte[] OldPointer { get; }
        public MacResourceStore Store { get; }
        public FakeAutomation Automation { get; }
        public FakeCore Core => (FakeCore)Automation.CoreService;
        public StagedUpdate Staged { get; }
        public MacResourceUpdateCoordinator Coordinator { get; }

        public static async Task<ResourceFixture> CreateAsync(FailureMode failure)
        {
            string root = Path.Combine(Path.GetTempPath(), "uma mac resource transaction " + Guid.NewGuid().ToString("N"));
            string dataRoot = Path.Combine(root, "data");
            string bundledRoot = Path.Combine(root, "bundle", "resource");
            Directory.CreateDirectory(bundledRoot);
            Directory.CreateDirectory(Path.Combine(dataRoot, "resource-overrides"));
            await File.WriteAllTextAsync(Path.Combine(dataRoot, "resource-overrides", "override.txt"), "preserved override");
            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var versions = await GetTestVersionsAsync();
            var verifier = new ManifestVerifier(signer.ExportSubjectPublicKeyInfo());
            var store = new MacResourceStore(dataRoot, verifier);
            var oldStaged = await CreateSignedStagedAsync(Path.Combine(root, "staged-old"), versions.Old,
                "old signed resource tree", signer);
            string oldPayloadRoot = await store.InstallRevisionAsync(oldStaged);
            await store.CommitActiveAsync(versions.Old);
            byte[] oldPointer = await store.CaptureActivePointerAsync()
                ?? throw new InvalidDataException("The fixture's initial active pointer was not written.");
            var oldWorkspace = await ResourceWorkspace.PrepareAsync(oldPayloadRoot, dataRoot);
            var staged = await CreateSignedStagedAsync(Path.Combine(root, "staged-new"), versions.New,
                "new resource tree", signer);
            var core = new FakeCore(oldWorkspace.ActiveBaseDirectory, failure);
            var automation = new FakeAutomation(core, oldWorkspace);
            var fixture = new ResourceFixture(root, dataRoot, bundledRoot, versions.Old,
                oldPayloadRoot, oldPointer, store, automation, staged);
            return fixture;
        }

        private static async Task<(string Old, string New)> GetTestVersionsAsync()
        {
            string bundled = await MacResourceStore.ReadBundledVersionAsync();
            string[] parts = bundled.Split('.');
            bool hasResourceDate = parts.Length == 4
                && parts[0].Length == 4 && parts[1].Length == 2 && parts[2].Length == 2
                && parts.All(part => int.TryParse(part, out _));
            if (!hasResourceDate) parts = DateTime.UtcNow.ToString("yyyy.MM.dd.0", System.Globalization.CultureInfo.InvariantCulture).Split('.');
            int revision = int.TryParse(parts[3], out int parsed) ? parsed : 0;
            parts[3] = checked(revision + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string oldVersion = string.Join(".", parts);
            parts[3] = checked(revision + 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return (oldVersion, string.Join(".", parts));
        }

        private static async Task<StagedUpdate> CreateSignedStagedAsync(
            string stageRoot, string version, string contents, ECDsa signer)
        {
            string payloadRoot = Path.Combine(stageRoot, "payload");
            Directory.CreateDirectory(payloadRoot);
            byte[] payload = Encoding.UTF8.GetBytes(contents);
            await File.WriteAllBytesAsync(Path.Combine(payloadRoot, "base.txt"), payload);
            string fileHash = Convert.ToHexString(SHA256.HashData(payload));
            var file = new UpdateFileEntry { Path = "base.txt", Size = payload.Length, Sha256 = fileHash };
            using var tree = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            tree.AppendData(Encoding.UTF8.GetBytes(file.Path));
            tree.AppendData([0]);
            tree.AppendData(payload);
            string targetTreeHash = Convert.ToHexString(tree.GetHashAndReset());
            var asset = new UpdateAsset
            {
                Type = "full",
                AssetName = "uma-resource-test.zip",
                Size = payload.Length,
                Sha256 = fileHash,
                TargetTreeSha256 = targetTreeHash,
                Files = [file],
            };
            var manifest = new UpdateManifest
            {
                Kind = "resource",
                TargetOs = "macos",
                TargetArch = "arm64",
                MinOsVersion = "14.0",
                Version = version,
                ReleaseTag = "resource-v" + version,
                BundledResourceVersion = "0.0.0.0",
                MinAppVersion = "0.0.0",
                ResourceSchema = "1",
                Assets = [asset],
            };
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            byte[] signature = signer.SignData(manifestBytes, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            string manifestPath = Path.Combine(stageRoot, "manifest.json");
            string signaturePath = Path.Combine(stageRoot, "manifest.sig");
            await File.WriteAllBytesAsync(manifestPath, manifestBytes);
            await File.WriteAllTextAsync(signaturePath, Convert.ToBase64String(signature));
            return new StagedUpdate("fixture-op", UpdateScope.Resource,
                ManifestVerifier.Deserialize(manifestBytes), asset, Path.Combine(stageRoot, asset.AssetName),
                payloadRoot, manifestPath, signaturePath);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FakeAutomation(FakeCore core, ResourceWorkspace workspace) : IUmaAutomationService
    {
        private ResourceWorkspace _workspace = workspace;
        private string? _restartReason;
        public bool IsInitialized => true;
        public bool IsConnected => false;
        public bool IsBusy => false;
        public bool RequiresRestartForMaintenance => _restartReason is not null;
        public string? NativeCoreVersion => "fixture";
        public string? ResourcePath => _workspace.ActiveResourceRoot;
        public ResourceWorkspace? ActiveResourceWorkspace => _workspace;
        public DeviceSessionIdentity? ActiveDeviceIdentity => null;
        public IUmaService CoreService => core;
        public string CurrentStatus => "Idle";
        public ConnectedDeviceInfo? CurrentDevice => null;
        public event Action<string>? StatusChanged { add { } remove { } }
        public event Action<bool>? BusyChanged { add { } remove { } }
        public event Action<CapturedScreenshot>? ScreenshotCaptured { add { } remove { } }
        public event Action<string>? ResourcesChanged { add { } remove { } }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public string ResolveActiveResourcePath(string sourcePath) => Path.Combine(_workspace.ActiveResourceRoot, Path.GetFileName(sourcePath));
        public Task<bool> SaveResourceTextAsync(string path, string text, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> SaveResourceBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> ConnectAsync(string adbPath, string serial, string profile = "General", CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CapturedScreenshot?> CaptureScreenshotAsync(CancellationToken cancellationToken = default) => Task.FromResult<CapturedScreenshot?>(null);
        public Task<TextRecognitionResult?> RecognizeOcrAsync(CapturedScreenshot? screenshot = null, string language = "eng", CancellationToken cancellationToken = default) => Task.FromResult<TextRecognitionResult?>(null);
        public Task<bool> StartGameAndRecognizeHomeAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
        public Task<T> RunMaintenanceWhenIdleAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
        public void ActivateResourceWorkspace(ResourceWorkspace next) => _workspace = next;
        public void RequireRestartAfterMaintenanceFailure(string reason) => _restartReason = reason;
        public Task<UmaTaskQueueRunResult> RunTaskQueueAsync(IReadOnlyList<UmaTaskRequest> requests, Action<UmaTaskQueueEvent>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new UmaTaskQueueRunResult(false, false, 0, 0, 0, "unused"));
        public void CancelCurrentOperation() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCore(string initialResourcePath, FailureMode failure) : IUmaService
    {
        private bool _reloadFailed;
        private bool _catalogFailed;
        public string? CoreVersion => "fixture";
        public string? ResourcePath { get; private set; } = initialResourcePath;
        public event Action<ConnectionEvent>? ConnectionEventReceived { add { } remove { } }
        public event Action<BridgeDiagnostic>? DiagnosticReceived { add { } remove { } }
        public Task InitializeAsync(string appBaseDir, string appDataDir, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReloadResourceAsync(string compositeBaseDirectory, CancellationToken cancellationToken = default)
        {
            ResourcePath = compositeBaseDirectory;
            if (failure == FailureMode.Reload && !_reloadFailed)
            {
                _reloadFailed = true;
                throw new InvalidOperationException("injected native resource reload failure");
            }
            return Task.CompletedTask;
        }
        public Task<CoreCatalog> GetCatalogAsync(string catalogType = "all", CancellationToken cancellationToken = default)
        {
            if (failure == FailureMode.Catalog && !_catalogFailed)
            {
                _catalogFailed = true;
                throw new InvalidOperationException("injected catalog validation failure");
            }
            return Task.FromResult(CoreCatalog.Empty);
        }
        public Task<ConnectionTerminalEvent> ConnectAsync(string adbPath, string serial, string profile, CancellationToken cancellationToken = default) =>
            Task.FromException<ConnectionTerminalEvent>(new NotSupportedException());
        public Task CancelOperationAsync(ulong operationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
