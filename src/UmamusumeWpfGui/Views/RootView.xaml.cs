using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using UmamusumeWpfGui.ViewModels;
using Wpf.Ui.Controls;

namespace UmamusumeWpfGui.Views;






public sealed partial class RootView : FluentWindow
{
    private readonly DispatcherTimer _navigationDebounceTimer;
    private readonly DispatcherTimer _navigationInputGateTimer;
    private int? _pendingNavigationIndex;
    private bool _navigationInputLocked;




    public RootView()
    {
        InitializeComponent();
        _navigationDebounceTimer = new DispatcherTimer(
            DispatcherPriority.Background,
            Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _navigationDebounceTimer.Tick += OnNavigationDebounceTick;
        _navigationInputGateTimer = new DispatcherTimer(
            DispatcherPriority.Input,
            Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _navigationInputGateTimer.Tick += OnNavigationInputGateTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (RootNavigation.SelectedItem is null && RootNavigation.MenuItems.Count > 0)
        {
            RootNavigation.SetCurrentValue(
                System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
                RootNavigation.MenuItems[0]);
        }
    }

    private void OnNavigationItemPreviewMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || sender is not NavigationViewItem item)
        {
            return;
        }

        e.Handled = true;
        TryAcceptNavigationItem(item);
    }

    private void OnNavigationItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is NavigationViewItem item)
        {
            TryAcceptNavigationItem(item);
        }
    }

    private void TryAcceptNavigationItem(NavigationViewItem item)
    {
        if (_navigationInputLocked
            || !int.TryParse(item.Tag?.ToString(), out _))
        {
            return;
        }

        _navigationInputLocked = true;
        _navigationInputGateTimer.Stop();
        _navigationInputGateTimer.Start();
        RootNavigation.SetCurrentValue(
            System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
            item);
        QueueNavigationItem(item);
    }

    private void QueueNavigationItem(NavigationViewItem item)
    {
        if (!int.TryParse(item.Tag?.ToString(), out var index))
        {
            return;
        }

        _pendingNavigationIndex = index;
        _navigationDebounceTimer.Stop();
        _navigationDebounceTimer.Start();
    }

    private void OnNavigationDebounceTick(object? sender, EventArgs e)
    {
        _navigationDebounceTimer.Stop();
        var index = _pendingNavigationIndex;
        _pendingNavigationIndex = null;
        if (index is null || DataContext is not RootViewModel viewModel)
        {
            return;
        }

        viewModel.SelectedNavigationIndex = index.Value;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _navigationDebounceTimer.Stop();
        _navigationInputGateTimer.Stop();
        _navigationInputLocked = false;
        _pendingNavigationIndex = null;
    }

    private void OnNavigationInputGateTick(object? sender, EventArgs e)
    {
        _navigationInputGateTimer.Stop();
        _navigationInputLocked = false;
    }
}
