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
using UmamusumeWpfGui.Services.Update;
using UmamusumeWpfGui.ViewModels;
using UmamusumeWpfGui.ViewModels.Tasks;
using ActivityKind = UmamusumeWpfGui.Models.ActivityKind;

namespace UmamusumeWpfGui;

public class Bootstrapper : Bootstrapper<RootViewModel>
{
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan VisualShutdownBudget = TimeSpan.FromSeconds(2);

    private Task<bool>? _startupInitialization;
    private IUmaService? _umaService;
    private IUmaVisualDeviceFacade? _visualDeviceFacade;

    protected override void ConfigureIoC(IStyletIoCBuilder builder)
    {

        builder.Bind<IConnectionStateService>()
            .To<ConnectionStateService>()
            .InSingletonScope();

        builder.Bind<IUmaService>()
            .To<UmaService>()
            .InSingletonScope();
        builder.Bind<IUmaVisualDeviceFacade>()
            .To<UmaVisualDeviceFacade>()
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
        builder.Bind<ShopTaskModule>()
            .ToSelf()
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
        builder.Bind<CareerTrainingTaskModule>()
            .ToSelf()
            .InSingletonScope();
        builder.Bind<MailCollectionTaskModule>()
            .ToSelf()
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
        builder.Bind<IGrassTaskExecutor>()
            .To<NativeGrassTaskExecutor>()
            .InSingletonScope();
        builder.Bind<IGrassTaskExecutionService>()
            .To<GrassTaskExecutionService>()
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
        var visualDeviceFacade = Container.Get<IUmaVisualDeviceFacade>();
        var umaDatabase = Container.Get<IUmaDatabaseService>();
        var resourceStore = Container.Get<IResourceStore>();
        _umaService = umaService;
        _visualDeviceFacade = visualDeviceFacade;

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
            base.Launch();
            _ = StartStartupUpdateCheckAsync();
            return;
        }

        // Diagnostics now connects and captures through the native Core
        // device path, so initialize it through the same startup barrier.
        _startupInitialization?.GetAwaiter().GetResult();

        var exitCode = CliDiagnostics.RunAsync(
                Args,
                Container.Get<IAdbRuntime>(),
                Container.Get<ISettingsService>(),
                Container.Get<IUmaService>())
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
            TemplateMatcher.ConfigureNativeRecognition(umaService);
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
            var catalog = await umaService.GetCatalogAsync("all").ConfigureAwait(false);
            umaDatabase.LoadFromCoreCatalog(catalog, resourceRoot);
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
        var bundledVersion = "bundled-" + UpdateVersionInfo.CurrentVersion;
        if (resources.Active.Version.Equals(bundledVersion, StringComparison.Ordinal)
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

    protected override void OnExit(ExitEventArgs e)
    {
        long shutdownStarted = Stopwatch.GetTimestamp();
        IDisposable? shutdownLease = null;
        try
        {
            try
            {
                shutdownLease = Container.Get<IActivityRegistry>().Acquire(ActivityKind.Shutdown);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Failed to register shutdown activity: {exception.Message}");
            }

            // Stylet disposes the root view model as soon as OnExit returns.
            // Start teardown away from the UI SynchronizationContext, then keep
            // pumping dispatcher work until it finishes or the existing global
            // shutdown budget expires.
            IDisposable? shutdownTaskLease = shutdownLease;
            Task shutdownTask = Task.Run(() => ShutdownAsync(shutdownStarted, shutdownTaskLease));
            shutdownLease = null;
            TimeSpan remaining = RemainingShutdownTime(shutdownStarted);
            bool completed = DispatcherShutdownBarrier.Wait(
                shutdownTask,
                Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher,
                remaining,
                exception => Debug.WriteLine($"Shutdown task failed after the exit barrier timed out: {exception.Message}"));
            if (!completed)
                Debug.WriteLine("Shutdown exceeded its 10-second budget; pending managed cleanup will continue in the background.");
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Failed to complete application shutdown: {exception.Message}");
        }
        finally
        {
            try
            {
                shutdownLease?.Dispose();
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Failed to release shutdown activity: {exception.Message}");
            }

            base.OnExit(e);
        }
    }

    private async Task ShutdownAsync(long shutdownStarted, IDisposable? shutdownLease)
    {
        try
        {
            try
            {
                IUmaVisualDeviceFacade? visualFacade = _visualDeviceFacade;
                if (visualFacade is not null)
                {
                    Task visualDisposal = Task.Run(async () =>
                        await visualFacade.DisposeAsync().ConfigureAwait(false));
                    TimeSpan remaining = RemainingShutdownTime(shutdownStarted);
                    TimeSpan visualBudget = remaining < VisualShutdownBudget
                        ? remaining
                        : VisualShutdownBudget;
                    try
                    {
                        await visualDisposal.WaitAsync(visualBudget).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        DispatcherShutdownBarrier.ObserveLateFault(
                            visualDisposal,
                            exception => Debug.WriteLine($"Native device facade disposal failed after timing out: {exception.Message}"));
                        Debug.WriteLine("Native device facade disposal exceeded its 2-second shutdown budget.");
                    }
                    catch (Exception exception)
                    {
                        Debug.WriteLine($"Failed to dispose native device facade: {exception.Message}");
                    }
                }
            }
            finally
            {
                if (_umaService is not null)
                {
                    TimeSpan remaining = RemainingShutdownTime(shutdownStarted);
                    try
                    {
                        await _umaService.DisposeAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        Debug.WriteLine($"Failed to dispose update/runtime services: {exception.Message}");
                    }
                }
            }
        }
        finally
        {
            try
            {
                shutdownLease?.Dispose();
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Failed to release shutdown activity: {exception.Message}");
            }
        }
    }

    private static TimeSpan RemainingShutdownTime(long shutdownStarted)
    {
        TimeSpan remaining = ShutdownBudget - Stopwatch.GetElapsedTime(shutdownStarted);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

}
