using Avalonia.Controls;
using Avalonia.Input;
using Umamusume.Avalonia.ViewModels;

namespace Umamusume.Avalonia.Views.Pages;

public partial class TasksPage : UserControl
{
    public TasksPage()
    {
        InitializeComponent();
    }

    private void OnTaskDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is TasksViewModel vm && vm.SelectedTask is not null)
        {
            vm.OpenTaskSettings(vm.SelectedTask);
        }
    }
}
