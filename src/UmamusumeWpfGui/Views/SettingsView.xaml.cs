using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Views;

/// <summary>
/// Settings view with a 160 px left navigation and scrollable right content panel.
/// Connection, Language, and System panels switch based on selected menu index.
///
/// Code-behind handles only:
/// - File dialog plumbing for Browse ADB Path
/// - Navigation item click routing
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
    /// Handles navigation item clicks by updating the ViewModel's selected index.
    /// </summary>
    private void OnNavItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element
            && element.DataContext is MenuItemViewModel menuItem
            && DataContext is SettingsViewModel vm)
        {
            vm.SelectedMenuIndex = menuItem.Index;
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
