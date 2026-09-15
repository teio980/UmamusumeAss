using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using UmamusumeWpfGui.ViewModels;
using Wpf.Ui.Controls;

namespace UmamusumeWpfGui.Views;






public sealed partial class RootView : FluentWindow
{
    public static readonly DependencyProperty IsActiveVisualProperty =
        DependencyProperty.RegisterAttached(
            "IsActiveVisual",
            typeof(bool),
            typeof(RootView),
            new FrameworkPropertyMetadata(false));

    private readonly DispatcherTimer _navigationDebounceTimer;
    private readonly DispatcherTimer _navigationInputGateTimer;
    private int? _pendingNavigationIndex;
    private bool _navigationInputLocked;
    private NavigationViewItem? _activeNavigationItem;




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
        SizeChanged += OnRootSizeChanged;
        RootNavigation.SizeChanged += OnRootNavigationSizeChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (RootNavigation.SelectedItem is null && RootNavigation.MenuItems.Count > 0)
        {
            var firstItem = RootNavigation.MenuItems[0] as NavigationViewItem;
            RootNavigation.SetCurrentValue(
                System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
                firstItem);
            if (firstItem is not null)
            {
                SetActiveNavigationItem(firstItem);
                ScheduleNavigationIndicatorUpdate(firstItem, animate: false);
            }
        }
        else if (RootNavigation.SelectedItem is NavigationViewItem selectedItem)
        {
            SetActiveNavigationItem(selectedItem);
            ScheduleNavigationIndicatorUpdate(selectedItem, animate: false);
        }
    }

    private void OnRootNavigationSelectionChanged(
        object sender,
        RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, RootNavigation)
            || RootNavigation.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        SetActiveNavigationItem(item);
        QueueNavigationItem(item);
        ScheduleNavigationIndicatorUpdate(item, animate: IsLoaded);
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
        SetActiveNavigationItem(item);
        ScheduleNavigationIndicatorUpdate(item, animate: IsLoaded);
        QueueNavigationItem(item);
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_activeNavigationItem is { } item)
            ScheduleNavigationIndicatorUpdate(item, animate: false);
    }

    private void OnRootNavigationSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_activeNavigationItem is { } item)
            ScheduleNavigationIndicatorUpdate(item, animate: false);
    }

    private void ScheduleNavigationIndicatorUpdate(
        NavigationViewItem item,
        bool animate)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => UpdateNavigationIndicator(item, animate)));
    }

    private void UpdateNavigationIndicator(NavigationViewItem item, bool animate)
    {
        if (!IsLoaded
            || !ReferenceEquals(_activeNavigationItem, item)
            || item.ActualHeight <= 0
            || NavigationIndicatorCanvas.ActualHeight <= 0)
        {
            return;
        }

        try
        {
            var itemOrigin = item.TransformToAncestor(this).Transform(new Point(0, 0));
            var canvasOrigin = NavigationIndicatorCanvas
                .TransformToAncestor(this)
                .Transform(new Point(0, 0));
            var left = itemOrigin.X - canvasOrigin.X + 4;
            var top = itemOrigin.Y - canvasOrigin.Y
                + Math.Max(0, (item.ActualHeight - 16) / 2);
            var maxLeft = Math.Max(0, NavigationIndicatorCanvas.ActualWidth - 3);

            NavigationIndicatorAnimator.SetBounds(
                NavigationSelectionIndicator,
                new Rect(
                    Math.Clamp(left, 0, maxLeft),
                    Math.Max(0, top),
                    3,
                    16),
                animate);
        }
        catch (InvalidOperationException)
        {
            // The item can be re-templated while a layout pass is in flight.
            // The next selection/resize pass will position the shared visual.
        }
    }

    private void SetActiveNavigationItem(NavigationViewItem activeItem)
    {
        _activeNavigationItem = activeItem;

        foreach (var rawItem in RootNavigation.MenuItems)
        {
            if (rawItem is NavigationViewItem item)
            {
                SetIsActiveVisual(item, ReferenceEquals(item, activeItem));
            }
        }

        foreach (var rawItem in RootNavigation.FooterMenuItems)
        {
            if (rawItem is NavigationViewItem item)
            {
                SetIsActiveVisual(item, ReferenceEquals(item, activeItem));
            }
        }
    }

    public static void SetIsActiveVisual(DependencyObject element, bool value)
    {
        element.SetValue(IsActiveVisualProperty, value);
    }

    public static bool GetIsActiveVisual(DependencyObject element)
    {
        return (bool)element.GetValue(IsActiveVisualProperty);
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
