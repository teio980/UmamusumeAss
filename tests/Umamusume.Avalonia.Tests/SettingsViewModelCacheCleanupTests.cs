using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Platform;
using Umamusume.AppCore.Services;
using Umamusume.Avalonia.Services;
using Umamusume.Avalonia.ViewModels;
using Umamusume.CoreBridge;
using Umamusume.UpdateCore;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Update;
using Xunit;

namespace Umamusume.Avalonia.Tests;

public sealed partial class SettingsViewModelTests
{
    private static readonly JsonSerializerOptions CleanupManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task CleanupCommandResynchronizesStagedStateAndSerializesThroughMaintenanceGate()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return;
        string root = NewTemporaryRoot();
        try
        {
            var stagedFixture = await CreateStagedProgramAsync(root);
            var resourceStore = await CreateActiveResourceStoreAsync(root);
            var cleanup = new CleanupProbe();
            cleanup.Handler = _ =>
            {
                DeleteStagedProgram(stagedFixture.Root, stagedFixture.Staged);
                cleanup.Deleted.TrySetResult(true);
                return Task.FromResult(new MacUpdateCleanupResult(1, 0, 0, 0, false, false));
            };
            var automation = new FakeAutomationService
            {
                MaintenanceEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
                MaintenanceRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var vm = CreateViewModel(stagedFixture, root, automation, cleanup, resourceStore);
            await vm.RestoreStagedUpdatesAsync();
            Assert.True(vm.HasProgramStaged);
            SetStaleResourceCheck(vm, await resourceStore.ReadActiveVersionAsync());
            vm.HasResourceUpdate = true;

            Task command = vm.DeleteOldResourcesCommand.ExecuteAsync(null);
            await automation.MaintenanceEntered!.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.IsUpdating);
            await vm.CheckUpdatesCommand.ExecuteAsync(null);
            await vm.DeleteOldResourcesCommand.ExecuteAsync(null);
            Assert.Equal(1, automation.MaintenanceCallCount);
            Assert.Equal(0, cleanup.CallCount);

            automation.MaintenanceRelease!.TrySetResult(true);
            await command.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, cleanup.CallCount);
            Assert.False(vm.IsUpdating);
            Assert.False(vm.HasProgramStaged);
            Assert.False(vm.HasResourceUpdate);
            Assert.Contains("Removed 1 update cache item", vm.UpdateStatus, StringComparison.Ordinal);
            Assert.Empty(vm.ProgramUpdateProgress);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task CleanupFailureOrCancellationStillRevalidatesStagedPackages(string outcome)
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return;
        string root = NewTemporaryRoot();
        try
        {
            var stagedFixture = await CreateStagedProgramAsync(root);
            var cleanup = new CleanupProbe();
            cleanup.Handler = async cancellationToken =>
            {
                DeleteStagedProgram(stagedFixture.Root, stagedFixture.Staged);
                cleanup.Deleted.TrySetResult(true);
                if (outcome == "failure") throw new IOException("fixture cleanup failure");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Cancellation was expected.");
            };
            var vm = CreateViewModel(stagedFixture, root, new FakeAutomationService(), cleanup);
            await vm.RestoreStagedUpdatesAsync();
            Assert.True(vm.HasProgramStaged);

            Task command = vm.DeleteOldResourcesCommand.ExecuteAsync(null);
            if (outcome == "cancel")
            {
                await cleanup.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                vm.CancelUpdateCommand.Execute(null);
            }
            await command.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(vm.IsUpdating);
            Assert.False(vm.HasProgramStaged);
            Assert.Empty(vm.ProgramUpdateProgress);
            Assert.False(File.Exists(Path.Combine(stagedFixture.Root, "updates", "staged-program.json")));
            Assert.False(Directory.Exists(Path.GetDirectoryName(stagedFixture.Staged.PackagePath)));
            Assert.Contains(outcome == "failure" ? "Cache deletion failed" : "Cache deletion canceled", vm.UpdateStatus, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CleanupRefusesWhenResourceRecoveryRequiresRestart()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return;
        string root = NewTemporaryRoot();
        try
        {
            var stagedFixture = await CreateStagedProgramAsync(root);
            var cleanup = new CleanupProbe();
            var automation = new FakeAutomationService { RequiresRestartForMaintenance = true };
            var vm = CreateViewModel(stagedFixture, root, automation, cleanup);
            await vm.RestoreStagedUpdatesAsync();

            await vm.DeleteOldResourcesCommand.ExecuteAsync(null);

            Assert.Equal(1, automation.MaintenanceCallCount);
            Assert.Equal(0, cleanup.CallCount);
            Assert.False(vm.IsUpdating);
            Assert.True(vm.HasProgramStaged);
            Assert.Contains("Restart the application", vm.UpdateStatus, StringComparison.Ordinal);
            Assert.True(File.Exists(stagedFixture.Staged.PackagePath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static SettingsViewModel CreateViewModel(
        (string Root, MacUpdateService Service, StagedUpdate Staged) stagedFixture,
        string root,
        FakeAutomationService automation,
        IMacUpdateCacheCleanupService cleanup,
        MacResourceStore? resourceStore = null)
    {
        var settings = new MemorySettings(new ConnectionSettings { Language = "en-US", Theme = "System" });
        return new SettingsViewModel(settings, new LogService(),
            new LocalizationService { CurrentLanguage = "en-US" }, automation,
            stagedFixture.Service, resourceStore ?? new MacResourceStore(root), cleanup);
    }

    private static async Task<(string Root, MacUpdateService Service, StagedUpdate Staged)> CreateStagedProgramAsync(string root)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = new ManifestVerifier(signer.ExportSubjectPublicKeyInfo());
        const string version = "999.0.0";
        const string tag = "v999.0.0";
        const string entryPath = "UmamusumeAss.app/Contents/Resources/version.txt";
        byte[] content = Encoding.UTF8.GetBytes(version);

        byte[] packageBytes;
        using (var buffer = new MemoryStream())
        {
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
                entry.ExternalAttributes = unchecked((0x81A4) << 16);
                using var stream = entry.Open();
                await stream.WriteAsync(content);
            }
            packageBytes = buffer.ToArray();
        }

        var file = new UpdateFileEntry
        {
            Path = entryPath,
            Size = content.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(content)),
        };
        var asset = new UpdateAsset
        {
            Type = "full",
            AssetName = $"UmamusumeAss-v{version}-osx-arm64.app.zip",
            Size = packageBytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(packageBytes)),
            TargetTreeSha256 = ComputeTreeHash(entryPath, content),
            Files = [file],
        };
        var manifest = new UpdateManifest
        {
            Kind = "program",
            TargetOs = "macos",
            TargetArch = "arm64",
            MinOsVersion = "14.0",
            Version = version,
            ReleaseTag = tag,
            BundledResourceVersion = "2026.10.21.1",
            MinAppVersion = "0.0.0",
            Assets = [asset],
        };
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, CleanupManifestJsonOptions);
        byte[] signature = signer.SignData(manifestBytes, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        string operationId = Guid.NewGuid().ToString("N");
        string operationRoot = Path.Combine(root, "updates", operationId);
        string payloadRoot = Path.Combine(operationRoot, "payload");
        Directory.CreateDirectory(Path.Combine(payloadRoot, Path.GetDirectoryName(entryPath)!));
        await File.WriteAllBytesAsync(Path.Combine(payloadRoot, entryPath), content);
        Directory.CreateDirectory(operationRoot);
        string packagePath = Path.Combine(operationRoot, asset.AssetName);
        await File.WriteAllBytesAsync(packagePath, packageBytes);
        string manifestPath = Path.Combine(operationRoot, "manifest.json");
        await File.WriteAllBytesAsync(manifestPath, manifestBytes);
        string signaturePath = Path.Combine(operationRoot, "manifest.sig");
        await File.WriteAllTextAsync(signaturePath, Convert.ToBase64String(signature));

        var service = new MacUpdateService(new HttpClient(new RejectRequestsHandler()), root, verifier);
        var staged = new StagedUpdate(operationId, UpdateScope.Program, manifest, asset,
            packagePath, payloadRoot, manifestPath, signaturePath);
        service.RememberStaged(staged);
        return (root, service, staged);
    }

    private static string ComputeTreeHash(string path, byte[] content)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(path));
        hash.AppendData([0]);
        hash.AppendData(content);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void SetStaleResourceCheck(SettingsViewModel vm, string currentResourceVersion)
    {
        string[] parts = currentResourceVersion.Split('.');
        string previous = currentResourceVersion;
        if (parts.Length == 4 && int.TryParse(parts[3], out int revision) && revision > 0)
            previous = $"{parts[0]}.{parts[1]}.{parts[2]}.{revision - 1}";
        var check = new MacUpdateCheck(MacManifestCompatibility.CurrentAppVersion(), currentResourceVersion,
            ProgramManifest: null, ProgramRelease: null,
            ResourceManifest: new UpdateManifest { Kind = "resource", Version = previous },
            ResourceRelease: new GitHubRelease($"resource-v{previous}", []));
        typeof(SettingsViewModel).GetField("_macUpdateCheck", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, check);
    }

    private static async Task<MacResourceStore> CreateActiveResourceStoreAsync(string root)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = new ManifestVerifier(signer.ExportSubjectPublicKeyInfo());
        const string version = "2026.10.21.9";
        const string tag = "resource-v2026.10.21.9";
        const string filePath = "resource/catalog.json";
        byte[] content = Encoding.UTF8.GetBytes("signed resource fixture");
        var asset = new UpdateAsset
        {
            Type = "full",
            AssetName = "UmamusumeAss-resource-v2026.10.21.9-osx-arm64-full.zip",
            Size = content.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(content)),
            TargetTreeSha256 = ComputeTreeHash(filePath, content),
            Files = [new UpdateFileEntry
            {
                Path = filePath,
                Size = content.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(content)),
            }],
        };
        var manifest = new UpdateManifest
        {
            Kind = "resource",
            TargetOs = "macos",
            TargetArch = "arm64",
            MinOsVersion = "14.0",
            Version = version,
            ReleaseTag = tag,
            BundledResourceVersion = version,
            MinAppVersion = "0.0.0",
            ResourceSchema = "1",
            Assets = [asset],
        };
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, CleanupManifestJsonOptions);
        byte[] signature = signer.SignData(manifestBytes, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        string stagedRoot = Path.Combine(root, "resource-stage");
        string payloadRoot = Path.Combine(stagedRoot, "payload");
        string payloadFile = Path.Combine(payloadRoot, filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(payloadFile)!);
        await File.WriteAllBytesAsync(payloadFile, content);
        string manifestPath = Path.Combine(stagedRoot, "manifest.json");
        await File.WriteAllBytesAsync(manifestPath, manifestBytes);
        string signaturePath = Path.Combine(stagedRoot, "manifest.sig");
        await File.WriteAllTextAsync(signaturePath, Convert.ToBase64String(signature));
        var staged = new StagedUpdate(Guid.NewGuid().ToString("N"), UpdateScope.Resource,
            manifest, asset, Path.Combine(stagedRoot, asset.AssetName), payloadRoot,
            manifestPath, signaturePath);

        var store = new MacResourceStore(root, verifier);
        await store.InstallRevisionAsync(staged);
        await store.CommitActiveAsync(version);
        return store;
    }

    private static void DeleteStagedProgram(string root, StagedUpdate staged)
    {
        string reference = Path.Combine(root, "updates", "staged-program.json");
        if (File.Exists(reference)) File.Delete(reference);
        string? operationRoot = Path.GetDirectoryName(staged.PackagePath);
        if (operationRoot is not null && Directory.Exists(operationRoot)) Directory.Delete(operationRoot, recursive: true);
    }

    private static string NewTemporaryRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "uma-vm-cleanup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class CleanupProbe : IMacUpdateCacheCleanupService
    {
        public Func<CancellationToken, Task<MacUpdateCleanupResult>> Handler { get; set; } =
            _ => Task.FromResult(new MacUpdateCleanupResult(0, 0, 0, 0, false, false));
        public TaskCompletionSource<bool> Deleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public Task<MacUpdateCleanupResult> ClearAsync(ResourceWorkspace? currentWorkspace,
            IProgress<MacUpdateCleanupStage>? progress = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Handler(cancellationToken);
        }
    }

    private sealed class RejectRequestsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The test fixture must not make network requests.");
    }
}
