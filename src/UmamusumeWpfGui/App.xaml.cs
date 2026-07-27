using System.Windows;
using Stylet;

namespace UmamusumeWpfGui;

#pragma warning disable CA1001 // Bootstrapper disposed in OnExit
public partial class App : Application
{
    private Bootstrapper? _bootstrapper;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _bootstrapper = new Bootstrapper();
        _bootstrapper.Start(e.Args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _bootstrapper?.Dispose();
        base.OnExit(e);
    }
}
#pragma warning restore CA1001
