using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia;
using Avalonia.Interactivity;
using Umamusume.Avalonia.ViewModels;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Umamusume.Avalonia.Views.Pages;

public partial class ScreenOcrPage : UserControl
{
    private Point? _selectionStart;

    public ScreenOcrPage()
    {
        InitializeComponent();
    }

    private async void OnImportScreenshotClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickImageAsync("导入截图");
        if (path is not null && DataContext is ScreenOcrViewModel vm)
            vm.ImportScreenshot(path);
    }

    private async void OnChooseTemplateClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickImageAsync("选择模板图像");
        if (path is not null && DataContext is ScreenOcrViewModel vm)
            vm.TemplateImagePath = path;
    }

    private async void OnExportScreenshotClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScreenOcrViewModel vm) return;
        var path = await PickSaveAsync("导出截图", "screenshot.png");
        if (path is null) return;
        try
        {
            await vm.ExportScreenshotAsync(path);
        }
        catch (Exception ex)
        {
            vm.ShowError("导出截图失败", ex.Message);
        }
    }

    private async void OnSaveCropClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScreenOcrViewModel vm) return;
        var path = await PickSaveAsync("保存裁剪图像", "screenshot-crop.png");
        if (path is null) return;
        try
        {
            await vm.SaveCropAsync(path);
        }
        catch (Exception ex)
        {
            vm.ShowError("保存裁剪图像失败", ex.Message);
        }
    }

    private void OnScreenshotPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ScreenOcrViewModel { HasScreenshot: true } vm)
            return;
        var point = ToImagePoint(e.GetPosition(ScreenshotCanvas), vm);
        if (point is null) return;
        _selectionStart = point;
        e.Pointer.Capture(ScreenshotCanvas);
        vm.SetCropRegion((int)point.Value.X, (int)point.Value.Y, 1, 1);
        UpdateCropOverlay(vm, point.Value);
        e.Handled = true;
    }

    private void OnScreenshotPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_selectionStart is not { } start || DataContext is not ScreenOcrViewModel vm)
            return;
        var point = ToImagePoint(e.GetPosition(ScreenshotCanvas), vm);
        if (point is null) return;
        vm.SetCropRegion((int)Math.Min(start.X, point.Value.X), (int)Math.Min(start.Y, point.Value.Y),
            (int)Math.Abs(point.Value.X - start.X), (int)Math.Abs(point.Value.Y - start.Y));
        UpdateCropOverlay(vm, point.Value);
    }

    private void OnScreenshotPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_selectionStart is not { } start || DataContext is not ScreenOcrViewModel vm)
            return;
        var point = ToImagePoint(e.GetPosition(ScreenshotCanvas), vm);
        if (point is not null)
        {
            vm.SetCropRegion((int)Math.Min(start.X, point.Value.X), (int)Math.Min(start.Y, point.Value.Y),
                (int)Math.Abs(point.Value.X - start.X), (int)Math.Abs(point.Value.Y - start.Y));
            UpdateCropOverlay(vm, point.Value);
        }
        _selectionStart = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private Point? ToImagePoint(Point position, ScreenOcrViewModel vm)
    {
        if (vm.ScreenshotBitmap is not { } bitmap || ScreenshotCanvas.Bounds.Width <= 0 || ScreenshotCanvas.Bounds.Height <= 0)
            return null;
        double availableWidth = Math.Max(1, ScreenshotCanvas.Bounds.Width - 16);
        double availableHeight = Math.Max(1, ScreenshotCanvas.Bounds.Height - 16);
        double scale = Math.Min(availableWidth / bitmap.PixelSize.Width, availableHeight / bitmap.PixelSize.Height);
        double displayedWidth = bitmap.PixelSize.Width * scale;
        double displayedHeight = bitmap.PixelSize.Height * scale;
        double left = (ScreenshotCanvas.Bounds.Width - displayedWidth) / 2;
        double top = (ScreenshotCanvas.Bounds.Height - displayedHeight) / 2;
        if (position.X < left || position.Y < top
            || position.X >= left + displayedWidth || position.Y >= top + displayedHeight)
            return null;
        int x = Math.Clamp((int)((position.X - left) / scale), 0, bitmap.PixelSize.Width - 1);
        int y = Math.Clamp((int)((position.Y - top) / scale), 0, bitmap.PixelSize.Height - 1);
        return new Point(x, y);
    }

    private void UpdateCropOverlay(ScreenOcrViewModel vm, Point current)
    {
        if (vm.CropRegion is not { } crop || vm.ScreenshotBitmap is not { } bitmap) return;
        double availableWidth = Math.Max(1, ScreenshotCanvas.Bounds.Width - 16);
        double availableHeight = Math.Max(1, ScreenshotCanvas.Bounds.Height - 16);
        double scale = Math.Min(availableWidth / bitmap.PixelSize.Width, availableHeight / bitmap.PixelSize.Height);
        double left = (ScreenshotCanvas.Bounds.Width - bitmap.PixelSize.Width * scale) / 2 + crop.X * scale;
        double top = (ScreenshotCanvas.Bounds.Height - bitmap.PixelSize.Height * scale) / 2 + crop.Y * scale;
        Canvas.SetLeft(CropSelection, left);
        Canvas.SetTop(CropSelection, top);
        CropSelection.Width = Math.Max(2, crop.Width * scale);
        CropSelection.Height = Math.Max(2, crop.Height * scale);
    }

    private async Task<string?> PickImageAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("图像文件")
            {
                Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.tif", "*.tiff"]
            }]
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private async Task<string?> PickSaveAsync(string title, string suggestedName)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] }]
        });
        return file?.TryGetLocalPath();
    }
}
