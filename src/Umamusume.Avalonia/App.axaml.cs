using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Umamusume.AppCore.Services;
using Umamusume.CoreBridge;
using Umamusume.Avalonia.ViewModels;
using Umamusume.Avalonia.Views;
using Umamusume.Avalonia.Services;

namespace Umamusume.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var logger = new LogService();
            var connState = new ConnectionStateService();
            var settings = new JsonSettingsService();
            var loc = new LocalizationService();
            var coreService = new UmaService(new ImmediateEventDispatcher());
            var resourceStore = new MacResourceStore();
            var autoService = new UmaAutomationService(logger, connState, coreService, settings,
                resourceSourceProvider: OperatingSystem.IsMacOS() ? resourceStore : null);

            var vm = new MainWindowViewModel(autoService, settings, logger, connState, loc, coreService);
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm
            };
            desktop.MainWindow.Opened += async (_, _) =>
            {
                if (MacUpdateHealth.Request is not null)
                {
                    bool healthReported = false;
                    try
                    {
                        await autoService.RunMaintenanceWhenIdleAsync(async token =>
                        {
                            try
                            {
                                await autoService.InitializeAsync(token);
                                await coreService.GetCatalogAsync("all", token);
                                await MacUpdateHealth.ReportAsync(true);
                                healthReported = true;
                                return true;
                            }
                            catch (Exception ex)
                            {
                                autoService.RequireRestartAfterMaintenanceFailure(ex.Message);
                                try
                                {
                                    await MacUpdateHealth.ReportAsync(false, ex.Message);
                                    healthReported = true;
                                }
                                catch (Exception reportError)
                                {
                                    logger.Log($"无法写入更新健康结果：{reportError.Message}", Umamusume.AppCore.Models.LogEntryKind.Warning);
                                }
                                throw;
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        if (!healthReported)
                        {
                            autoService.RequireRestartAfterMaintenanceFailure(ex.Message);
                            try { await MacUpdateHealth.ReportAsync(false, ex.Message); }
                            catch (Exception reportError) { logger.Log($"无法写入更新健康结果：{reportError.Message}", Umamusume.AppCore.Models.LogEntryKind.Warning); }
                        }
                        logger.Log($"更新后健康检查失败：{ex.Message}", Umamusume.AppCore.Models.LogEntryKind.Error);
                    }
                }
                else
                {
                    await vm.Settings.RestoreStagedUpdatesAsync();
                    if (settings.Load().StartupUpdateCheck)
                        await vm.Settings.CheckUpdatesAsync();
                }
            };
            bool closing = false;
            var shutdownCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            desktop.MainWindow.Closing += async (_, e) =>
            {
                if (closing) return;
                e.Cancel = true;
                closing = true;
                autoService.CancelCurrentOperation();
                vm.Settings.CancelUpdateCommand.Execute(null);
                vm.Tasks.Dispose();
                vm.DeveloperTools.Dispose();
                vm.ScreenOcr.ClearAllCommand.Execute(null);
                try { await coreService.DisposeAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception ex) { logger.Log($"核心关闭失败：{ex.Message}", Umamusume.AppCore.Models.LogEntryKind.Warning); }
                shutdownCompleted.TrySetResult();
                desktop.MainWindow.Close();
            };
            MacAppLifecycle.RequestShutdownAsync = async () =>
            {
                await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => desktop.MainWindow.Close());
                await shutdownCompleted.Task.ConfigureAwait(false);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
