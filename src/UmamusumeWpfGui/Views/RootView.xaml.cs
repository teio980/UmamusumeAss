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
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (RootNavigation.SelectedItem is null && RootNavigation.MenuItems.Count > 0)
        {
            RootNavigation.SetCurrentValue(
                System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
                RootNavigation.MenuItems[0]);
        }
    }

    private void OnNavigationSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is NavigationView navigationView
            && navigationView.SelectedItem is NavigationViewItem item)
        {
            ApplyNavigationItem(item);
        }
    }

    private void OnNavigationItemInvoked(NavigationView sender, RoutedEventArgs e)
    {
        if (sender.SelectedItem is NavigationViewItem item)
        {
            ApplyNavigationItem(item);
        }
    }

    private void OnNavigationItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not NavigationViewItem item)
            return;

        SelectNavigationItem(item);
    }

    private void OnNavigationItemMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is NavigationViewItem item)
        {
            SelectNavigationItem(item);
        }
    }

    private void SelectNavigationItem(NavigationViewItem item)
    {
        RootNavigation.SetCurrentValue(
            System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
            item);
        ApplyNavigationItem(item);
    }

    private void ApplyNavigationItem(NavigationViewItem item)
    {
        if (!int.TryParse(item.Tag?.ToString(), out var index)
            || DataContext is not RootViewModel viewModel)
        {
            return;
        }

        viewModel.SelectedNavigationIndex = index;
    }
}
