using System.Windows;
using System.Windows.Controls;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Views;

public sealed partial class HachimiTaskSettingsView : UserControl
{
    public HachimiTaskSettingsView() => InitializeComponent();

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is GrassViewModel viewModel)
            viewModel.CloseTaskSettings();
    }
}
