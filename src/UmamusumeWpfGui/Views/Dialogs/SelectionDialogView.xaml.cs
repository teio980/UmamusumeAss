using System.Windows;
using UmamusumeWpfGui.ViewModels.Dialogs;

namespace UmamusumeWpfGui.Views.Dialogs;

/// <summary>
/// Selection dialog for choosing among multiple detected emulator candidates.
/// Wires the ViewModel's <see cref="SelectionDialogViewModel.RequestClose"/>
/// event to close the window with the appropriate result.
/// </summary>
public sealed partial class SelectionDialogView : Window
{
    /// <summary>
    /// Creates the selection dialog and wires the close handler.
    /// </summary>
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
