using System.Windows;

namespace UmamusumeWpfGui.Views;

/// <summary>
/// Main application window hosting Log and Settings tabs via TabControl.
/// Child views are composed through Stylet's View.Model attached property.
/// </summary>
public sealed partial class RootView : Window
{
    /// <summary>
    /// Creates the RootView.
    /// </summary>
    public RootView()
    {
        InitializeComponent();
    }
}
