using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UmamusumeWpfGui.Views;

namespace UmamusumeWpfGui.Views.Tasks;

public partial class CareerTrainingTaskSettingsView : UserControl
{
    private bool _indicatorAnimationPending;

    public CareerTrainingTaskSettingsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        CareerSettingsTabs.SelectionChanged += OnTabSelectionChanged;
        CareerSettingsTabs.SizeChanged += OnTabsSizeChanged;
        CareerSettingsTabs.LayoutUpdated += OnTabsLayoutUpdated;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => UpdateTabIndicator(animate: false)));

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _indicatorAnimationPending = false;
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is not TabItem
            && !ReferenceEquals(e.OriginalSource, CareerSettingsTabs))
            return;

        _indicatorAnimationPending = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                _indicatorAnimationPending = false;
                UpdateTabIndicator(animate: IsLoaded);
            }));
    }

    private void OnTabsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_indicatorAnimationPending)
            UpdateTabIndicator(animate: false);
    }

    private void OnTabsLayoutUpdated(object? sender, EventArgs e)
    {
        if (IsLoaded && !_indicatorAnimationPending)
            UpdateTabIndicator(animate: false);
    }

    private void UpdateTabIndicator(bool animate)
    {
        if (!IsLoaded
            || CareerSettingsTabs.SelectedItem is not TabItem selected
            || selected.ActualWidth <= 0
            || selected.ActualHeight <= 0
            || !TryGetTabIndicatorParts(out var canvas, out var indicator)
            || (!animate && NavigationIndicatorAnimator.IsAnimating(indicator))
            || canvas.ActualWidth <= 0)
            return;

        try
        {
            var selectedOrigin = selected.TransformToAncestor(CareerSettingsTabs)
                .Transform(new Point(0, 0));
            var canvasOrigin = canvas.TransformToAncestor(CareerSettingsTabs)
                .Transform(new Point(0, 0));
            const double indicatorLength = 16;
            const double headerRightPadding = 18;
            var contentWidth = Math.Max(0, selected.ActualWidth - headerRightPadding);
            var left = selectedOrigin.X - canvasOrigin.X
                + Math.Max(0, (contentWidth - indicatorLength) / 2);
            var top = selectedOrigin.Y - canvasOrigin.Y
                + Math.Max(0, selected.ActualHeight - 3);
            NavigationIndicatorAnimator.SetBounds(
                indicator,
                new Rect(Math.Max(0, left), Math.Max(0, top), indicatorLength, 3),
                animate);
        }
        catch (InvalidOperationException)
        {
            // Retry on the next layout pass while a template is rebuilding.
        }
    }

    private bool TryGetTabIndicatorParts(out Canvas canvas, out Border indicator)
    {
        canvas = CareerSettingsTabs.Template?.FindName(
            "TabIndicatorCanvas", CareerSettingsTabs) as Canvas ?? null!;
        indicator = CareerSettingsTabs.Template?.FindName(
            "SelectedTabIndicator", CareerSettingsTabs) as Border ?? null!;
        return canvas is not null && indicator is not null;
    }

}
