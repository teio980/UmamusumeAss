using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;
using ActivityKind = UmamusumeWpfGui.Models.ActivityKind;

namespace UmamusumeWpfGui.Services.Update;

public sealed class UpdateCoordinator : IUpdateService
{
    private readonly GitHubReleaseClient _github;
    private readonly ManifestVerifier _verifier;
    private readonly PackageStager _stager;
    private readonly IActivityRegistry _activities;
    private readonly IResourceStore _resources;
    private readonly IUmaService _uma;
    private readonly IUmaDatabaseService _database;
    private readonly UpdateStateStore _state;
    private readonly string _appDataRoot;
    private GitHubRelease? _programRelease;
    private GitHubRelease? _resourceRelease;
    private byte[]? _programManifestBytes;
    private byte[]? _programSignature;
    private byte[]? _resourceManifestBytes;
    private byte[]? _resourceSignature;

    public UpdateCoordinator(
        GitHubReleaseClient github,
        ManifestVerifier verifier,
        PackageStager stager,
        IActivityRegistry activities,
        IResourceStore resources,
        IUmaService uma,
        IUmaDatabaseService database,
        UpdateStateStore state)
    {
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _stager = stager ?? throw new ArgumentNullException(nameof(stager));
        _activities = activities ?? throw new ArgumentNullException(nameof(activities));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _uma = uma ?? throw new ArgumentNullException(nameof(uma));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UmamusumeAss");
    }

    public async Task<UpdateCheckResult> CheckAsync(
        UpdateScope scope,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var releases = await _github.GetStableReleasesAsync(cancellationToken).ConfigureAwait(false);
            UpdateManifest? program = null;
            UpdateManifest? resource = null;
            if (scope is UpdateScope.Program or UpdateScope.All)
            {
                _programRelease = releases
                    .Where(item => GitHubReleaseClient.IsProgramTag(item.Tag))
                    .OrderByDescending(item => ParseReleaseVersion(item.Tag))
                    .FirstOrDefault();
                (program, _programManifestBytes, _programSignature) =
                    await ReadManifestAsync(_programRelease, "program-manifest", cancellationToken)
                        .ConfigureAwait(false);
            }
            if (scope is UpdateScope.Resource or UpdateScope.All)
            {
                _resourceRelease = releases
                    .Where(item => GitHubReleaseClient.IsResourceTag(item.Tag))
                    .OrderByDescending(item => ParseResourceTagVersion(item.Tag))
                    .FirstOrDefault();
                (resource, _resourceManifestBytes, _resourceSignature) =
                    await ReadManifestAsync(_resourceRelease, "resource-manifest", cancellationToken)
                        .ConfigureAwait(false);
            }
            return new UpdateCheckResult(program, resource);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            return new UpdateCheckResult(null, null, exception.Message);
        }
    }

    public async Task<StagedUpdate> DownloadAsync(
        UpdatePlan plan,
        UpdateScope scope,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var release = scope == UpdateScope.Resource ? _resourceRelease : _programRelease;
        if (release is null)
            throw new InvalidOperationException("Run update check before downloading.");
        var asset = release.Assets.FirstOrDefault(item => item.Name.Equals(
            plan.Asset.AssetName, StringComparison.Ordinal));
        if (asset is null)
            throw new FileNotFoundException("Selected update asset was not found in the Release.");
        var rawManifest = scope == UpdateScope.Resource ? _resourceManifestBytes : _programManifestBytes;
        var signature = scope == UpdateScope.Resource ? _resourceSignature : _programSignature;
        if (rawManifest is null || signature is null)
            throw new InvalidOperationException("The signed manifest is not staged.");
        return await _stager.StageAsync(
                plan,
                asset,
                scope,
                rawManifest,
                signature,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public UpdatePlan? SelectProgram(UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.Kind.Equals("program", StringComparison.Ordinal)
            || !ManifestVerifier.IsCanonicalManifest(manifest, manifest.ReleaseTag))
            return null;
        RecoverFailedProgramState();
        var state = _state.Load();
        var trusted = TryLoadTrustedProgramBaseline(
            state,
            out var currentTreeHash,
            out var currentManifestHash);
        return UpdateSelector.Select(
            manifest,
            UpdateVersionInfo.CurrentVersion,
            trusted ? currentTreeHash : null,
            trusted ? currentManifestHash : null,
            currentManifestSigned: trusted,
            forceFull: state.ForceFull || !trusted);
    }

    public UpdatePlan? SelectResource(UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.Kind.Equals("resource", StringComparison.Ordinal)
            || !ManifestVerifier.IsCanonicalManifest(manifest, manifest.ReleaseTag)
            || !IsResourceCompatibleWithCurrentApp(manifest))
            return null;
        if (!TryParseResourceVersion(manifest.Version, out var target)
            || (!_resources.RequiresFullResource
                && TryParseResourceVersion(_resources.Active.Version, out var current)
                && target.CompareTo(current) <= 0))
            return null;
        var asset = manifest.Assets.FirstOrDefault(item =>
            item.Type.Equals("full", StringComparison.OrdinalIgnoreCase));
        var state = _state.Load();
        if (asset is not null && !_resources.RequiresFullResource
            && TryLoadTrustedResourceBaseline(state, out var sourceManifestHash))
        {
            asset = manifest.Assets.FirstOrDefault(item =>
                item.Type.Equals("delta", StringComparison.OrdinalIgnoreCase)
                && item.FromVersion is not null
                && item.FromVersion.Equals(_resources.Active.Version, StringComparison.Ordinal)
                && item.SourceTreeSha256.Equals(
                    _resources.Active.BaseTreeSha256, StringComparison.OrdinalIgnoreCase)
                && item.SourceManifestSha256.Equals(sourceManifestHash, StringComparison.OrdinalIgnoreCase))
                ?? asset;
        }
        return asset is null ? null : new UpdatePlan(manifest, asset, manifest.ReleaseTag);
    }

    public async Task ApplyResourceWhenIdleAsync(
        StagedUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Scope != UpdateScope.Resource)
            throw new ArgumentException("The staged update is not a resource update.", nameof(update));
        await _activities.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
        using var lease = _activities.Acquire(ActivityKind.ResourceReload);
        var candidate = await _resources.PrepareAsync(
                update.Manifest.Version,
                update.PayloadRoot,
                update.Asset.Type.Equals("delta", StringComparison.OrdinalIgnoreCase),
                update.Asset.Deletes,
                update.Asset.TargetTreeSha256,
                cancellationToken)
            .ConfigureAwait(false);
        var old = _resources.Active;
        var oldDirectory = _resources.ActiveCompositeBaseDirectory;
        try
        {
            await _database.LoadAsync(
                    Path.Combine(candidate.CompositeDirectory, "resource"),
                    cancellationToken)
                .ConfigureAwait(false);
            await _uma.ReloadResourceAsync(candidate.CompositeDirectory, cancellationToken)
                .ConfigureAwait(false);
            await _resources.CommitAsync(candidate, cancellationToken).ConfigureAwait(false);
            var resourceManifestPath = Path.Combine(
                _appDataRoot, "resources", "current-manifest.json");
            var resourceSignaturePath = Path.Combine(
                _appDataRoot, "resources", "current-manifest.sig");
            Directory.CreateDirectory(Path.GetDirectoryName(resourceManifestPath)!);
            File.Copy(update.ManifestPath, resourceManifestPath, overwrite: true);
            File.Copy(update.SignaturePath, resourceSignaturePath, overwrite: true);
            _state.Update(resourceState =>
            {
                resourceState.OperationId = update.OperationId;
                resourceState.Scope = "resource";
                resourceState.Stage = "succeeded";
                resourceState.TargetVersion = update.Manifest.Version;
                resourceState.ResourceManifestSha256 = ManifestVerifier.Sha256File(resourceManifestPath);
                resourceState.ResourceManifestPath = resourceManifestPath;
                resourceState.ResourceSignaturePath = resourceSignaturePath;
            });
        }
        catch
        {
            await _resources.RollbackAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Directory.Exists(oldDirectory))
                {
                    await _database.LoadAsync(Path.Combine(oldDirectory, "resource"), cancellationToken)
                        .ConfigureAwait(false);
                    await _uma.ReloadResourceAsync(oldDirectory, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                // The old pointer remains authoritative; startup will repair the snapshot.
            }
            _state.Update(failedResourceState =>
            {
                failedResourceState.OperationId = update.OperationId;
                failedResourceState.Scope = "resource";
                failedResourceState.Stage = "failed-rollback";
                failedResourceState.TargetVersion = old.Version;
                failedResourceState.Error = "Resource reload failed.";
            });
            throw;
        }
    }

    public async Task RequestProgramRestartAsync(
        StagedUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Scope != UpdateScope.Program)
            throw new ArgumentException("The staged update is not a program update.", nameof(update));
        await _activities.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
        using var lease = _activities.Acquire(ActivityKind.Shutdown);

        // Quiesce the application before launching the helper. In particular,
        // unload the native bridge while this process still owns its lifetime;
        // OnExit remains an idempotent safety net for normal window closes.
        await _uma.DisposeAsync().ConfigureAwait(false);

        var operationRoot = Path.Combine(_appDataRoot, "updates", update.OperationId);
        var backupRoot = Path.Combine(operationRoot, "backup");
        var statusPath = Path.Combine(operationRoot, "status.txt");
        var planPath = Path.Combine(operationRoot, "plan.json");
        Directory.CreateDirectory(operationRoot);
        // Keep the exact signed bytes beside the helper plan. This prevents a
        // mutable check-cache path from being substituted after staging.
        var manifestPath = Path.Combine(operationRoot, "manifest.json");
        var signaturePath = Path.Combine(operationRoot, "manifest.sig");
        // PackageStager already writes these files into operationRoot. When
        // the user clicks "Install and restart", copying them again to the
        // same path makes Windows report that manifest.json is in use. Reuse
        // the staged files when the paths are identical; this also keeps the
        // signed bytes unchanged until the native updater takes over.
        CopyIfDifferent(update.ManifestPath, manifestPath);
        CopyIfDifferent(update.SignaturePath, signaturePath);
        string? sourceManifestPath = null;
        string? sourceSignaturePath = null;
        if (update.Asset.Type.Equals("delta", StringComparison.OrdinalIgnoreCase))
        {
            var current = _state.Load();
            if (string.IsNullOrWhiteSpace(current.SignedManifestPath)
                || string.IsNullOrWhiteSpace(current.SignedSignaturePath)
                || !File.Exists(current.SignedManifestPath)
                || !File.Exists(current.SignedSignaturePath))
                throw new InvalidOperationException("A program delta requires a signed current manifest.");
            sourceManifestPath = Path.Combine(operationRoot, "source-manifest.json");
            sourceSignaturePath = Path.Combine(operationRoot, "source-manifest.sig");
            File.Copy(current.SignedManifestPath, sourceManifestPath, overwrite: true);
            File.Copy(current.SignedSignaturePath, sourceSignaturePath, overwrite: true);
        }
        var installRoot = Path.GetFullPath(AppContext.BaseDirectory);
        var updaterSource = Path.Combine(installRoot, "UmamusumeAss.Updater.exe");
        if (!File.Exists(updaterSource))
            throw new FileNotFoundException("The installed updater executable is missing.", updaterSource);
        var updaterCopy = Path.Combine(operationRoot, "UmamusumeAss.Updater.exe");
        File.Copy(updaterSource, updaterCopy, overwrite: true);

        var plan = new
        {
            operationId = update.OperationId,
            manifestPath,
            signaturePath,
            currentManifestPath = sourceManifestPath,
            currentSignaturePath = sourceSignaturePath,
            currentTreeSha256 = update.Asset.SourceTreeSha256,
            manifest = new { selectedAsset = update.Asset },
            replace = update.Asset.Files,
            deletes = update.Asset.Deletes,
        };
        await File.WriteAllTextAsync(
            planPath,
            JsonSerializer.Serialize(plan),
            cancellationToken).ConfigureAwait(false);
        _state.Update(programState =>
        {
            programState.OperationId = update.OperationId;
            programState.Scope = "program";
            programState.Stage = "launching-updater";
            programState.TargetVersion = update.Manifest.Version;
            programState.BackupPath = backupRoot;
        });

        var startInfo = new ProcessStartInfo
        {
            FileName = updaterCopy,
            WorkingDirectory = operationRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--install-root");
        startInfo.ArgumentList.Add(installRoot);
        startInfo.ArgumentList.Add("--staging-root");
        startInfo.ArgumentList.Add(update.PayloadRoot);
        startInfo.ArgumentList.Add("--backup-root");
        startInfo.ArgumentList.Add(backupRoot);
        startInfo.ArgumentList.Add("--plan");
        startInfo.ArgumentList.Add(planPath);
        startInfo.ArgumentList.Add("--status");
        startInfo.ArgumentList.Add(statusPath);
        startInfo.ArgumentList.Add("--operation-id");
        startInfo.ArgumentList.Add(update.OperationId);
        Process.Start(startInfo);
        Application.Current?.Shutdown();
    }

    internal static void CopyIfDifferent(string sourcePath, string destinationPath)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            return;
        File.Copy(source, destination, overwrite: true);
    }

    private async Task<(UpdateManifest?, byte[]?, byte[]?)> ReadManifestAsync(
        GitHubRelease? release,
        string prefix,
        CancellationToken cancellationToken)
    {
        if (release is null) return (null, null, null);
        var manifestAsset = release.Assets.FirstOrDefault(item => item.Name.StartsWith(prefix, StringComparison.Ordinal)
            && item.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        var signatureAsset = release.Assets.FirstOrDefault(item => item.Name.StartsWith(prefix, StringComparison.Ordinal)
            && item.Name.EndsWith(".sig", StringComparison.OrdinalIgnoreCase));
        if (manifestAsset is null || signatureAsset is null)
            throw new InvalidDataException($"Release {release.Tag} does not contain a signed manifest.");
        var directory = Path.Combine(_appDataRoot, "updates", "checks");
        Directory.CreateDirectory(directory);
        var manifestPath = Path.Combine(directory, manifestAsset.Name);
        var signaturePath = Path.Combine(directory, signatureAsset.Name);
        await _github.DownloadAssetFileAsync(manifestAsset, manifestPath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await _github.DownloadAssetFileAsync(signatureAsset, signaturePath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var signature = ManifestVerifier.DecodeSignature(
            await File.ReadAllTextAsync(signaturePath, cancellationToken).ConfigureAwait(false));
        var manifest = ManifestVerifier.Deserialize(bytes);
        if (!ManifestVerifier.IsCanonicalManifest(manifest, release.Tag)
            || !_verifier.Verify(bytes, signature))
            throw new InvalidDataException($"Release manifest {manifestAsset.Name} failed validation.");
        return (manifest, bytes, signature);
    }

    private void RecoverFailedProgramState()
    {
        var state = _state.Load();
        if (!state.Scope.Equals("program", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(state.BackupPath))
            return;
        var statusPath = Path.Combine(Directory.GetParent(state.BackupPath)!.FullName, "status.txt");
        if (!File.Exists(statusPath)) return;
        var status = File.ReadAllText(statusPath);
        if (!status.StartsWith("failed", StringComparison.OrdinalIgnoreCase)
            && !status.StartsWith("health-rollback", StringComparison.OrdinalIgnoreCase))
            return;
        if (state.ForceFull) return;
        state.ForceFull = true;
        state.Stage = "retry-full";
        _state.Save(state);
    }

    private bool TryLoadTrustedProgramBaseline(
        UpdateState state,
        out string treeHash,
        out string manifestHash)
    {
        treeHash = string.Empty;
        manifestHash = string.Empty;
        if (!state.CurrentManifestSigned
            || string.IsNullOrWhiteSpace(state.SignedManifestPath)
            || string.IsNullOrWhiteSpace(state.SignedSignaturePath)
            || !File.Exists(state.SignedManifestPath)
            || !File.Exists(state.SignedSignaturePath))
            return false;
        try
        {
            var bytes = File.ReadAllBytes(state.SignedManifestPath);
            manifestHash = ManifestVerifier.Sha256Bytes(bytes);
            if (!manifestHash.Equals(state.ManifestSha256, StringComparison.OrdinalIgnoreCase))
                return false;
            var signature = ManifestVerifier.DecodeSignature(
                File.ReadAllText(state.SignedSignaturePath));
            var manifest = ManifestVerifier.Deserialize(bytes);
            if (!_verifier.Verify(bytes, signature)
                || !ManifestVerifier.IsCanonicalManifest(manifest, manifest.ReleaseTag)
                || !manifest.Version.Equals(UpdateVersionInfo.CurrentVersion, StringComparison.Ordinal))
                return false;
            var full = manifest.Assets.FirstOrDefault(asset =>
                asset.Type.Equals("full", StringComparison.OrdinalIgnoreCase));
            if (full is null
                || !UpdateSelector.VerifyManagedTree(
                    AppContext.BaseDirectory, full, full.TargetTreeSha256))
                return false;
            treeHash = full.TargetTreeSha256;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryLoadTrustedResourceBaseline(
        UpdateState state,
        out string manifestHash)
    {
        manifestHash = string.Empty;
        if (string.IsNullOrWhiteSpace(state.ResourceManifestPath)
            || string.IsNullOrWhiteSpace(state.ResourceSignaturePath)
            || !File.Exists(state.ResourceManifestPath)
            || !File.Exists(state.ResourceSignaturePath))
            return false;
        try
        {
            var bytes = File.ReadAllBytes(state.ResourceManifestPath);
            manifestHash = ManifestVerifier.Sha256Bytes(bytes);
            if (!manifestHash.Equals(state.ResourceManifestSha256, StringComparison.OrdinalIgnoreCase))
                return false;
            var manifest = ManifestVerifier.Deserialize(bytes);
            var signature = ManifestVerifier.DecodeSignature(
                File.ReadAllText(state.ResourceSignaturePath));
            var full = manifest.Assets.FirstOrDefault(asset =>
                asset.Type.Equals("full", StringComparison.OrdinalIgnoreCase));
            return _verifier.Verify(bytes, signature)
                && ManifestVerifier.IsCanonicalManifest(manifest, manifest.ReleaseTag)
                && full is not null
                && full.TargetTreeSha256.Equals(
                    _resources.Active.BaseTreeSha256, StringComparison.OrdinalIgnoreCase)
                && UpdateSelector.VerifyManagedTree(
                    _resources.ActiveBaseDirectory, full, full.TargetTreeSha256);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsResourceCompatibleWithCurrentApp(UpdateManifest manifest)
    {
        if (!manifest.ResourceSchema.Equals("1", StringComparison.Ordinal)
            || !manifest.SupportedResourceSchema.Equals("1", StringComparison.Ordinal)
            || !SemVersion.TryParse(UpdateVersionInfo.CurrentVersion, out var appVersion))
            return false;
        if (!string.IsNullOrWhiteSpace(manifest.MinAppVersion)
            && (!SemVersion.TryParse(manifest.MinAppVersion, out var min)
                || appVersion < min))
            return false;
        if (!string.IsNullOrWhiteSpace(manifest.MaxAppVersion)
            && (!SemVersion.TryParse(manifest.MaxAppVersion, out var max)
                || appVersion > max))
            return false;
        return true;
    }

    private static SemVersion ParseReleaseVersion(string tag)
    {
        var value = tag.StartsWith('v') ? tag[1..] : tag;
        return SemVersion.TryParse(value, out var version) ? version : default;
    }

    private static bool TryParseResourceVersion(string? value, out ResourceVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('.');
        if (parts.Length != 4 || !parts.All(part => int.TryParse(
                part, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _))) return false;
        version = new ResourceVersion(
            int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    private static ResourceVersion ParseResourceTagVersion(string tag)
    {
        return TryParseResourceVersion(
                tag.StartsWith("resource-v", StringComparison.Ordinal)
                    ? tag[10..] : null,
                out var version)
            ? version : default;
    }

    private readonly record struct ResourceVersion(int Year, int Month, int Day, int Revision)
        : IComparable<ResourceVersion>
    {
        public int CompareTo(ResourceVersion other) =>
            Year != other.Year ? Year.CompareTo(other.Year)
            : Month != other.Month ? Month.CompareTo(other.Month)
            : Day != other.Day ? Day.CompareTo(other.Day)
            : Revision.CompareTo(other.Revision);
    }
}

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckAsync(UpdateScope scope, CancellationToken cancellationToken = default);
    UpdatePlan? SelectProgram(UpdateManifest manifest);
    UpdatePlan? SelectResource(UpdateManifest manifest);
    Task<StagedUpdate> DownloadAsync(UpdatePlan plan, UpdateScope scope, IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default);
    Task ApplyResourceWhenIdleAsync(StagedUpdate update, CancellationToken cancellationToken = default);
    Task RequestProgramRestartAsync(StagedUpdate update, CancellationToken cancellationToken = default);
}
