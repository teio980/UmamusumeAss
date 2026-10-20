using System.Linq;
using Avalonia;
using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using Umamusume.Avalonia.ViewModels;

namespace Umamusume.Avalonia.Views;

public partial class MainWindow : AppWindow
{
    public MainWindow()
    {
        InitializeComponent();

        TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBar.TitleBarHitTestType = TitleBarHitTestType.Complex;

        Loaded += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                UpdateSettingsLabel(vm);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainWindowViewModel.SettingsNavigationLabel)) UpdateSettingsLabel(vm);
                };
            }
            if (NavView.MenuItems is System.Collections.IList { Count: > 0 } list)
            {
                NavView.SelectedItem = list[0];
            }
        };
    }

    private void UpdateSettingsLabel(MainWindowViewModel vm)
    {
        if (NavView.SettingsItem is NavigationViewItem item) item.Content = vm.SettingsNavigationLabel;
    }

    private void OnNavSelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        if (e.IsSettingsSelected)
        {
            vm.Navigate("Settings");
        }
        else if (e.SelectedItemContainer is NavigationViewItem nvi && nvi.Tag is string tag)
        {
            vm.Navigate(tag);
        }
    }
}
