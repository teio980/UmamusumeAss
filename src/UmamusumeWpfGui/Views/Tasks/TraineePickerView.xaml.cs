using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UmamusumeWpfGui.Views.Tasks;

public sealed partial class TraineePickerView : UserControl
{
    public TraineePickerView() => InitializeComponent();

    private void TraineeList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: not null } list)
            return;

        var property = list.DataContext?.GetType().GetProperty(
            "IsTraineeDropDownOpen",
            BindingFlags.Instance | BindingFlags.Public);
        if (property?.CanWrite == true)
            property.SetValue(list.DataContext, false);
    }
}
