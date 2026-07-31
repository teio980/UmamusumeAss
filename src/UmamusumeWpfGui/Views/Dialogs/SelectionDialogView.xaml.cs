using System.Windows;
using UmamusumeWpfGui.ViewModels.Dialogs;
using Wpf.Ui.Controls;

namespace UmamusumeWpfGui.Views.Dialogs;






public sealed partial class SelectionDialogView : FluentWindow
{



    public SelectionDialogView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SelectionDialogViewModel vm)
        {
            vm.RequestClose += OnRequestClose;
        }
    }

    private void OnRequestClose(bool? dialogResult)
    {
        DialogResult = dialogResult;
        Close();
    }
}
