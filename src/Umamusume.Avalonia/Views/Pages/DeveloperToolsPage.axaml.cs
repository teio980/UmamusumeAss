using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Umamusume.Avalonia.Services;
using Umamusume.Avalonia.ViewModels;

namespace Umamusume.Avalonia.Views.Pages;

public partial class DeveloperToolsPage : UserControl
{
    private Point? _selectionStart;

    public DeveloperToolsPage()
    {
        InitializeComponent();
    }

    private async void OnImportScreenshotClick(object? sender, RoutedEventArgs e)
    {
        string? path = await PickOpenFileAsync("导入截图", ImageFileTypes);
        if (path is not null && DataContext is DeveloperToolsViewModel vm)
            vm.ImportScreenshot(path);
    }

    private async void OnChooseTemplateClick(object? sender, RoutedEventArgs e)
    {
        string? path = await PickOpenFileAsync("选择模板图像", ImageFileTypes);
        if (path is not null && DataContext is DeveloperToolsViewModel vm)
            vm.TemplatePath = path;
    }

    private async void OnExportScreenshotClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeveloperToolsViewModel vm) return;
        string? path = await PickSaveFileAsync("导出截图", "screenshot.png", "PNG 图片", ["*.png"]);
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
        if (DataContext is not DeveloperToolsViewModel vm) return;
        string? path = await PickSaveFileAsync("保存截图裁剪", "crop.png", "PNG 图片", ["*.png"]);
        if (path is null) return;
        try
        {
            await vm.SaveCropAsync(path);
        }
        catch (Exception ex)
        {
            vm.ShowError("保存裁剪失败", ex.Message);
        }
    }

    private async void OnOpenPipelineClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeveloperToolsViewModel vm) return;
        string? path = await PickOpenFileAsync("打开 Pipeline JSON", JsonFileTypes);
        if (path is null) return;
        var file = new DeveloperResourceFile(Path.GetFullPath(path), Path.GetFileName(path));
        if (!vm.PipelineFiles.Any(existing => PathEquals(existing.Path, file.Path)))
            vm.PipelineFiles.Add(file);
        vm.SelectedPipelineFile = file;
    }

    private async void OnSavePipelineClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeveloperToolsViewModel vm) return;
        string? path = vm.SelectedPipelineFile?.Path;
        if (path is null)
            path = await PickSaveFileAsync("保存 Pipeline JSON", "pipeline.json", "JSON 文件", ["*.json"]);
        if (path is null) return;
        await vm.SavePipelineToAsync(path);
    }

    private async void OnChooseReplayFixtureClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeveloperToolsViewModel vm) return;
        string? path = await PickOpenFileAsync("选择离线回放 Fixture", JsonFileTypes);
        if (path is null) return;
        var file = new DeveloperResourceFile(Path.GetFullPath(path), Path.GetFileName(path));
        if (!vm.ReplayFixtures.Any(existing => PathEquals(existing.Path, file.Path)))
            vm.ReplayFixtures.Add(file);
        vm.SelectedReplayFixture = file;
    }

    private async void OnOpenScenarioManifestClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeveloperToolsViewModel vm) return;
        string? path = await PickOpenFileAsync("打开场景包 manifest", JsonFileTypes);
        if (path is null) return;
        var file = new DeveloperResourceFile(Path.GetFullPath(path), Path.GetFileName(path));
        if (!vm.ScenarioManifests.Any(existing => PathEquals(existing.Path, file.Path)))
            vm.ScenarioManifests.Add(file);
        vm.SelectedScenarioManifest = file;
    }

    private void OnScreenshotPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not DeveloperToolsViewModel { HasScreenshot: true } vm) return;
        var point = ToImagePoint(e.GetPosition(ScreenshotCanvas), vm);
        if (point is null) return;
        _selectionStart = point;
        e.Pointer.Capture(ScreenshotCanvas);
        vm.SetCropRegion((int)point.Value.X, (int)point.Value.Y, 1, 1);
        UpdateCropOverlay(vm);
        e.Handled = true;
    }

    private void OnScreenshotPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_selectionStart is not { } start || DataContext is not DeveloperToolsViewModel vm) return;
        var point = ToImagePoint(e.GetPosition(ScreenshotCanvas), vm);
        if (point is null) return;
        vm.SetCropRegion((int)Math.Min(start.X, point.Value.X), (int)Math.Min(start.Y, point.Value.Y),
            (int)Math.Abs(point.Value.X - start.X), (int)Math.Abs(point.Value.Y - start.Y));
        UpdateCropOverlay(vm);
    }

    private void OnScreenshotPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_selectionStart is not { } start || DataContext is not DeveloperToolsViewModel vm) return;
        var point = ToImagePoint(e.GetPosition(ScreenshotCanvas), vm);
        if (point is not null)
        {
            vm.SetCropRegion((int)Math.Min(start.X, point.Value.X), (int)Math.Min(start.Y, point.Value.Y),
                (int)Math.Abs(point.Value.X - start.X), (int)Math.Abs(point.Value.Y - start.Y));
            UpdateCropOverlay(vm);
        }
        _selectionStart = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private Point? ToImagePoint(Point position, DeveloperToolsViewModel vm)
    {
        if (vm.ScreenshotBitmap is not { } bitmap
            || ScreenshotCanvas.Bounds.Width <= 0
            || ScreenshotCanvas.Bounds.Height <= 0)
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

    private void UpdateCropOverlay(DeveloperToolsViewModel vm)
    {
        if (vm.CropRegion is not { } crop || vm.ScreenshotBitmap is not { } bitmap) return;
        double availableWidth = Math.Max(1, ScreenshotCanvas.Bounds.Width - 16);
        double availableHeight = Math.Max(1, ScreenshotCanvas.Bounds.Height - 16);
        double scale = Math.Min(availableWidth / bitmap.PixelSize.Width, availableHeight / bitmap.PixelSize.Height);
        Canvas.SetLeft(CropSelection, (ScreenshotCanvas.Bounds.Width - bitmap.PixelSize.Width * scale) / 2 + crop.X * scale);
        Canvas.SetTop(CropSelection, (ScreenshotCanvas.Bounds.Height - bitmap.PixelSize.Height * scale) / 2 + crop.Y * scale);
        CropSelection.Width = Math.Max(2, crop.Width * scale);
        CropSelection.Height = Math.Max(2, crop.Height * scale);
    }

    private async Task<string?> PickOpenFileAsync(string title, FilePickerFileType[] filters)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = filters
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private async Task<string?> PickSaveFileAsync(string title, string suggestedName, string typeName, string[] patterns)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = Path.GetExtension(suggestedName).TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = patterns }]
        });
        return file?.TryGetLocalPath();
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static readonly FilePickerFileType[] ImageFileTypes =
    [
        new("图像文件") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.tif", "*.tiff"] }
    ];

    private static readonly FilePickerFileType[] JsonFileTypes =
    [
        new("JSON 文件") { Patterns = ["*.json"] }
    ];
}
