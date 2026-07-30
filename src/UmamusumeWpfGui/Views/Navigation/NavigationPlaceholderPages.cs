using System.Windows.Controls;

namespace UmamusumeWpfGui.Views.Navigation;

/// <summary>
/// Empty pages used only to let WPF-UI own selection and navigation state.
/// The visible application content remains composed by Stylet's ActiveContent
/// bridge so each feature view has one, and only one, visual instance.
/// </summary>
public sealed class OverviewNavigationPage : Page
{
}

public sealed class LogNavigationPage : Page
{
}

public sealed class SettingsNavigationPage : Page
{
}

public sealed class GrassNavigationPage : Page
{
}
