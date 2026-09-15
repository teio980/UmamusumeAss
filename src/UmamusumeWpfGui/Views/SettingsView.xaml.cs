using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Views;









public sealed partial class SettingsView : UserControl
{
    private bool _tabSelectionAnimationPending;



    public SettingsView()
    {
        InitializeComponent();
        SettingsTabs.SelectionChanged += HandleTabSelectionChanged;
        SettingsTabs.SizeChanged += HandleSettingsTabsSizeChanged;
        SettingsTabs.LayoutUpdated += HandleSettingsTabsLayoutUpdated;
        Loaded += HandleSettingsLoaded;
    }





    private void OnBrowseAdbPath(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Select ADB Executable",
            Filter = "ADB Executable (adb.exe)|adb.exe|All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };


        if (!string.IsNullOrEmpty(vm.DraftAdbPath))
        {
            try
            {
                dialog.FileName = System.IO.Path.GetFileName(vm.DraftAdbPath);
                dialog.InitialDirectory = System.IO.Path.GetDirectoryName(vm.DraftAdbPath);
            }
            catch
            {

            }
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            vm.DraftAdbPath = dialog.FileName;
        }
    }

    private void HandleSettingsLoaded(object sender, RoutedEventArgs e)
    {
        UpdateTabIndicator(animate: false);
    }

    private void HandleTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged bubbles from ComboBox and other controls in the
        // tab content. Only a TabItem change should move the shared visual.
        if (e.OriginalSource is not TabItem
            && !ReferenceEquals(e.OriginalSource, SettingsTabs))
            return;

        _tabSelectionAnimationPending = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                _tabSelectionAnimationPending = false;
                UpdateTabIndicator(animate: IsLoaded);
            }));
    }

    private void HandleSettingsTabsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_tabSelectionAnimationPending)
            UpdateTabIndicator(animate: false);
    }

    private void HandleSettingsTabsLayoutUpdated(object? sender, EventArgs e)
    {
        // Header widths can change without TabControl.SizeChanged when a
        // DynamicResource is replaced during localization. Repositioning is
        // immediate here so a resize never leaves the indicator behind.
        if (IsLoaded && !_tabSelectionAnimationPending)
            UpdateTabIndicator(animate: false);
    }

    private void UpdateTabIndicator(bool animate)
    {
        if (!IsLoaded
            || SettingsTabs.SelectedItem is not TabItem selected
            || selected.ActualWidth <= 0
            || selected.ActualHeight <= 0
            || !TryGetTabIndicatorParts(out var canvas, out var indicator)
            || (!animate && NavigationIndicatorAnimator.IsAnimating(indicator))
            || canvas.ActualWidth <= 0)
        {
            return;
        }

        try
        {
            var selectedOrigin = selected.TransformToAncestor(SettingsTabs)
                .Transform(new Point(0, 0));
            var canvasOrigin = canvas.TransformToAncestor(SettingsTabs)
                .Transform(new Point(0, 0));
            // qfluentwidgets.Pivot keeps a fixed 16 px indicator centered
            // beneath the selected item; it does not stretch to the whole
            // header width.
            const double indicatorLength = 16;
            // The template's right padding is spacing, not part of the
            // header text. Center the fixed indicator under the text area,
            // as qfluentwidgets.Pivot does.
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
            // A template can be rebuilt while a resource/localization update
            // is being applied. The next layout pass will retry.
        }
    }

    private bool TryGetTabIndicatorParts(out Canvas canvas, out Border indicator)
    {
        if (SettingsTabs.Template is null)
        {
            canvas = null!;
            indicator = null!;
            return false;
        }

        var foundCanvas = SettingsTabs.Template.FindName("TabIndicatorCanvas", SettingsTabs) as Canvas;
        var foundIndicator = SettingsTabs.Template.FindName("SelectedTabIndicator", SettingsTabs) as Border;
        if (foundCanvas is null || foundIndicator is null)
        {
            canvas = null!;
            indicator = null!;
            return false;
        }

        canvas = foundCanvas;
        indicator = foundIndicator;
        return true;
    }
}
