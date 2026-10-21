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
using Umamusume.Avalonia.Services;

namespace Umamusume.Avalonia.ViewModels;

public sealed partial class ScreenOcrViewModel : ViewModelBase
{
    private readonly IUmaAutomationService _automationService;
    private readonly ILogService _logService;
    private readonly IConnectionStateService _connectionStateService;
    private readonly ILocalizationService _localizationService;
    private readonly IUmaService? _coreService;
    private CapturedScreenshot? _currentScreenshot;
    private ImageCropRegion? _cropRegion;
    private string? _recognizedLanguage;

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
    private string _templateMatchResultText = "Select a template image to test the current screenshot.";

    [ObservableProperty]
    private string _cropRegionText = "No crop selected";

    [ObservableProperty]
    private bool _hasCropRegion;

    [ObservableProperty]
    private string _ocrLanguage = "eng";

    [ObservableProperty]
    private string _ocrResultLanguageText = "Native OCR resources currently support eng only.";

    [ObservableProperty]
    private string _allRecognizedText = string.Empty;

    [ObservableProperty]
    private bool _hasOcrDetections;

    [ObservableProperty]
    private int _detectionCount;

    public string DetectionCountText => $"{DetectionCount} {T("项", "items")}";

    partial void OnDetectionCountChanged(int value) => OnPropertyChanged(nameof(DetectionCountText));

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
        ILocalizationService localizationService,
        IUmaService? coreService = null)
    {
        _automationService = automationService;
        _logService = logService;
        _connectionStateService = connectionStateService;
        _localizationService = localizationService;
        _coreService = coreService;
        _localizationService.LanguageChanged += OnLanguageChanged;
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
                ScreenshotDimensionsText = $"{captured.Width} × {captured.Height} {T("像素", "pixels")}";
                ScreenshotTimestampText = captured.CapturedAt.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                _currentScreenshot = captured;
                HasScreenshot = true;
                SetCropRegion(null);
                OcrDetections.Clear();
                _recognizedLanguage = null;
                OcrResultLanguageText = T("当前原生 OCR 资源仅支持 eng。", "Native OCR resources currently support eng only.");
                AllRecognizedText = string.Empty;
                HasOcrDetections = false;
                DetectionCount = 0;
            }
        }
        catch (Exception ex)
        {
            _logService.Log($"{T("解码截图图像位图失败", "Failed to decode screenshot bitmap")}: {ex.Message}", LogEntryKind.Error);
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
                ShowNotification(T("导入失败", "Import failed"), T("无法解码此图像文件。", "This image file could not be decoded."), InfoBarSeverity.Error);
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
            ShowNotification(T("图像已导入", "Image imported"), $"{Path.GetFileName(path)} ({decoded.Width}×{decoded.Height})", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowNotification(T("导入截图失败", "Failed to import screenshot"), ex.Message, InfoBarSeverity.Error);
            _logService.Log($"{T("导入截图失败", "Failed to import screenshot")}: {ex}", LogEntryKind.Error);
        }
    }

    public async Task ExportScreenshotAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_currentScreenshot is null)
            throw new InvalidOperationException(T("当前没有可导出的截图。", "There is no screenshot to export."));

        await File.WriteAllBytesAsync(path, _currentScreenshot.PngBytes, cancellationToken);
        ShowNotification(T("截图已导出", "Screenshot exported"), path, InfoBarSeverity.Success);
    }

    public async Task SaveCropAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_currentScreenshot is null || _cropRegion is not { } crop)
            throw new InvalidOperationException(T("请先选择一个有效的截图裁剪区域。", "Select a valid screenshot crop region first."));

        cancellationToken.ThrowIfCancellationRequested();
        var source = RecognitionImageDecoder.DecodeBytes(_currentScreenshot.PngBytes)
            ?? throw new InvalidDataException(T("当前截图无法解码。", "The current screenshot could not be decoded."));
        var cropped = RecognitionImageDecoder.Transform(source, new RecognitionImageTransform(
            crop.X, crop.Y, crop.Width, crop.Height));
        if (cropped.RgbaPixels is not { } rgba || rgba.Length != checked(cropped.Width * cropped.Height * 4))
            throw new InvalidDataException(T("裁剪图像没有可保存的 RGBA 像素数据。", "The cropped image has no RGBA pixel data to save."));

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
        ShowNotification(T("裁剪图像已保存", "Crop saved"), path, InfoBarSeverity.Success);
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
            : T("未选择裁剪区域", "No crop selected");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Avalonia binds this instance property as the ComboBox item source.")]
    public IReadOnlyList<TemplateMatchMethod> AvailableTemplateMatchMethods => TemplateMatchMethods;

    [RelayCommand]
    private async Task MatchTemplateAsync()
    {
        if (_coreService is null || _currentScreenshot is null || IsMatchingTemplate)
        {
            TemplateMatchResultText = _coreService is null
                ? T("原生 Core 服务不可用。", "The native Core service is unavailable.")
                : T("请先捕获或导入截图。", "Capture or import a screenshot first.");
            return;
        }

        if (string.IsNullOrWhiteSpace(TemplateImagePath) || !File.Exists(TemplateImagePath))
        {
            TemplateMatchResultText = T("请选择存在的模板图像文件。", "Select an existing template image file.");
            return;
        }

        IsMatchingTemplate = true;
        try
        {
            if (!double.TryParse(TemplateMatchThreshold, NumberStyles.Float, CultureInfo.InvariantCulture, out double threshold)
                || threshold is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(TemplateMatchThreshold), T("匹配阈值应介于 0 与 1 之间。", "The match threshold must be between 0 and 1."));
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
                    ?? throw new InvalidDataException(T("当前截图无法解码。", "The current screenshot could not be decoded."));
                var template = _coreService.LoadTemplateImage(activeTemplatePath)
                    ?? throw new InvalidDataException(T("原生 Core 无法加载模板图像。", "Native Core could not load the template image."));
                return _coreService.MatchTemplate(screen, template, new TemplateMatchOptions(
                    method, null, threshold, screen.Width, screen.Height));
                }, ct);
            });
            TemplateImagePath = activeTemplatePath;
            TemplateMatchResultText = result.Found
                ? $"{T("找到模板", "Template found")} · Score {result.Score:0.000} · X {result.X}, Y {result.Y} · {result.Width} × {result.Height} px"
                : $"{T("未找到模板", "Template not found")} · Best score {result.Score:0.000} · Threshold {threshold:0.000}";
            _logService.Log($"{T("模板匹配", "Template match")}: {TemplateMatchResultText}", result.Found ? LogEntryKind.Success : LogEntryKind.Normal);
        }
        catch (Exception ex)
        {
            TemplateMatchResultText = $"{T("模板匹配失败", "Template match failed")}: {ex.Message}";
            _logService.Log($"{T("模板匹配失败", "Template match failed")}: {ex}", LogEntryKind.Error);
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
                ShowNotification(T("截屏成功", "Screenshot captured"), $"{T("已成功从设备抓取画面", "Captured from device")} ({captured.Width}×{captured.Height})", InfoBarSeverity.Success);
            }
            else
            {
                ShowNotification(T("截屏失败", "Screenshot failed"), T("设备返回了空画面数据，请检查 ADB 连接状态。", "The device returned an empty frame. Check the ADB connection."), InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            ShowNotification(T("截屏异常", "Screenshot error"), ex.Message, InfoBarSeverity.Error);
            _logService.Log($"{T("截屏异常", "Screenshot error")}: {ex}", LogEntryKind.Error);
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
        _logService.Log($"{T("启动 OCR 文本识别", "Starting OCR recognition")} (language: {OcrLanguage})...", LogEntryKind.Normal);

        try
        {
            // OCR also works on imported screenshots without connecting a device.
            await _automationService.InitializeAsync();
            var result = await _automationService.RecognizeOcrAsync(_currentScreenshot, OcrLanguage);
            if (result != null)
            {
                OcrDetections.Clear();
                _recognizedLanguage = result.Language;
                OcrResultLanguageText = $"{T("原生识别语言", "Native OCR language")}: {result.Language}";
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

                ShowNotification(T("OCR 识别完毕", "OCR complete"), $"Core used {result.Language} · {DetectionCount} text line(s) detected", InfoBarSeverity.Success);
                _logService.Log($"OCR complete · Core language {result.Language} · {DetectionCount} text element(s) detected", LogEntryKind.Success);
            }
            else
            {
                ShowNotification(T("未检测到文本", "No text detected"), T("OCR 引擎未在此画面中检测到可识别的文字。", "The OCR engine did not find readable text in this image."), InfoBarSeverity.Informational);
            }
        }
        catch (Exception ex)
        {
            ShowNotification(T("OCR 识别异常", "OCR error"), ex.Message, InfoBarSeverity.Error);
            _logService.Log($"OCR error: {ex}", LogEntryKind.Error);
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

    private string T(string chinese, string english) => UiText.Get(_localizationService, chinese, english);

    public string Localize(string chinese, string english) => T(chinese, english);

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(DetectionCountText));
        if (_currentScreenshot is { } screenshot)
            ScreenshotDimensionsText = $"{screenshot.Width} × {screenshot.Height} {T("像素", "pixels")}";
        _recognizedLanguage = null;
        OcrResultLanguageText = T("当前原生 OCR 资源仅支持 eng。", "Native OCR resources currently support eng only.");
        TemplateMatchResultText = T("选择模板图像后可测试当前截图。", "Select a template image to test the current screenshot.");
        SetCropRegion(_cropRegion);
        DismissNotification();
    }

    public void ShowError(string title, string message) =>
        ShowNotification(title, message, InfoBarSeverity.Error);
}

public readonly record struct ImageCropRegion(int X, int Y, int Width, int Height);
