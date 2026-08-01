using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Views;

public sealed partial class DeveloperToolsView : UserControl
{
    private bool _isSelecting;
    private Point _selectionStart;
    private DeveloperToolsViewModel? _viewModel;

    public DeveloperToolsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToViewModel();
        UpdatePreviewState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeFromViewModel();
        CropOverlay.ReleaseMouseCapture();
        _isSelecting = false;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnsubscribeFromViewModel();
        SubscribeToViewModel();
        UpdatePreviewState();
    }

    private void SubscribeToViewModel()
    {
        if (_viewModel is not null || DataContext is not DeveloperToolsViewModel viewModel)
        {
            return;
        }

        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void UnsubscribeFromViewModel()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeveloperToolsViewModel.ScreenshotImage)
            or nameof(DeveloperToolsViewModel.CropRegion)
            or nameof(DeveloperToolsViewModel.HasScreenshot))
        {
            Dispatcher.BeginInvoke(UpdatePreviewState);
        }
    }

    private void OnPreviewSurfaceSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateCropRectangle();

    private void OnCropMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is not { HasScreenshot: true })
        {
            return;
        }

        _isSelecting = true;
        _selectionStart = e.GetPosition(CropOverlay);
        CropOverlay.CaptureMouse();
        SetSelectionRectangle(_selectionStart, _selectionStart);
        e.Handled = true;
    }

    private void OnCropMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSelecting || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        SetSelectionRectangle(_selectionStart, e.GetPosition(CropOverlay));
    }

    private void OnCropMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting)
        {
            return;
        }

        var end = e.GetPosition(CropOverlay);
        _isSelecting = false;
        CropOverlay.ReleaseMouseCapture();
        if (TryGetCropRegion(_selectionStart, end, out var region))
        {
            _viewModel?.SetCropRegion(region);
        }
        else
        {
            _viewModel?.SetCropRegion(null);
        }

        UpdateCropRectangle();
        e.Handled = true;
    }

    private void UpdatePreviewState()
    {
        EmptyPreview.Visibility = _viewModel?.HasScreenshot == true
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateCropRectangle();
    }

    private void SetSelectionRectangle(Point start, Point end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var width = Math.Abs(end.X - start.X);
        var height = Math.Abs(end.Y - start.Y);
        CropRectangle.SetCurrentValue(Canvas.LeftProperty, left);
        CropRectangle.SetCurrentValue(Canvas.TopProperty, top);
        CropRectangle.Width = width;
        CropRectangle.Height = height;
        CropRectangle.Visibility = Visibility.Visible;
    }

    private void UpdateCropRectangle()
    {
        if (_viewModel?.CropRegion is not { } region || !TryGetImageLayout(out var imageRect, out var scale))
        {
            CropRectangle.Visibility = Visibility.Collapsed;
            return;
        }

        SetSelectionRectangle(
            new Point(imageRect.Left + region.X * scale, imageRect.Top + region.Y * scale),
            new Point(
                imageRect.Left + (region.X + region.Width) * scale,
                imageRect.Top + (region.Y + region.Height) * scale));
    }

    private bool TryGetCropRegion(Point start, Point end, out Int32Rect region)
    {
        region = default;
        if (_viewModel?.ScreenshotImage is not { } image
            || !TryGetImageLayout(out var imageRect, out var scale))
        {
            return false;
        }

        var selection = new Rect(start, end);
        selection.Intersect(imageRect);
        if (selection.Width < 2 || selection.Height < 2 || scale <= 0)
        {
            return false;
        }

        var x = (int)Math.Floor((selection.Left - imageRect.Left) / scale);
        var y = (int)Math.Floor((selection.Top - imageRect.Top) / scale);
        var right = (int)Math.Ceiling((selection.Right - imageRect.Left) / scale);
        var bottom = (int)Math.Ceiling((selection.Bottom - imageRect.Top) / scale);
        x = Math.Clamp(x, 0, image.PixelWidth);
        y = Math.Clamp(y, 0, image.PixelHeight);
        right = Math.Clamp(right, x, image.PixelWidth);
        bottom = Math.Clamp(bottom, y, image.PixelHeight);
        region = new Int32Rect(x, y, right - x, bottom - y);
        return region.Width > 0 && region.Height > 0;
    }

    private bool TryGetImageLayout(out Rect imageRect, out double scale)
    {
        imageRect = default;
        scale = 0;
        if (_viewModel?.ScreenshotImage is not { } image
            || CropOverlay.ActualWidth <= 0
            || CropOverlay.ActualHeight <= 0)
        {
            return false;
        }

        scale = Math.Min(
            CropOverlay.ActualWidth / image.PixelWidth,
            CropOverlay.ActualHeight / image.PixelHeight);
        var width = image.PixelWidth * scale;
        var height = image.PixelHeight * scale;
        imageRect = new Rect(
            (CropOverlay.ActualWidth - width) / 2,
            (CropOverlay.ActualHeight - height) / 2,
            width,
            height);
        return scale > 0;
    }
}
