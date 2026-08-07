using System.Windows.Controls;
using System.Windows.Input;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Views.Tasks;

public sealed partial class DailyRaceTaskSettingsView : UserControl
{
    public DailyRaceTaskSettingsView() => InitializeComponent();

    private void TraineeList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: not null }
            && DataContext is DailyRaceTaskSettingsViewModel settings)
        {
            settings.IsTraineeDropDownOpen = false;
        }
    }
}
