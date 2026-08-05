using System.Diagnostics;
using System.IO;
using System.Windows;
using Stylet;
using StyletIoC;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Helper;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.ViewModels;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui;

public class Bootstrapper : Bootstrapper<RootViewModel>
{
    protected override void ConfigureIoC(IStyletIoCBuilder builder)
    {

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


        builder.Bind<IProcessEnumerator>()
            .To<ProcessEnumerator>();
        builder.Bind<IAdbRunner>()
            .To<AdbRunner>();
        builder.Bind<IAdbRuntime>()
            .To<AdbRuntime>();
        builder.Bind<IAdbTouchRuntime>()
            .To<AdbTouchRuntime>();
        builder.Bind<IVisualPipelineRuntime>()
            .To<AdbVisualPipelineRuntime>()
            .InSingletonScope();
        builder.Bind<HachimiJsonPipelineRunner>()
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

        var appBaseDir = AppContext.BaseDirectory;

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
