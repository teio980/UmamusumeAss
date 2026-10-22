using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using Umamusume.CoreBridge;
using Umamusume.UpdateCore;
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
    private readonly UpdateChecker _checker;
    private readonly FullPackageStager _stager;
    private readonly IActivityRegistry _activities;
    private readonly IResourceStore _resources;
    private readonly IUmaService _uma;
    private readonly IUmaDatabaseService _database;
    private readonly UpdateStateStore _state;
    private readonly string _appDataRoot;

    private FullUpdateDescriptor? _pendingDescriptor;

    public UpdateCoordinator(
        GitHubReleaseClient github,
        ManifestVerifier verifier,
        PackageStager oldStager,
        IActivityRegistry activities,
        IResourceStore resources,
        IUmaService uma,
        IUmaDatabaseService database,
        UpdateStateStore state)
    {
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _activities = activities ?? throw new ArgumentNullException(nameof(activities));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _uma = uma ?? throw new ArgumentNullException(nameof(uma));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UmamusumeAss");
        _checker = new UpdateChecker(_github);
        _stager = new FullPackageStager(_github, Path.Combine(_appDataRoot, "updates"));
    }

    public async Task<UpdateCheckResult> CheckAsync(
        UpdateScope scope,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var currentVersion = UpdateVersionInfo.CurrentVersion;
            var skipped = _state.Load().SkippedProgramVersion;
            var check = await _checker.CheckForUpdateAsync(
                currentVersion,
                skipped,
                "windows-x64",
                cancellationToken).ConfigureAwait(false);

            if (check.Error is not null)
            {
                if (check.Update is not null)
                    _pendingDescriptor = check.Update;
                return new UpdateCheckResult(null, null, check.Error);
            }

            if (check.Update is not null)
            {
                _pendingDescriptor = check.Update;
                var manifest = new UpdateManifest
                {
                    Version = check.Update.Version,
                    ReleaseTag = check.Update.Tag,
                    Assets =
                    [
                        new UpdateAsset
                        {
                            Type = "full",
                            AssetName = check.Update.Asset.Name,
                            Size = check.Update.Asset.Size,
                            Sha256 = check.Update.ExpectedSha256 ?? string.Empty,
                            TargetTreeSha256 = check.Update.ExpectedSha256 ?? string.Empty,
                        }
                    ]
                };
                return new UpdateCheckResult(manifest, null);
            }

            _pendingDescriptor = null;
            return new UpdateCheckResult(null, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            return new UpdateCheckResult(null, null, exception.Message);
        }
    }

    public UpdatePlan? SelectProgram(UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Assets.Count == 0) return null;
        var fullAsset = manifest.Assets.FirstOrDefault(a => a.Type.Equals("full", StringComparison.OrdinalIgnoreCase))
            ?? manifest.Assets[0];
        return new UpdatePlan(manifest, fullAsset, manifest.ReleaseTag);
    }

    public UpdatePlan? SelectResource(UpdateManifest manifest)
    {
        // Standalone resource update is removed. Program and resources update together.
        return null;
    }

    public async Task<StagedUpdate> DownloadAsync(
        UpdatePlan plan,
        UpdateScope scope,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (_pendingDescriptor is null)
            throw new InvalidOperationException("Run update check before downloading.");

        var staged = await _stager.StageAsync(_pendingDescriptor, progress, cancellationToken)
            .ConfigureAwait(false);

        _state.Update(state =>
        {
            state.StagedProgramOperationId = staged.OperationId;
            state.StagedProgramVersion = staged.Version;
            state.StagedProgramAssetName = staged.Inventory.PackageName;
        });

        return new StagedUpdate(
            staged.OperationId,
            UpdateScope.Program,
            plan.Manifest,
            plan.Asset,
            staged.PackagePath,
            staged.PayloadRoot,
            staged.InventoryPath,
            staged.InventoryPath);
    }

    public StagedUpdate? RestoreStagedProgram()
    {
        try
        {
            var staged = _stager.TryRestoreStaged();
            if (staged is not null
                && SemVersion.TryParse(staged.Version, out var stagedVersion)
                && SemVersion.TryParse(UpdateVersionInfo.CurrentVersion, out var currentVersion)
                && stagedVersion > currentVersion)
            {
                var manifest = new UpdateManifest
                {
                    Version = staged.Version,
                    ReleaseTag = staged.Tag,
                    Assets =
                    [
                        new UpdateAsset
                        {
                            Type = "full",
                            AssetName = staged.Inventory.PackageName,
                            Size = staged.Inventory.PackageSize,
                            Sha256 = staged.Inventory.PackageSha256,
                            TargetTreeSha256 = staged.Inventory.PackageSha256
                        }
                    ]
                };

                return new StagedUpdate(
                    staged.OperationId,
                    UpdateScope.Program,
                    manifest,
                    manifest.Assets[0],
                    staged.PackagePath,
                    staged.PayloadRoot,
                    staged.InventoryPath,
                    staged.InventoryPath);
            }

            return null;
        }
        catch
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

        _state.Update(s =>
        {
            s.StagedProgramOperationId = null;
            s.StagedProgramVersion = null;
            s.StagedProgramAssetName = null;
        });

        var opDir = Path.Combine(_appDataRoot, "updates", operationId);
        try
        {
            if (Directory.Exists(opDir))
                Directory.Delete(opDir, recursive: true);
        }
        catch { }
    }

    public async Task ClearAllUpdateCacheAsync(CancellationToken cancellationToken = default)
    {
        await _stager.ClearUpdateCachesAsync(cancellationToken).ConfigureAwait(false);
        _state.Update(state =>
        {
            state.StagedProgramOperationId = null;
            state.StagedProgramVersion = null;
            state.StagedProgramAssetName = null;
            state.BackupPath = null;
        });
    }

    internal static void DeleteCacheDirectory(
        string directory,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory))
                return;
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception) when (attempt < 4)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    public Task ApplyResourceWhenIdleAsync(
        StagedUpdate update,
        CancellationToken cancellationToken = default)
    {
        // Standalone resource update is removed.
        return Task.CompletedTask;
    }

    public async Task RequestProgramRestartAsync(
        StagedUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _activities.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
        using var lease = _activities.Acquire(ActivityKind.Shutdown);

        var operationRoot = Path.Combine(_appDataRoot, "updates", update.OperationId);
        var backupRoot = Path.Combine(operationRoot, "backup");
        var statusPath = Path.Combine(operationRoot, "status.txt");
        var planPath = Path.Combine(operationRoot, "plan.json");
        Directory.CreateDirectory(operationRoot);

        var installRoot = Path.GetFullPath(AppContext.BaseDirectory);
        var stagedUpdater = Path.Combine(update.PayloadRoot, "UmamusumeAss.Updater.exe");
        var installedUpdater = Path.Combine(installRoot, "UmamusumeAss.Updater.exe");
        var updaterSource = File.Exists(stagedUpdater) ? stagedUpdater : installedUpdater;

        if (!File.Exists(updaterSource))
            throw new FileNotFoundException("No update helper executable is available.");

        var updaterCopy = Path.Combine(_appDataRoot, "updater", "UmamusumeAss.Updater.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(updaterCopy)!);

        var plan = new
        {
            schemaVersion = 2,
            operationId = update.OperationId,
            version = update.Manifest.Version,
            installRoot,
            stagingRoot = update.PayloadRoot,
            backupRoot,
            localInventoryPath = update.ManifestPath,
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
            programState.ForceFull = true;
            programState.Error = null;
        });

        var startInfo = new ProcessStartInfo
        {
            FileName = updaterCopy,
            WorkingDirectory = installRoot,
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
            File.Copy(updaterSource, updaterCopy, overwrite: true);
            File.WriteAllText(statusPath, "starting\n");
            updater = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The update helper could not be started.");
            await _uma.DisposeAsync().ConfigureAwait(false);
            await RequestApplicationShutdownAsync(application).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            StopUpdater(updater);
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
        catch { }
    }

    private static Task RequestApplicationShutdownAsync(Application application)
    {
        if (application.Dispatcher.CheckAccess())
        {
            application.Shutdown();
            return Task.CompletedTask;
        }

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
}

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckAsync(UpdateScope scope, CancellationToken cancellationToken = default);
    UpdatePlan? SelectProgram(UpdateManifest manifest);
    UpdatePlan? SelectResource(UpdateManifest manifest);
    Task<StagedUpdate> DownloadAsync(UpdatePlan plan, UpdateScope scope, IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default);
    StagedUpdate? RestoreStagedProgram();
    void DiscardStagedProgram();
    Task ClearAllUpdateCacheAsync(CancellationToken cancellationToken = default);
    Task ApplyResourceWhenIdleAsync(StagedUpdate update, CancellationToken cancellationToken = default);
    Task RequestProgramRestartAsync(StagedUpdate update, CancellationToken cancellationToken = default);
}
