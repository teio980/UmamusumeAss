using System;
using Avalonia;

namespace Umamusume.Avalonia;

public static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Keep dropdowns in the main window so macOS DLL/debug launches do not
            // depend on a separate native popup window's activation and ordering.
            .With(new AvaloniaNativePlatformOptions { OverlayPopups = true })
            .WithInterFont()
            .LogToTrace();
}
