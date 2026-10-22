using System.Net.Http;
using System.Text.Json;
using System.Runtime.InteropServices;
using Umamusume.AppCore.Platform;
using Umamusume.UpdateCore;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Update;

namespace Umamusume.Avalonia.Services;

public sealed record MacUpdateCheck(
    string CurrentProgramVersion,
    string CurrentResourceVersion,
    FullUpdateDescriptor? Update = null,
    string? Error = null,
    UpdateManifest? ProgramManifest = null,
    GitHubRelease? ProgramRelease = null,
    UpdateManifest? ResourceManifest = null,
    GitHubRelease? ResourceRelease = null)
{
    public bool HasProgramUpdate => Update is not null || ProgramManifest is not null;
    public bool HasResourceUpdate => ResourceManifest is not null && false;
    public UpdateManifest? ProgramManifest { get; init; } = ProgramManifest ?? (Update is null ? null : new UpdateManifest
    {
        Version = Update.Version,
        ReleaseTag = Update.Tag,
        Assets =
        [
            new UpdateAsset
            {
                Type = "full",
                AssetName = Update.Asset.Name,
                Size = Update.Asset.Size,
                Sha256 = Update.ExpectedSha256 ?? string.Empty,
                TargetTreeSha256 = Update.ExpectedSha256 ?? string.Empty,
            }
        ]
    });
    public GitHubRelease? ProgramRelease { get; init; } = ProgramRelease ?? (Update is null ? null : new GitHubRelease(Update.Tag, [Update.Asset], string.Empty));
    public UpdateManifest? ResourceManifest { get; init; } = ResourceManifest;
    public GitHubRelease? ResourceRelease { get; init; } = ResourceRelease;
}

/// <summary>Full-only update discovery and staging for macOS arm64.</summary>
public sealed class MacUpdateService
{
    private readonly GitHubReleaseClient _releases;
    private readonly UpdateChecker _checker;
    private readonly FullPackageStager _stager;
    private readonly string _userDataRoot;
    private readonly string _updatesRoot;
    private sealed record StagedReference(string OperationId, string Scope, string AssetName);

    public MacUpdateService(HttpClient? httpClient = null, string? userDataRoot = null)
        : this(httpClient, userDataRoot, null)
    {
    }

    internal MacUpdateService(HttpClient? httpClient, string? userDataRoot, ManifestVerifier? verifier)
    {
        _userDataRoot = Path.GetFullPath(userDataRoot ?? PlatformPaths.GetDefaultUserDataDirectory());
        _updatesRoot = Path.Combine(_userDataRoot, "updates");
        _releases = new GitHubReleaseClient(httpClient, Path.Combine(_updatesRoot, "release-cache"));
        _checker = new UpdateChecker(_releases);
        _stager = new FullPackageStager(_releases, _updatesRoot);
    }

    internal static string ProgramManifestName(string version) => $"osx-arm64-program-manifest-{version}.json";
    internal static string ResourceManifestName(string revision) => $"osx-arm64-resource-manifest-{revision}.json";

    public Task<MacUpdateCheck> CheckAsync(
        string currentProgramVersion,
        string currentResourceVersion,
        CancellationToken cancellationToken = default) =>
        CheckAsync(currentProgramVersion, null, currentResourceVersion, cancellationToken);

    public async Task<MacUpdateCheck> CheckAsync(
        string currentProgramVersion,
        string? skippedVersion,
        string? currentResourceVersion,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            throw new PlatformNotSupportedException("Automatic updates are supported only on macOS arm64.");

        var result = await _checker.CheckForUpdateAsync(
            currentProgramVersion,
            skippedVersion,
            "osx-arm64",
            cancellationToken).ConfigureAwait(false);

        if (result.Error is not null)
        {
            return new MacUpdateCheck(currentProgramVersion, currentResourceVersion ?? string.Empty, result.Update, result.Error);
        }

        return new MacUpdateCheck(currentProgramVersion, currentResourceVersion ?? string.Empty, result.Update);
    }

    public async Task<StagedFullUpdate> DownloadProgramFullAsync(
        MacUpdateCheck check,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (check.Update is null)
            throw new InvalidOperationException("No program update is available.");
        return await _stager.StageAsync(check.Update, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StagedUpdate> DownloadProgramAsync(
        MacUpdateCheck check,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var staged = await DownloadProgramFullAsync(check, progress, cancellationToken).ConfigureAwait(false);
        return ToStagedUpdate(staged);
    }

    public Task<StagedUpdate> DownloadResourceAsync(
        MacUpdateCheck check,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Standalone resource update is removed.
        throw new NotSupportedException("Standalone resource update is removed. Resources update together with program.");
    }

    public StagedUpdate? RestoreStaged(UpdateScope scope, string currentProgramVersion, string currentResourceVersion)
    {
        if (scope != UpdateScope.Program) return null;
        var staged = _stager.TryRestoreStaged();
        if (staged is not null
            && SemVersion.TryParse(staged.Version, out var stagedVersion)
            && SemVersion.TryParse(currentProgramVersion, out var currentVersion))
        {
            if (stagedVersion > currentVersion)
            {
                return ToStagedUpdate(staged);
            }
            else
            {
                // Obsolete / already installed: delete staging directory to free up disk space
                try
                {
                    string opDir = Path.GetDirectoryName(staged.InventoryPath)!;
                    if (Directory.Exists(opDir)) Directory.Delete(opDir, recursive: true);
                }
                catch { }
            }
        }
        return null;
    }

    public void RememberStaged(StagedUpdate staged)
    {
        if (staged.Scope != UpdateScope.Program)
            return;
        Directory.CreateDirectory(_updatesRoot);
        string path = GetReferencePath(staged.Scope);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(new StagedReference(
            staged.OperationId, staged.Scope.ToString(), staged.Asset.AssetName)));
        File.Move(temporary, path, overwrite: true);
    }

    public void ForgetStaged(UpdateScope scope)
    {
        string path = GetReferencePath(scope);
        if (File.Exists(path))
        {
            try { File.Delete(path); }
            catch { }
        }
    }

    public async Task ClearAllUpdateCacheAsync(CancellationToken cancellationToken = default)
    {
        await _stager.ClearUpdateCachesAsync(cancellationToken).ConfigureAwait(false);
        ForgetStaged(UpdateScope.Program);
        ForgetStaged(UpdateScope.Resource);
    }

    public static StagedUpdate ToStagedUpdate(StagedFullUpdate staged)
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
                    TargetTreeSha256 = staged.Inventory.PackageSha256,
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

    private string GetReferencePath(UpdateScope scope) => Path.Combine(_updatesRoot,
        scope == UpdateScope.Program ? "staged-program.json" : "staged-resource.json");
}
