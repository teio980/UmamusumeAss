using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;
using ActivityKind = UmamusumeWpfGui.Models.ActivityKind;

namespace UmamusumeWpfGui.Services.Update;

public sealed class UpdateCoordinator : IUpdateService
{
    private static readonly JsonSerializerOptions UpdaterPlanJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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
        var staged = await _stager.StageAsync(
                plan,
                asset,
                scope,
                rawManifest,
                signature,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        if (scope == UpdateScope.Program)
        {
            _state.Update(state =>
            {
                state.StagedProgramOperationId = staged.OperationId;
                state.StagedProgramVersion = staged.Manifest.Version;
                state.StagedProgramAssetName = staged.Asset.AssetName;
            });
        }
        return staged;
    }

    public StagedUpdate? RestoreStagedProgram()
    {
        try
        {
            var state = _state.Load();
            if (string.IsNullOrWhiteSpace(state.StagedProgramOperationId)
                || string.IsNullOrWhiteSpace(state.StagedProgramVersion)
                || string.IsNullOrWhiteSpace(state.StagedProgramAssetName))
                return null;

            var staged = _stager.TryRestore(
                state.StagedProgramOperationId,
                UpdateScope.Program,
                state.StagedProgramAssetName);
            if (staged is not null
                && staged.Manifest.Version.Equals(
                    state.StagedProgramVersion, StringComparison.Ordinal)
                && SemVersion.TryParse(staged.Manifest.Version, out var stagedVersion)
                && SemVersion.TryParse(UpdateVersionInfo.CurrentVersion, out var currentVersion)
                && stagedVersion > currentVersion)
                return staged;

            var staleOperationId = state.StagedProgramOperationId;
            ClearStagedProgram(staleOperationId);
            TryDeleteStagedOperation(staleOperationId);
            return null;
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

    public void DiscardStagedProgram()
    {
        var state = _state.Load();
        var operationId = state.StagedProgramOperationId;
        if (string.IsNullOrWhiteSpace(operationId))
            return;

        ClearStagedProgram(operationId);
        TryDeleteStagedOperation(operationId);
    }

    private void ClearStagedProgram(string operationId)
    {
        _state.Update(state =>
        {
            if (!string.Equals(
                    state.StagedProgramOperationId,
                    operationId,
                    StringComparison.Ordinal))
                return;
            state.StagedProgramOperationId = null;
            state.StagedProgramVersion = null;
            state.StagedProgramAssetName = null;
        });
    }

    private void TryDeleteStagedOperation(string operationId)
    {
        if (!Guid.TryParseExact(operationId, "N", out _))
            return;
        TryDeleteOperationRoot(Path.Combine(_appDataRoot, "updates", operationId));
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
            TryDeleteOperationRoot(Path.GetDirectoryName(update.ManifestPath)!);
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
        var installedUpdater = Path.Combine(installRoot, "UmamusumeAss.Updater.exe");
        var stagedUpdater = Path.Combine(update.PayloadRoot, "UmamusumeAss.Updater.exe");
        // A release may repair the helper, but a previously published target
        // may also contain a stale helper. Try the installed and staged copies
        // independently; only a helper that verifies the plan and reports
        // ready is allowed to take ownership of the update.
        var updaterSources = new[] { installedUpdater, stagedUpdater }
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (updaterSources.Length == 0)
            throw new FileNotFoundException("No update helper executable is available.");
        var updaterCopy = Path.Combine(operationRoot, "UmamusumeAss.Updater.exe");

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
            SerializeUpdaterPlan(plan),
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

        var application = Application.Current
            ?? throw new InvalidOperationException("The WPF application is unavailable.");

        Process? updater = null;
        try
        {
            // Start the helper before tearing down the native runtime. Each
            // candidate validates the signed plan and writes "ready" before
            // waiting for this process. A rejected candidate is stopped while
            // the current app is still fully usable, then the next is tried.
            for (var index = 0; index < updaterSources.Length; index++)
            {
                try
                {
                    File.Copy(updaterSources[index], updaterCopy, overwrite: true);
                    File.WriteAllText(statusPath, "starting\n");
                    updater = Process.Start(startInfo)
                        ?? throw new InvalidOperationException("The update helper could not be started.");
                    await WaitForUpdaterReadyAsync(updater, statusPath, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException
                    && index < updaterSources.Length - 1)
                {
                    StopUpdater(updater);
                    updater?.Dispose();
                    updater = null;
                    File.WriteAllText(statusPath, "retrying-helper\n" + exception.Message);
                }
            }

            if (updater is null)
                throw new InvalidOperationException("No update helper accepted the signed update plan.");

            // The helper is now safely waiting on our PID. Release native
            // resources while the current process still owns them, then ask
            // WPF to shut down on its owning dispatcher thread.
            await _uma.DisposeAsync().ConfigureAwait(false);
            await RequestApplicationShutdownAsync(application).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // If the helper itself reported a failure, leave its error dialog
            // alive. Otherwise stop a helper that would otherwise wait forever
            // for a parent which is intentionally staying open.
            if (!HasUpdaterFailed(statusPath))
            {
                StopUpdater(updater);
                TryWriteHandoffFailure(statusPath, exception.Message);
            }
            _state.Update(failedState =>
            {
                failedState.Stage = "failed-handoff";
                failedState.Error = exception.Message;
            });
            throw;
        }
        finally
        {
            updater?.Dispose();
        }
    }

    internal static string SerializeUpdaterPlan(object plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return JsonSerializer.Serialize(plan, UpdaterPlanJsonOptions);
    }

    private static void TryDeleteOperationRoot(string operationRoot)
    {
        try
        {
            if (Directory.Exists(operationRoot))
                Directory.Delete(operationRoot, recursive: true);
        }
        catch (IOException)
        {
            // Update data is cache; a later startup can retry cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Update data is cache; a later startup can retry cleanup.
        }
    }

    private static void TryWriteHandoffFailure(string statusPath, string reason)
    {
        try
        {
            File.WriteAllText(statusPath, "failed\n" + reason);
        }
        catch (IOException)
        {
            // The status file is diagnostic; the exception is still surfaced
            // to the settings view and the persisted update state above.
        }
        catch (UnauthorizedAccessException)
        {
            // The status file is diagnostic; the exception is still surfaced
            // to the settings view and the persisted update state above.
        }
    }

    private static async Task WaitForUpdaterReadyAsync(
        Process updater,
        string statusPath,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 30;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(statusPath))
            {
                try
                {
                    var status = await File.ReadAllTextAsync(statusPath, cancellationToken)
                        .ConfigureAwait(false);
                    if (status.StartsWith("ready", StringComparison.OrdinalIgnoreCase))
                        return;
                    if (status.StartsWith("failed", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "The update helper rejected the update plan. See the updater error dialog.");
                }
                catch (IOException)
                {
                    // The helper may still be closing the status file. Retry
                    // rather than treating this short hand-off race as a
                    // failed update.
                }
                catch (UnauthorizedAccessException)
                {
                    // Retry transient antivirus/indexer sharing interference.
                }
            }

            if (updater.HasExited)
                throw new InvalidOperationException("The update helper exited before taking ownership of the update.");
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("The update helper did not become ready.");
    }

    private static bool HasUpdaterFailed(string statusPath)
    {
        try
        {
            return File.Exists(statusPath)
                && File.ReadAllText(statusPath).StartsWith(
                    "failed", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void StopUpdater(Process? updater)
    {
        if (updater is null)
            return;
        try
        {
            if (!updater.HasExited)
            {
                updater.Kill();
                updater.WaitForExit(5_000);
            }
        }
        catch (InvalidOperationException)
        {
            // The helper exited between the checks.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The helper may already have terminated or been blocked by policy.
        }
    }

    private static Task RequestApplicationShutdownAsync(Application application)
    {
        if (application.Dispatcher.CheckAccess())
        {
            application.Shutdown();
            return Task.CompletedTask;
        }

        // Shutdown is a DispatcherObject operation. InvokeAsync ensures the
        // request is actually executed on the WPF UI thread before this method
        // completes; BeginInvoke alone allowed the caller to race the exit path.
        return application.Dispatcher
            .InvokeAsync(application.Shutdown, DispatcherPriority.Send)
            .Task;
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
    StagedUpdate? RestoreStagedProgram();
    void DiscardStagedProgram();
    Task ApplyResourceWhenIdleAsync(StagedUpdate update, CancellationToken cancellationToken = default);
    Task RequestProgramRestartAsync(StagedUpdate update, CancellationToken cancellationToken = default);
}
