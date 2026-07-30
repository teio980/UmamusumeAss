using System.Windows;
using UmamusumeWpfGui.ViewModels;
using Wpf.Ui.Controls;

namespace UmamusumeWpfGui.Views;

/// <summary>
/// Main application window hosting Log and Settings tabs via TabControl.
/// Child views are composed through Stylet's View.Model attached property.
/// </summary>
public sealed partial class RootView : FluentWindow
{
    /// <summary>
    /// Creates the RootView.
    /// </summary>
    public RootView()
    {
        InitializeComponent();
    }

    private void OnNavigationSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not NavigationView navigationView
            || navigationView.SelectedItem is not NavigationViewItem item
            || !int.TryParse(item.Tag?.ToString(), out var index)
            || DataContext is not RootViewModel viewModel)
        {
            return;
        }

        viewModel.SelectedNavigationIndex = index;
    }
}
