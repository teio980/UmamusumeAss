using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Views;

public sealed partial class DeveloperToolsView : UserControl
{
    private const double MinPreviewScale = 0.5;
    private const double MaxPreviewScale = 4.0;
    private const double PreviewScaleStep = 1.1;

    private bool _isSelecting;
    private bool _isPanning;
    private Point _selectionStart;
    private Point _panStart;
    private Vector _panStartTranslation;
    private Point _lastPreviewPointer;
    private bool _hasPreviewPointer;
    private double _previewScale = 1.0;
    private Vector _previewTranslation;
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
        _isPanning = false;
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
            Dispatcher.BeginInvoke(() =>
            {
                if (e.PropertyName == nameof(DeveloperToolsViewModel.ScreenshotImage))
                {
                    SetPreviewScale(1.0);
                    SetPreviewTranslation(new Vector());
                }

                UpdatePreviewState();
            });
        }
    }

    private void OnPreviewSurfaceSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateCropRectangle();

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var point = e.GetPosition(CropOverlay);
        if (_viewModel?.HasScreenshot != true
            || e.Delta == 0
            || !TryGetImageLayout(out var imageRect, out _)
            || !imageRect.Contains(ToPreviewContentPoint(point)))
        {
            return;
        }

        SetPreviewScale(_previewScale * (e.Delta > 0
            ? PreviewScaleStep
            : 1.0 / PreviewScaleStep));
        UpdatePreviewCoordinate(point);
        e.Handled = true;
    }

    private void OnPanMouseMiddleButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle
            || _viewModel is not { HasScreenshot: true })
        {
            return;
        }

        _isPanning = true;
        _panStart = e.GetPosition(CropOverlay);
        _panStartTranslation = _previewTranslation;
        CropOverlay.CaptureMouse();
        e.Handled = true;
    }

    private void OnPanMouseMiddleButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !_isPanning)
        {
            return;
        }

        _isPanning = false;
        CropOverlay.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnCropMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is not { HasScreenshot: true })
        {
            return;
        }

        _isSelecting = true;
        _selectionStart = e.GetPosition(CropOverlay);
        CropOverlay.CaptureMouse();
        SetRectangle(SelectionRectangle, _selectionStart, _selectionStart);
        e.Handled = true;
    }

    private void OnCropMouseMove(object sender, MouseEventArgs e)
    {
        var current = e.GetPosition(CropOverlay);
        UpdatePreviewCoordinate(current);

        if (_isPanning && e.MiddleButton == MouseButtonState.Pressed)
        {
            SetPreviewTranslation(_panStartTranslation + current - _panStart);
            e.Handled = true;
            return;
        }

        if (!_isSelecting || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        SetRectangle(SelectionRectangle, _selectionStart, e.GetPosition(CropOverlay));
    }

    private void OnCropMouseLeave(object sender, MouseEventArgs e)
    {
        _hasPreviewPointer = false;
        PreviewCoordinateBadge.Visibility = Visibility.Collapsed;
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

        SelectionRectangle.Visibility = Visibility.Collapsed;
        UpdateCropRectangle();
        e.Handled = true;
    }

    private void UpdatePreviewState()
    {
        EmptyPreview.Visibility = _viewModel?.HasScreenshot == true
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateCropRectangle();
        UpdateRoiBadge();
    }

    private void SetPreviewScale(double scale)
    {
        _previewScale = Math.Clamp(scale, MinPreviewScale, MaxPreviewScale);
        PreviewScaleTransform.ScaleX = _previewScale;
        PreviewScaleTransform.ScaleY = _previewScale;
        UpdateCropRectangle();
    }

    private void SetPreviewTranslation(Vector translation)
    {
        _previewTranslation = translation;
        PreviewTranslateTransform.X = translation.X;
        PreviewTranslateTransform.Y = translation.Y;
        UpdateCropRectangle();
        if (_hasPreviewPointer)
        {
            UpdatePreviewCoordinate(_lastPreviewPointer);
        }
    }

    private void UpdatePreviewCoordinate(Point point)
    {
        _lastPreviewPointer = point;
        _hasPreviewPointer = true;
        if (_viewModel?.ScreenshotImage is not { } image
            || !TryGetImageLayout(out var imageRect, out var scale)
            || !imageRect.Contains(ToPreviewContentPoint(point)))
        {
            PreviewCoordinateBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var contentPoint = ToPreviewContentPoint(point);
        var x = Math.Clamp(
            (int)Math.Floor((contentPoint.X - imageRect.Left) / scale),
            0,
            image.PixelWidth - 1);
        var y = Math.Clamp(
            (int)Math.Floor((contentPoint.Y - imageRect.Top) / scale),
            0,
            image.PixelHeight - 1);
        PreviewCoordinateText.Text = $"X: {x}, Y: {y}";
        PreviewCoordinateBadge.Visibility = Visibility.Visible;
    }

    private static void SetRectangle(Rectangle rectangle, Point start, Point end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var width = Math.Abs(end.X - start.X);
        var height = Math.Abs(end.Y - start.Y);
        rectangle.SetCurrentValue(Canvas.LeftProperty, left);
        rectangle.SetCurrentValue(Canvas.TopProperty, top);
        rectangle.Width = width;
        rectangle.Height = height;
        rectangle.Visibility = Visibility.Visible;
    }

    private void UpdateCropRectangle()
    {
        if (_viewModel?.CropRegion is not { } region || !TryGetImageLayout(out var imageRect, out var scale))
        {
            CropRectangle.Visibility = Visibility.Collapsed;
            return;
        }

        SetRectangle(
            CropRectangle,
            new Point(imageRect.Left + region.X * scale, imageRect.Top + region.Y * scale),
            new Point(
                imageRect.Left + (region.X + region.Width) * scale,
                imageRect.Top + (region.Y + region.Height) * scale));
    }

    private void UpdateRoiBadge()
    {
        if (_viewModel?.CropRegion is not { } region)
        {
            PreviewRoiBadge.Visibility = Visibility.Collapsed;
            return;
        }

        PreviewRoiText.Text = $"ROI: [{region.X}, {region.Y}, {region.Width}, {region.Height}]";
        PreviewRoiBadge.Visibility = Visibility.Visible;
    }

    private bool TryGetCropRegion(Point start, Point end, out Int32Rect region)
    {
        region = default;
        if (_viewModel?.ScreenshotImage is not { } image
            || !TryGetImageLayout(out var imageRect, out var scale))
        {
            return false;
        }

        var selection = new Rect(
            ToPreviewContentPoint(start),
            ToPreviewContentPoint(end));
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

    private Point ToPreviewContentPoint(Point point)
    {
        var center = new Point(
            PreviewSurface.ActualWidth / 2,
            PreviewSurface.ActualHeight / 2);
        var translatedPoint = point - _previewTranslation;
        return new Point(
            center.X + (translatedPoint.X - center.X) / _previewScale,
            center.Y + (translatedPoint.Y - center.Y) / _previewScale);
    }

    private bool TryGetImageLayout(out Rect imageRect, out double scale)
    {
        imageRect = default;
        scale = 0;
        if (_viewModel?.ScreenshotImage is not { } image
            || PreviewContent.ActualWidth <= 0
            || PreviewContent.ActualHeight <= 0)
        {
            return false;
        }

        scale = Math.Min(
            PreviewContent.ActualWidth / image.PixelWidth,
            PreviewContent.ActualHeight / image.PixelHeight);
        var width = image.PixelWidth * scale;
        var height = image.PixelHeight * scale;
        imageRect = new Rect(
            (PreviewContent.ActualWidth - width) / 2,
            (PreviewContent.ActualHeight - height) / 2,
            width,
            height);
        return scale > 0;
    }
}
