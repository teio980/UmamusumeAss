using System;
using Avalonia;
using Umamusume.Avalonia.Services;

namespace Umamusume.Avalonia;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!MacAppInstanceGate.TryAcquire()) return;
        MacUpdateHealth.Initialize(args);
        if (MacUpdateHealth.Request is null && MacUpdateRecovery.TryStartRecoveryIfNeeded())
        {
            MacAppInstanceGate.Release();
            return;
        }
        if (MacUpdateHealth.Request is null) MacUpdateRecovery.TryInitializeHealthFromJournal();
        string[] startupArgs = args.Length == 4 && args[0] == "--update-health" ? Array.Empty<string>() : args;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(startupArgs);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Keep dropdowns in the main window so macOS DLL/debug launches do not
            // depend on a separate native popup window's activation and ordering.
            .With(new AvaloniaNativePlatformOptions { OverlayPopups = true })
            .WithInterFont()
            .LogToTrace();
}
