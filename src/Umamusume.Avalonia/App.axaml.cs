using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Umamusume.AppCore.Services;
using Umamusume.CoreBridge;
using Umamusume.Avalonia.ViewModels;
using Umamusume.Avalonia.Views;

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
            var autoService = new UmaAutomationService(logger, connState, coreService, settings);

            var vm = new MainWindowViewModel(autoService, settings, logger, connState, loc, coreService);
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm
            };
            desktop.MainWindow.Opened += async (_, _) =>
            {
                if (settings.Load().StartupUpdateCheck) await vm.Settings.CheckUpdatesAsync();
            };
            bool closing = false;
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
                desktop.MainWindow.Close();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
