using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Services;
using Umamusume.CoreBridge;

namespace Umamusume.Avalonia.ViewModels;

public sealed partial class ScreenOcrViewModel : ViewModelBase
{
    private readonly IUmaAutomationService _automationService;
    private readonly ILogService _logService;
    private readonly IConnectionStateService _connectionStateService;
    private readonly IUmaService? _coreService;
    private CapturedScreenshot? _currentScreenshot;
    private ImageCropRegion? _cropRegion;

    private static readonly TemplateMatchMethod[] TemplateMatchMethods =
        Enum.GetValues<TemplateMatchMethod>();

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private Bitmap? _screenshotBitmap;

    [ObservableProperty]
    private string _screenshotDimensionsText = "-";

    [ObservableProperty]
    private string _screenshotTimestampText = "-";

    [ObservableProperty]
    private bool _hasScreenshot;

    [ObservableProperty]
    private bool _isCapturing;

    [ObservableProperty]
    private bool _isRecognizing;

    [ObservableProperty]
    private bool _isMatchingTemplate;

    [ObservableProperty]
    private string _templateImagePath = string.Empty;

    [ObservableProperty]
    private string _templateMatchThreshold = "0.86";

    [ObservableProperty]
    private TemplateMatchMethod _templateMatchMethod = TemplateMatchMethod.Gray;

    [ObservableProperty]
    private string _templateMatchResultText = "选择模板图像后可测试当前截图。";

    [ObservableProperty]
    private string _cropRegionText = "未选择裁剪区域";

    [ObservableProperty]
    private bool _hasCropRegion;

    [ObservableProperty]
    private string _ocrLanguage = "eng";

    [ObservableProperty]
    private string _ocrResultLanguageText = "当前原生 OCR 资源仅支持 eng。";

    [ObservableProperty]
    private string _allRecognizedText = string.Empty;

    [ObservableProperty]
    private bool _hasOcrDetections;

    [ObservableProperty]
    private int _detectionCount;

    // In-page Notification (InfoBar)
    [ObservableProperty]
    private bool _isNotificationOpen;

    [ObservableProperty]
    private string _notificationTitle = string.Empty;

    [ObservableProperty]
    private string _notificationMessage = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _notificationSeverity = InfoBarSeverity.Informational;

    public IReadOnlyList<string> AvailableLanguages { get; } = new[] { "eng" };

    public ObservableCollection<OcrItemViewModel> OcrDetections { get; } = new();

    public ImageCropRegion? CropRegion => _cropRegion;

    public ScreenOcrViewModel(
        IUmaAutomationService automationService,
        ILogService logService,
        IConnectionStateService connectionStateService,
        IUmaService? coreService = null)
    {
        _automationService = automationService;
        _logService = logService;
        _connectionStateService = connectionStateService;
        _coreService = coreService;
        IsConnected = _connectionStateService.State == ConnectionState.Connected;

        _connectionStateService.StateChanged += (s, e) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsConnected = _connectionStateService.State == ConnectionState.Connected;
            });
        };

        _automationService.ScreenshotCaptured += captured =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                UpdateScreenshot(captured);
            });
        };
    }

    private void UpdateScreenshot(CapturedScreenshot captured)
    {
        try
        {
            if (captured.PngBytes.Length > 0)
            {
                using var ms = new MemoryStream(captured.PngBytes);
                ScreenshotBitmap?.Dispose();
                ScreenshotBitmap = new Bitmap(ms);
                ScreenshotDimensionsText = $"{captured.Width} × {captured.Height} 像素";
                ScreenshotTimestampText = captured.CapturedAt.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                _currentScreenshot = captured;
                HasScreenshot = true;
                SetCropRegion(null);
                OcrDetections.Clear();
                OcrResultLanguageText = "当前原生 OCR 资源仅支持 eng。";
                AllRecognizedText = string.Empty;
                HasOcrDetections = false;
                DetectionCount = 0;
            }
        }
        catch (Exception ex)
        {
            _logService.Log($"解码截图图像位图失败: {ex.Message}", LogEntryKind.Error);
        }
    }

    public void ImportScreenshot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            RecognitionImage? decoded = RecognitionImageDecoder.DecodeBytes(bytes);
            if (decoded is null)
            {
                ShowNotification("导入失败", "无法解码此图像文件。", InfoBarSeverity.Error);
                return;
            }

            using var bitmapStream = new MemoryStream(bytes, writable: false);
            using var bitmap = new Bitmap(bitmapStream);
            using var normalizedStream = new MemoryStream();
            bitmap.Save(normalizedStream);
            byte[] pngBytes = normalizedStream.ToArray();
            _currentScreenshot = new CapturedScreenshot(
                pngBytes,
                decoded.RgbaPixels,
                decoded.Width,
                decoded.Height,
                File.GetLastWriteTime(path));
            UpdateScreenshot(_currentScreenshot);
            ShowNotification("图像已导入", $"{Path.GetFileName(path)} ({decoded.Width}×{decoded.Height})", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowNotification("导入截图失败", ex.Message, InfoBarSeverity.Error);
            _logService.Log($"导入截图失败: {ex}", LogEntryKind.Error);
        }
    }

    public async Task ExportScreenshotAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_currentScreenshot is null)
            throw new InvalidOperationException("当前没有可导出的截图。");

        await File.WriteAllBytesAsync(path, _currentScreenshot.PngBytes, cancellationToken);
        ShowNotification("截图已导出", path, InfoBarSeverity.Success);
    }

    public async Task SaveCropAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_currentScreenshot is null || _cropRegion is not { } crop)
            throw new InvalidOperationException("请先选择一个有效的截图裁剪区域。");

        cancellationToken.ThrowIfCancellationRequested();
        var source = RecognitionImageDecoder.DecodeBytes(_currentScreenshot.PngBytes)
            ?? throw new InvalidDataException("当前截图无法解码。");
        var cropped = RecognitionImageDecoder.Transform(source, new RecognitionImageTransform(
            crop.X, crop.Y, crop.Width, crop.Height));
        if (cropped.RgbaPixels is not { } rgba || rgba.Length != checked(cropped.Width * cropped.Height * 4))
            throw new InvalidDataException("裁剪图像没有可保存的 RGBA 像素数据。");

        using var bitmap = new WriteableBitmap(
            new PixelSize(cropped.Width, cropped.Height),
            new Vector(96, 96),
            PixelFormat.Rgba8888,
            AlphaFormat.Unpremul);
        using (var framebuffer = bitmap.Lock())
        {
            int rowBytes = checked(cropped.Width * 4);
            for (int y = 0; y < cropped.Height; y++)
            {
                Marshal.Copy(rgba, y * rowBytes, IntPtr.Add(framebuffer.Address, y * framebuffer.RowBytes), rowBytes);
            }
        }

        await using (var output = File.Create(path))
        {
            bitmap.Save(output);
            await output.FlushAsync(cancellationToken);
        }
        ShowNotification("裁剪图像已保存", path, InfoBarSeverity.Success);
    }

    public void SetCropRegion(int x, int y, int width, int height)
    {
        if (_currentScreenshot is null || width <= 0 || height <= 0)
        {
            SetCropRegion(null);
            return;
        }

        int left = Math.Clamp(x, 0, _currentScreenshot.Width - 1);
        int top = Math.Clamp(y, 0, _currentScreenshot.Height - 1);
        int right = Math.Clamp(x + width, left + 1, _currentScreenshot.Width);
        int bottom = Math.Clamp(y + height, top + 1, _currentScreenshot.Height);
        SetCropRegion(new ImageCropRegion(left, top, right - left, bottom - top));
    }

    private void SetCropRegion(ImageCropRegion? region)
    {
        _cropRegion = region;
        OnPropertyChanged(nameof(CropRegion));
        HasCropRegion = region is { Width: > 0, Height: > 0 };
        CropRegionText = region is { } crop
            ? $"X {crop.X}, Y {crop.Y} · {crop.Width} × {crop.Height} px"
            : "未选择裁剪区域";
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Avalonia binds this instance property as the ComboBox item source.")]
    public IReadOnlyList<TemplateMatchMethod> AvailableTemplateMatchMethods => TemplateMatchMethods;

    [RelayCommand]
    private async Task MatchTemplateAsync()
    {
        if (_coreService is null || _currentScreenshot is null || IsMatchingTemplate)
        {
            TemplateMatchResultText = _coreService is null
                ? "原生 Core 服务不可用。"
                : "请先捕获或导入截图。";
            return;
        }

        if (string.IsNullOrWhiteSpace(TemplateImagePath) || !File.Exists(TemplateImagePath))
        {
            TemplateMatchResultText = "请选择存在的模板图像文件。";
            return;
        }

        IsMatchingTemplate = true;
        try
        {
            if (!double.TryParse(TemplateMatchThreshold, NumberStyles.Float, CultureInfo.InvariantCulture, out double threshold)
                || threshold is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(TemplateMatchThreshold), "匹配阈值应介于 0 与 1 之间。");
            var screenshot = _currentScreenshot;
            string templatePath = TemplateImagePath;
            string activeTemplatePath = templatePath;
            var method = TemplateMatchMethod;
            var result = await _automationService.RunExclusiveAsync(async ct =>
            {
                await _automationService.InitializeAsync(ct);
                activeTemplatePath = _automationService.ResolveActiveResourcePath(templatePath);
                return await Task.Run(() =>
                {
                ct.ThrowIfCancellationRequested();
                var screen = RecognitionImageDecoder.DecodeBytes(screenshot.PngBytes)
                    ?? throw new InvalidDataException("当前截图无法解码。");
                var template = _coreService.LoadTemplateImage(activeTemplatePath)
                    ?? throw new InvalidDataException("原生 Core 无法加载模板图像。");
                return _coreService.MatchTemplate(screen, template, new TemplateMatchOptions(
                    method, null, threshold, screen.Width, screen.Height));
                }, ct);
            });
            TemplateImagePath = activeTemplatePath;
            TemplateMatchResultText = result.Found
                ? $"找到模板 · 得分 {result.Score:0.000} · X {result.X}, Y {result.Y} · {result.Width} × {result.Height} px"
                : $"未找到模板 · 最佳得分 {result.Score:0.000}，阈值 {threshold:0.000}";
            _logService.Log($"模板匹配：{TemplateMatchResultText}", result.Found ? LogEntryKind.Success : LogEntryKind.Normal);
        }
        catch (Exception ex)
        {
            TemplateMatchResultText = $"模板匹配失败：{ex.Message}";
            _logService.Log($"模板匹配失败: {ex}", LogEntryKind.Error);
        }
        finally
        {
            IsMatchingTemplate = false;
        }
    }

    [RelayCommand]
    private async Task CaptureScreenshotAsync()
    {
        if (!IsConnected || IsCapturing) return;

        IsCapturing = true;
        DismissNotification();
        try
        {
            var captured = await _automationService.CaptureScreenshotAsync();
            if (captured != null)
            {
                UpdateScreenshot(captured);
                ShowNotification("截屏成功", $"已成功从设备抓取画面 ({captured.Width}×{captured.Height})", InfoBarSeverity.Success);
            }
            else
            {
                ShowNotification("截屏失败", "设备返回了空画面数据，请检查 ADB 连接状态。", InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            ShowNotification("截屏异常", ex.Message, InfoBarSeverity.Error);
            _logService.Log($"截屏异常: {ex}", LogEntryKind.Error);
        }
        finally
        {
            IsCapturing = false;
        }
    }

    [RelayCommand]
    private async Task RunOcrAsync()
    {
        if (!HasScreenshot || IsRecognizing) return;

        IsRecognizing = true;
        DismissNotification();
        _logService.Log($"启动 OCR 文本识别 (语言: {OcrLanguage})...", LogEntryKind.Normal);

        try
        {
            // OCR also works on imported screenshots without connecting a device.
            await _automationService.InitializeAsync();
            var result = await _automationService.RecognizeOcrAsync(_currentScreenshot, OcrLanguage);
            if (result != null)
            {
                OcrDetections.Clear();
                OcrResultLanguageText = $"原生识别语言：{result.Language}";
                var textLines = new List<string>();

                foreach (var det in result.Detections)
                {
                    OcrDetections.Add(new OcrItemViewModel(
                        det.Text,
                        det.Bounds.X,
                        det.Bounds.Y,
                        det.Bounds.Width,
                        det.Bounds.Height,
                        det.Confidence));

                    textLines.Add(det.Text);
                }

                AllRecognizedText = string.Join(Environment.NewLine, textLines);
                DetectionCount = OcrDetections.Count;
                HasOcrDetections = DetectionCount > 0;

                ShowNotification("OCR 识别完毕", $"Core 实际使用 {result.Language} · 共检测到 {DetectionCount} 条文本行", InfoBarSeverity.Success);
                _logService.Log($"OCR 识别完毕，Core 实际语言 {result.Language}，检测到 {DetectionCount} 个文本元素", LogEntryKind.Success);
            }
            else
            {
                ShowNotification("未检测到文本", "OCR 引擎未在此画面中检测到可识别的文字。", InfoBarSeverity.Informational);
            }
        }
        catch (Exception ex)
        {
            ShowNotification("OCR 识别异常", ex.Message, InfoBarSeverity.Error);
            _logService.Log($"OCR 异常: {ex}", LogEntryKind.Error);
        }
        finally
        {
            IsRecognizing = false;
        }
    }

    [RelayCommand]
    private void ClearAll()
    {
        ScreenshotBitmap?.Dispose();
        ScreenshotBitmap = null;
        HasScreenshot = false;
        _currentScreenshot = null;
        SetCropRegion(null);
        ScreenshotDimensionsText = "-";
        ScreenshotTimestampText = "-";
        OcrDetections.Clear();
        AllRecognizedText = string.Empty;
        HasOcrDetections = false;
        DetectionCount = 0;
    }

    [RelayCommand]
    private void DismissNotification()
    {
        IsNotificationOpen = false;
    }

    private void ShowNotification(string title, string message, InfoBarSeverity severity)
    {
        NotificationTitle = title;
        NotificationMessage = message;
        NotificationSeverity = severity;
        IsNotificationOpen = true;
    }

    public void ShowError(string title, string message) =>
        ShowNotification(title, message, InfoBarSeverity.Error);
}

public readonly record struct ImageCropRegion(int X, int Y, int Width, int Height);
