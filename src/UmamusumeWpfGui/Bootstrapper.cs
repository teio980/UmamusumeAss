using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Stylet;
using StyletIoC;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui;

public class Bootstrapper : Bootstrapper<RootViewModel>
{
    protected override void ConfigureIoC(IStyletIoCBuilder builder)
    {
        // Singleton services (shared state)
        builder.Bind<IConnectionStateService>()
            .To<ConnectionStateService>()
            .InSingletonScope();

        builder.Bind<IUmaService>()
            .To<UmaService>()
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

        // Transient helpers
        builder.Bind<IProcessEnumerator>()
            .To<ProcessEnumerator>();
        builder.Bind<IAdbRunner>()
            .To<AdbRunner>();
        builder.Bind<IFileSystem>()
            .To<FileSystem>();
        builder.Bind<IEmulatorLauncher>()
            .To<EmulatorLauncher>();
        builder.Bind<IAsyncDelay>()
            .To<AsyncDelay>();
        builder.Bind<IWinAdapter>()
            .To<WinAdapter>();

        builder.Bind<LogViewModel>().ToSelf().InSingletonScope();
        builder.Bind<OverviewViewModel>().ToSelf();
        builder.Bind<SettingsViewModel>().ToSelf();
        builder.Bind<RootViewModel>().ToSelf();
    }

    protected override void Configure()
    {
        base.Configure();

        var umaService = Container.Get<IUmaService>();

        var appBaseDir = Path.GetDirectoryName(
            Assembly.GetExecutingAssembly().Location)
            ?? AppContext.BaseDirectory;

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UmamusumeAss");

        _ = InitializeUmaServiceAsync(umaService, appBaseDir, appDataDir);

        var localization = Container.Get<ILocalizationService>();
        localization.Initialize();
    }

    private static async Task InitializeUmaServiceAsync(
        IUmaService umaService, string appBaseDir, string appDataDir)
    {
        try
        {
            await umaService.InitializeAsync(appBaseDir, appDataDir);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to initialize UmaService: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
    }
}
