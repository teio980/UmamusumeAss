using System.Diagnostics;
using System.IO;
using System.Windows;
using Stylet;
using StyletIoC;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;
using UmamusumeWpfGui.Services.Update;
using UmamusumeWpfGui.ViewModels;
using UmamusumeWpfGui.ViewModels.Tasks;
using ActivityKind = UmamusumeWpfGui.Models.ActivityKind;

namespace UmamusumeWpfGui;

public class Bootstrapper : Bootstrapper<RootViewModel>
{
    private Task<bool>? _startupInitialization;
    private IUmaService? _umaService;

    protected override void ConfigureIoC(IStyletIoCBuilder builder)
    {

        builder.Bind<IConnectionStateService>()
            .To<ConnectionStateService>()
            .InSingletonScope();

        builder.Bind<IUmaService>()
            .To<UmaService>()
            .InSingletonScope();

        builder.Bind<IResourceStore>()
            .To<ResourceStore>()
            .InSingletonScope();
        builder.Bind<IActivityRegistry>()
            .To<ActivityRegistry>()
            .InSingletonScope();
        builder.Bind<GitHubReleaseClient>().ToSelf().InSingletonScope();
        builder.Bind<ManifestVerifier>().ToSelf().InSingletonScope();
        builder.Bind<PackageStager>().ToSelf().InSingletonScope();
        builder.Bind<UpdateStateStore>().ToSelf().InSingletonScope();
        builder.Bind<IUpdateService>().To<UpdateCoordinator>().InSingletonScope();

        builder.Bind<IUmaDatabaseService>()
            .To<UmaDatabaseService>()
            .InSingletonScope();

        builder.Bind<IEventDispatcher>()
            .To<WpfEventDispatcher>()
            .InSingletonScope();

        builder.Bind<ISettingsService>()
            .To<JsonSettingsService>()
            .InSingletonScope();

        builder.Bind<ILocalizationService>()
            .To<LocalizationService>()
            .InSingletonScope();


        builder.Bind<IProcessEnumerator>()
            .To<ProcessEnumerator>();
        builder.Bind<IAdbRunner>()
            .To<AdbRunner>();
        builder.Bind<IAdbRuntime>()
            .To<AdbRuntime>();
        builder.Bind<IScreenTextRecognizer>()
            .To<WindowsOcrTextRecognizer>()
            .InSingletonScope();
        builder.Bind<IAdbTouchRuntime>()
            .To<AdbTouchRuntime>();
        builder.Bind<IVisualPipelineRuntime>()
            .To<AdbVisualPipelineRuntime>()
            .InSingletonScope();
        builder.Bind<HachimiJsonPipelineRunner>()
            .ToSelf()
            .InSingletonScope();
        builder.Bind<ShopTaskModule>()
            .ToSelf()
            .InSingletonScope();
        builder.Bind<DailyRaceRunnerSelector>()
            .ToSelf()
            .InSingletonScope();
        builder.Bind<IAdbConnectionSessionFactory>()
            .To<AdbConnectionSessionFactory>();
        builder.Bind<IGameLauncher>()
            .To<AdbGameLauncher>();
        builder.Bind<IStartGamePipeline>()
            .To<AdbStartGamePipeline>()
            .InSingletonScope();
        builder.Bind<StartGameTaskModule>()
            .ToSelf()
            .InSingletonScope();
        builder.Bind<TeamRaceTaskModule>()
            .ToSelf()
            .InSingletonScope();
        builder.Bind<DailyRaceTaskModule>()
            .ToSelf()
            .InSingletonScope();
        builder.Bind<ICareerTrainingPipeline>()
            .To<AdbNormalCareerTrainingPipeline>()
            .InSingletonScope();
        builder.Bind<CareerJsonActionExecutor>()
            .ToSelf()
            .InSingletonScope();
        builder.Bind<CareerEntryNavigator>()
            .ToSelf()
            .InSingletonScope();
        builder.Bind<IIndependentTrainingPipeline>()
            .To<AdbIndependentTrainingPipeline>()
            .InSingletonScope();
        builder.Bind<UraTraineeSelector>()
            .ToSelf()
            .InSingletonScope();
        builder.Bind<UraLegacySelector>()
            .ToSelf()
            .InSingletonScope();
        builder.Bind<CareerTrainingTaskModule>()
            .ToSelf()
            .InSingletonScope();
        builder.Bind<ITeamRacePipeline>()
            .To<AdbTeamRacePipeline>()
            .InSingletonScope();
        builder.Bind<IDailyRacePipeline>()
            .To<AdbDailyRacePipeline>()
            .InSingletonScope();
        builder.Bind<IMailCollectionPipeline>()
            .To<AdbMailCollectionPipeline>()
            .InSingletonScope();
        builder.Bind<MailCollectionTaskModule>()
            .ToSelf()
            .InSingletonScope();
        builder.Bind<IMissionCollectionPipeline>()
            .To<AdbMissionCollectionPipeline>()
            .InSingletonScope();
        builder.Bind<MissionCollectionTaskModule>()
            .ToSelf()
            .InSingletonScope();
        builder.Bind<IFileSystem>()
            .To<FileSystem>();
        builder.Bind<IEmulatorLauncher>()
            .To<EmulatorLauncher>();
        builder.Bind<IAsyncDelay>()
            .To<AsyncDelay>();
        builder.Bind<IWinAdapter>()
            .To<WinAdapter>();
        builder.Bind<IConnectionHealthMonitor>()
            .To<ConnectionHealthMonitor>()
            .InSingletonScope();

        builder.Bind<LogViewModel>().ToSelf().InSingletonScope();
        builder.Bind<IGrassTaskCatalog>()
            .To<DefaultGrassTaskCatalog>()
            .InSingletonScope();
        builder.Bind<GrassViewModel>().ToSelf().InSingletonScope();
        builder.Bind<OverviewViewModel>().ToSelf();
        builder.Bind<SettingsViewModel>().ToSelf().InSingletonScope();
        builder.Bind<DeveloperToolsViewModel>().ToSelf().InSingletonScope();
        builder.Bind<RootViewModel>().ToSelf();
    }

    protected override void Configure()
    {
        base.Configure();

        var umaService = Container.Get<IUmaService>();
        var umaDatabase = Container.Get<IUmaDatabaseService>();
        var resourceStore = Container.Get<IResourceStore>();
        _umaService = umaService;

        var appBaseDir = AppContext.BaseDirectory;

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UmamusumeAss");

        _startupInitialization = InitializeUmaServicesAsync(
            umaService,
            umaDatabase,
            resourceStore,
            appBaseDir,
            appDataDir,
            Container.Get<UpdateStateStore>(),
            Container.Get<ManifestVerifier>());

        var localization = Container.Get<ILocalizationService>();
        localization.Initialize();
    }

    protected override void Launch()
    {
        if (!CliDiagnostics.IsRequested(Args))
        {
            // Stylet's Launch hook is synchronous, so complete the explicit
            // startup barrier before constructing any resource-dependent view.
            _startupInitialization?.GetAwaiter().GetResult();
            CleanupCompletedUpdateCaches(Container.Get<UpdateStateStore>());
            base.Launch();
            _ = CompleteHealthAckAsync();
            _ = StartStartupUpdateCheckAsync();
            return;
        }

        var exitCode = CliDiagnostics.RunAsync(
                Args,
                Container.Get<HachimiJsonPipelineRunner>(),
                Container.Get<IAdbConnectionSessionFactory>(),
                Container.Get<IAdbRuntime>(),
                Container.Get<ISettingsService>())
            .GetAwaiter()
            .GetResult();
        Application.Current.Shutdown(exitCode);
    }

    private static async Task<bool> InitializeUmaServicesAsync(
        IUmaService umaService,
        IUmaDatabaseService umaDatabase,
        IResourceStore resourceStore,
        string appBaseDir,
        string appDataDir,
        UpdateStateStore updateState,
        ManifestVerifier manifestVerifier)
    {
        try
        {
            var bundledResource = Path.Combine(appBaseDir, "resource");
            // Launch() waits for this task synchronously. Keep the whole
            // startup chain off the WPF dispatcher or that wait deadlocks.
            await resourceStore.InitializeAsync(bundledResource).ConfigureAwait(false);
            if (!IsActiveResourceCompatible(resourceStore, updateState, manifestVerifier))
                await resourceStore.UseBundledFallbackAsync(bundledResource).ConfigureAwait(false);
            await umaService.InitializeAsync(
                resourceStore.ActiveCompositeBaseDirectory,
                appDataDir).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to initialize UmaService: {ex.Message}");
            return false;
        }

        try
        {
            var resourceRoot = umaService.ResourcePath
                ?? Path.Combine(appBaseDir, "resource");
            await umaDatabase.LoadAsync(resourceRoot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to load Uma database: {ex.Message}");
            return false;
        }
        return true;
    }

    private static bool IsActiveResourceCompatible(
        IResourceStore resources,
        UpdateStateStore updateState,
        ManifestVerifier verifier)
    {
        if (resources.Active.Version.StartsWith("bundled-", StringComparison.Ordinal)
            || resources.Active.Version.Equals(
                UpdateVersionInfo.CurrentVersion, StringComparison.Ordinal))
            return true;
        var state = updateState.Load();
        if (string.IsNullOrWhiteSpace(state.ResourceManifestPath)
            || string.IsNullOrWhiteSpace(state.ResourceSignaturePath)
            || !File.Exists(state.ResourceManifestPath)
            || !File.Exists(state.ResourceSignaturePath))
            return false;
        try
        {
            var bytes = File.ReadAllBytes(state.ResourceManifestPath);
            if (!ManifestVerifier.Sha256Bytes(bytes).Equals(
                    state.ResourceManifestSha256, StringComparison.OrdinalIgnoreCase))
                return false;
            var manifest = ManifestVerifier.Deserialize(bytes);
            var signature = ManifestVerifier.DecodeSignature(
                File.ReadAllText(state.ResourceSignaturePath));
            if (!verifier.Verify(bytes, signature)
                || !ManifestVerifier.IsCanonicalManifest(manifest, manifest.ReleaseTag)
                || !manifest.Kind.Equals("resource", StringComparison.Ordinal)
                || !manifest.ResourceSchema.Equals("1", StringComparison.Ordinal)
                || !manifest.SupportedResourceSchema.Equals("1", StringComparison.Ordinal)
                || !SemVersion.TryParse(UpdateVersionInfo.CurrentVersion, out var appVersion))
                return false;
            if (!string.IsNullOrWhiteSpace(manifest.MinAppVersion)
                && (!SemVersion.TryParse(manifest.MinAppVersion, out var min) || appVersion < min))
                return false;
            if (!string.IsNullOrWhiteSpace(manifest.MaxAppVersion)
                && (!SemVersion.TryParse(manifest.MaxAppVersion, out var max) || appVersion > max))
                return false;
            var full = manifest.Assets.FirstOrDefault(asset =>
                asset.Type.Equals("full", StringComparison.OrdinalIgnoreCase));
            return full is not null
                && full.TargetTreeSha256.Equals(
                    resources.Active.BaseTreeSha256, StringComparison.OrdinalIgnoreCase)
                && UpdateSelector.VerifyManagedTree(
                    resources.ActiveBaseDirectory, full, full.TargetTreeSha256);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task StartStartupUpdateCheckAsync()
    {
        try
        {
            await Container.Get<SettingsViewModel>()
                .RunStartupUpdateCheckAsync()
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // Startup networking is best effort and must not block the UI.
            Debug.WriteLine($"Startup update check failed: {exception.Message}");
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await ShutdownAsync(e).ConfigureAwait(true);
    }

    private async Task CompleteHealthAckAsync()
    {
        if (_startupInitialization is null)
            return;
        var succeeded = await _startupInitialization.ConfigureAwait(true);
        if (!succeeded)
            return;
        var ackPath = GetArgumentValue("--update-ack");
        if (string.IsNullOrWhiteSpace(ackPath))
            return;
        var operationId = GetArgumentValue("--update-operation");
        if (string.IsNullOrWhiteSpace(operationId))
            return;

        try
        {
            var stateStore = Container.Get<UpdateStateStore>();
            var appDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UmamusumeAss");
            var operationRoot = UpdateCachePaths.GetOperationRoot(
                operationId,
                Path.Combine(appDataRoot, "updates"));
            var operationManifestPath = Path.Combine(operationRoot, "manifest.json");
            var operationSignaturePath = Path.Combine(operationRoot, "manifest.sig");
            if (!File.Exists(operationManifestPath) || !File.Exists(operationSignaturePath))
                throw new FileNotFoundException("The installed update manifest is missing.");

            // Persist the trusted baseline before acknowledging health. If this
            // fails, the updater must keep its backup and roll back instead of
            // deleting the only recovery copy.
            var manifest = ManifestVerifier.Deserialize(
                await File.ReadAllBytesAsync(operationManifestPath));
            var trustedPaths = PersistTrustedProgramManifest(
                operationManifestPath,
                operationSignaturePath,
                appDataRoot);
            PersistProgramHealthState(
                stateStore,
                trustedPaths.ManifestPath,
                trustedPaths.SignaturePath,
                manifest);

            var temporary = ackPath + ".tmp-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ackPath))!);
            await File.WriteAllTextAsync(temporary, "healthy\n").ConfigureAwait(true);
            File.Move(temporary, ackPath, overwrite: true);
            await CleanupCompletedProgramOperationAsync(
                    operationRoot,
                    Path.Combine(operationRoot, "status.txt"))
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Failed to persist update health state: {exception.Message}");
            return;
        }
    }

    internal static void PersistProgramHealthState(
        UpdateStateStore stateStore,
        string manifestPath,
        string signaturePath,
        UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(signaturePath);
        ArgumentNullException.ThrowIfNull(manifest);

        var manifestHash = ManifestVerifier.Sha256File(manifestPath);
        var targetTreeHash = manifest.Assets
            .FirstOrDefault(asset => !string.IsNullOrWhiteSpace(asset.TargetTreeSha256))
            ?.TargetTreeSha256;
        stateStore.Update(state =>
        {
            state.Stage = "health-succeeded";
            state.CurrentManifestSigned = true;
            state.SignedManifestPath = manifestPath;
            state.SignedSignaturePath = signaturePath;
            state.ManifestSha256 = manifestHash;
            state.CurrentTreeSha256 = targetTreeHash;
            state.ForceFull = false;
            state.BackupPath = null;
            state.Error = null;
            state.StagedProgramOperationId = null;
            state.StagedProgramVersion = null;
            state.StagedProgramAssetName = null;
        });
    }

    private static string UpdatesRoot => UpdateCachePaths.GetUpdatesRoot();

    private static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UmamusumeAss");

    private static string TrustedProgramManifestRoot => AppDataRoot;

    private static (string ManifestPath, string SignaturePath) PersistTrustedProgramManifest(
        string operationManifestPath,
        string operationSignaturePath,
        string appDataRoot)
    {
        var trustedRoot = appDataRoot;
        Directory.CreateDirectory(trustedRoot);
        var manifestPath = Path.Combine(trustedRoot, "program-manifest.json");
        var signaturePath = Path.Combine(trustedRoot, "program-manifest.sig");
        CopyFileAtomically(operationManifestPath, manifestPath);
        CopyFileAtomically(operationSignaturePath, signaturePath);
        return (manifestPath, signaturePath);
    }

    private static void CopyFileAtomically(string sourcePath, string destinationPath)
    {
        var temporaryPath = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: true);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // A stale temporary file is harmless and is cleaned on the next run.
            }
            catch (UnauthorizedAccessException)
            {
                // A stale temporary file is harmless and is cleaned on the next run.
            }
        }
    }

    private static void CleanupCompletedUpdateCaches(UpdateStateStore stateStore)
    {
        var updatesRoot = UpdatesRoot;
        if (!Directory.Exists(updatesRoot))
            return;

        try
        {
            MigrateTrustedProgramManifest(stateStore, updatesRoot);
            CleanupVersionedManifestCaches(updatesRoot);
            foreach (var operationRoot in Directory.EnumerateDirectories(updatesRoot))
            {
                var directoryName = Path.GetFileName(operationRoot);
                if (directoryName.Equals("checks", StringComparison.OrdinalIgnoreCase))
                    continue;
                var statusPath = Path.Combine(operationRoot, "status.txt");
                if (!File.Exists(statusPath)
                    || !File.ReadAllText(statusPath).StartsWith(
                        "health-succeeded", StringComparison.OrdinalIgnoreCase))
                    continue;
                TryDeleteOperationRoot(operationRoot);
            }
            TryDeleteUpdatesRootIfNoPendingOperation(updatesRoot);
        }
        catch (IOException)
        {
            // Cache cleanup is best effort and must never block startup.
        }
        catch (UnauthorizedAccessException)
        {
            // Cache cleanup is best effort and must never block startup.
        }
    }

    private static void CleanupVersionedManifestCaches(string updatesRoot)
    {
        var checksRoot = Path.Combine(updatesRoot, "checks");
        if (!Directory.Exists(checksRoot))
            return;

        foreach (var path in Directory.EnumerateFiles(checksRoot))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith("program-manifest-", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("resource-manifest-", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A concurrent update check can still be using this file.
            }
            catch (UnauthorizedAccessException)
            {
                // A concurrent update check can still be using this file.
            }
        }
    }

    private static void MigrateTrustedProgramManifest(
        UpdateStateStore stateStore,
        string updatesRoot)
    {
        var state = stateStore.Load();
        var manifestPath = state.SignedManifestPath;
        var signaturePath = state.SignedSignaturePath;
        var hasTrustedFiles = state.CurrentManifestSigned
            && !string.IsNullOrWhiteSpace(manifestPath)
            && !string.IsNullOrWhiteSpace(signaturePath)
            && File.Exists(manifestPath)
            && File.Exists(signaturePath);

        if (hasTrustedFiles
            && (!IsPathUnderRoot(manifestPath!, updatesRoot)
                || !IsPathUnderRoot(signaturePath!, updatesRoot)))
            return;

        if (hasTrustedFiles
            && (!IsPathUnderRoot(manifestPath!, TrustedProgramManifestRoot)
                || !IsPathUnderRoot(signaturePath!, TrustedProgramManifestRoot)))
        {
            var trustedPaths = PersistTrustedProgramManifest(
                manifestPath!,
                signaturePath!,
                AppDataRoot);
            stateStore.Update(current =>
            {
                current.SignedManifestPath = trustedPaths.ManifestPath;
                current.SignedSignaturePath = trustedPaths.SignaturePath;
                current.ManifestSha256 = ManifestVerifier.Sha256File(trustedPaths.ManifestPath);
            });
            state = stateStore.Load();
        }

        if (state.CurrentManifestSigned
            && (string.IsNullOrWhiteSpace(state.SignedManifestPath)
                || string.IsNullOrWhiteSpace(state.SignedSignaturePath)
                || !File.Exists(state.SignedManifestPath)
                || !File.Exists(state.SignedSignaturePath)))
        {
            // The previous build could leave state pointing into a deleted
            // operation directory. Force a full update rather than retrying a
            // broken delta forever.
            stateStore.Update(current =>
            {
                current.CurrentManifestSigned = false;
                current.ForceFull = true;
                current.SignedManifestPath = null;
                current.SignedSignaturePath = null;
                current.ManifestSha256 = string.Empty;
                current.CurrentTreeSha256 = null;
                current.BackupPath = null;
                current.Error = null;
            });
        }
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteUpdatesRootIfNoPendingOperation(string updatesRoot)
    {
        if (!Directory.Exists(updatesRoot))
            return;

        try
        {
            var hasPendingOperation = Directory.EnumerateDirectories(updatesRoot)
                .Any(path =>
                {
                    var name = Path.GetFileName(path);
                    return !name.Equals("checks", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals("current-program", StringComparison.OrdinalIgnoreCase);
                });
            if (!hasPendingOperation)
                Directory.Delete(updatesRoot, recursive: true);
        }
        catch (IOException)
        {
            // Cache cleanup is best effort and will retry on the next launch.
        }
        catch (UnauthorizedAccessException)
        {
            // Cache cleanup is best effort and will retry on the next launch.
        }
    }

    private static async Task CleanupCompletedProgramOperationAsync(
        string operationRoot,
        string statusPath)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(statusPath)
                    && File.ReadAllText(statusPath).StartsWith(
                        "health-succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryDeleteOperationRoot(operationRoot))
                    {
                        TryDeleteUpdatesRootIfNoPendingOperation(UpdatesRoot);
                        return;
                    }
                }
            }
            catch (IOException)
            {
                // The updater may still be writing the status file.
            }
            catch (UnauthorizedAccessException)
            {
                // The updater may still be releasing its file handles.
            }

            await Task.Delay(100).ConfigureAwait(true);
        }
    }

    private static bool TryDeleteOperationRoot(string operationRoot)
    {
        if (!Directory.Exists(operationRoot))
            return true;

        try
        {
            Directory.Delete(operationRoot, recursive: true);
            return true;
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

    private async Task ShutdownAsync(ExitEventArgs e)
    {
        try
        {
            using var lease = Container.Get<IActivityRegistry>().Acquire(ActivityKind.Shutdown);
            if (_umaService is not null)
                await _umaService.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Failed to dispose update/runtime services: {exception.Message}");
        }
        finally
        {
            base.OnExit(e);
        }
    }

    private string? GetArgumentValue(string name)
    {
        for (var index = 0; index + 1 < Args.Length; index++)
        {
            if (Args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                return Args[index + 1];
        }
        return null;
    }
}
