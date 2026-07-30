using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using UmamusumeWpfGui.ViewModels;
using Wpf.Ui.Controls;

namespace UmamusumeWpfGui.Views;

/// <summary>
/// Settings view with an official WPF-UI top NavigationView.
/// Connection, Language, and System panels switch based on selected menu index.
///
/// Code-behind handles only:
/// - File dialog plumbing for Browse ADB Path
/// - Top navigation item routing
/// </summary>
public sealed partial class SettingsView : UserControl
{
    /// <summary>
    /// Creates the SettingsView.
    /// </summary>
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Routes the official top NavigationView item click to the existing
    /// SettingsViewModel section index and updates its active indicator.
    /// </summary>
    private void OnSettingsNavigationItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not NavigationViewItem item
            || DataContext is not SettingsViewModel vm
            || !int.TryParse(item.Tag?.ToString(), out var index))
        {
            return;
        }

        vm.SelectedMenuIndex = index;
        foreach (var menuItem in SettingsNavigation.MenuItems)
        {
            if (menuItem is NavigationViewItem navigationItem)
            {
                navigationItem.IsActive = ReferenceEquals(navigationItem, item);
            }
        }
    }

    /// <summary>
    /// Opens an OpenFileDialog to select an ADB executable and
    /// updates the ViewModel's DraftAdbPath.
    /// </summary>
    private void OnBrowseAdbPath(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Select ADB Executable",
            Filter = "ADB Executable (adb.exe)|adb.exe|All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        // Set initial directory from current draft value, or default
        if (!string.IsNullOrEmpty(vm.DraftAdbPath))
        {
            try
            {
                dialog.FileName = System.IO.Path.GetFileName(vm.DraftAdbPath);
                dialog.InitialDirectory = System.IO.Path.GetDirectoryName(vm.DraftAdbPath);
            }
            catch
            {
                // Ignore invalid paths
            }
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            vm.DraftAdbPath = dialog.FileName;
        }
    }
}
