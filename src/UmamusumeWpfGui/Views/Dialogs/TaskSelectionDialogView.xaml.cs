using System.Windows;
using UmamusumeWpfGui.ViewModels.Dialogs;
using Wpf.Ui.Controls;

namespace UmamusumeWpfGui.Views.Dialogs;

public sealed partial class TaskSelectionDialogView : FluentWindow
{
    public TaskSelectionDialogView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is TaskSelectionDialogViewModel vm)
            vm.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(bool? dialogResult)
    {
        DialogResult = dialogResult;
        Close();
    }
}
