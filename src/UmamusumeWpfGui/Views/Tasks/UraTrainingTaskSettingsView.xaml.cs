using System.Windows.Controls;
using System.Windows.Input;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Views.Tasks;

public partial class UraTrainingTaskSettingsView : UserControl
{
    public UraTrainingTaskSettingsView()
    {
        InitializeComponent();
    }

    private void TraineeList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: not null }
            && DataContext is UraTrainingTaskSettingsViewModel settings)
        {
            settings.IsTraineeDropDownOpen = false;
        }
    }
}
