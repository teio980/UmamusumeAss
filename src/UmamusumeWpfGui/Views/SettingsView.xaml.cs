using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Views;









public sealed partial class SettingsView : UserControl
{



    public SettingsView()
    {
        InitializeComponent();
    }





    private void OnSettingsTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != SettingsTabs
            || DataContext is not SettingsViewModel vm
            || SettingsTabs.SelectedIndex < 0)
        {
            return;
        }

        vm.SelectedMenuIndex = SettingsTabs.SelectedIndex;
    }





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


        if (!string.IsNullOrEmpty(vm.DraftAdbPath))
        {
            try
            {
                dialog.FileName = System.IO.Path.GetFileName(vm.DraftAdbPath);
                dialog.InitialDirectory = System.IO.Path.GetDirectoryName(vm.DraftAdbPath);
            }
            catch
            {

            }
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            vm.DraftAdbPath = dialog.FileName;
        }
    }
}
