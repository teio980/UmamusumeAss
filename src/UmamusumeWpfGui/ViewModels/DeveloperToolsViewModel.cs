using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.ViewModels;

public sealed class DeveloperToolsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IAdbRuntime _adbRuntime;
    private readonly IConnectionStateService _connectionState;
    private readonly SettingsViewModel _settingsViewModel;
    private AdbScreenshotResult? _screenshot;
    private BitmapSource? _screenshotImage;
    private Int32Rect? _cropRegion;
    private string _statusText = "Ready.";
    private string _captureDetails = string.Empty;
    private bool _isBusy;
    private bool _disposed;

    public DeveloperToolsViewModel(
        IAdbRuntime adbRuntime,
        IConnectionStateService connectionState,
        SettingsViewModel settingsViewModel)
    {
        ArgumentNullException.ThrowIfNull(adbRuntime);
        ArgumentNullException.ThrowIfNull(connectionState);
        ArgumentNullException.ThrowIfNull(settingsViewModel);

        _adbRuntime = adbRuntime;
        _connectionState = connectionState;
        _settingsViewModel = settingsViewModel;
        _connectionState.StateChanged += OnConnectionStateChanged;

        ConnectCommand = new RelayCommand(
            _ => _ = EnsureConnectedAsync(),
            _ => !_disposed && !_isBusy);
        CaptureCommand = new RelayCommand(
            _ => _ = CaptureScreenshotAsync(),
            _ => !_disposed && !_isBusy);
        SaveOriginalCommand = new RelayCommand(
            _ => SaveOriginal(),
            _ => !_disposed && HasScreenshot);
        SaveCroppedCommand = new RelayCommand(
            _ => SaveCropped(),
            _ => !_disposed && HasCropRegion);
        ClearCropCommand = new RelayCommand(
            _ => SetCropRegion(null),
            _ => !_disposed && HasCropRegion);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand ConnectCommand { get; }
    public ICommand CaptureCommand { get; }
    public ICommand SaveOriginalCommand { get; }
    public ICommand SaveCroppedCommand { get; }
    public ICommand ClearCropCommand { get; }

    public ConnectionState ConnectionState => _connectionState.State;

    public LastVerifiedConnection? LastVerifiedConnection =>
        _connectionState.LastVerifiedConnection;

    public string DeviceSummary => LastVerifiedConnection is { } connection
        ? $"{connection.Serial} · {connection.Width} × {connection.Height}"
        : "No verified emulator connection";

    public string DeviceSummaryDisplay => LastVerifiedConnection is { } connection
        ? $"{connection.Serial} | {connection.Width} x {connection.Height}"
        : "No verified emulator connection";

    public bool IsBusy => _isBusy;

    public bool HasScreenshot => _screenshotImage is not null;

    public bool HasCropRegion => _cropRegion is { Width: > 0, Height: > 0 };

    public BitmapSource? ScreenshotImage => _screenshotImage;

    public Int32Rect? CropRegion => _cropRegion;

    public string CropRegionText => _cropRegion is { } region
        ? $"{region.X}, {region.Y} · {region.Width} × {region.Height}"
        : "No crop selected";

    public string CropRegionTextDisplay => _cropRegion is { } region
        ? $"{region.X}, {region.Y} | {region.Width} x {region.Height}"
        : "No crop selected";

    public string CaptureDetailsDisplay => _screenshot is { } screenshot
        && _screenshotImage is { } image
        ? $"{image.PixelWidth} x {image.PixelHeight} | {screenshot.Method} | {screenshot.Duration.TotalMilliseconds:0} ms"
        : string.Empty;

    public string StatusText => _statusText;

    public string CaptureDetails => _captureDetails;

    public async Task EnsureConnectedAsync()
    {
        if (_disposed || _isBusy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            // Keep the Settings page as the single source of truth for connection setup.
            await _settingsViewModel.ConnectAsync().ConfigureAwait(true);
            if (ConnectionState == ConnectionState.Connected)
            {
                SetStatus("Connected to the emulator.");
            }
            else
            {
                SetStatus("Connection was not established. Check Settings for details.");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Connection failed: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    public async Task CaptureScreenshotAsync()
    {
        if (_disposed || _isBusy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            // Capture always invokes Settings.ConnectAsync first, including after a stale ADB session.
            await _settingsViewModel.ConnectAsync().ConfigureAwait(true);
            var connection = LastVerifiedConnection;
            if (ConnectionState != ConnectionState.Connected || connection is null)
            {
                SetStatus("Connect the emulator in Settings before capturing a screenshot.");
                return;
            }

            SetStatus("Capturing screenshot...");
            var capture = await _adbRuntime.CaptureBestScreenshotAsync(
                connection.AdbPath,
                connection.Serial).ConfigureAwait(true);
            if (!capture.Succeeded || capture.Screenshot is null)
            {
                SetStatus(DescribeCaptureFailure(capture));
                return;
            }

            var bitmap = ScreenshotBitmapCodec.ToBitmapSource(capture.Screenshot);
            if (bitmap is null)
            {
                SetStatus("The emulator returned an unsupported screenshot format.");
                return;
            }

            _screenshot = capture.Screenshot;
            _screenshotImage = bitmap;
            _cropRegion = null;
            _captureDetails =
                $"{bitmap.PixelWidth} × {bitmap.PixelHeight} · {capture.Screenshot.Method} · "
                + $"{capture.Screenshot.Duration.TotalMilliseconds:0} ms";
            SetStatus("Screenshot captured. Drag on the preview to select a crop region.");
            NotifyScreenshotPropertiesChanged();
        }
        catch (OperationCanceledException)
        {
            SetStatus("Screenshot capture canceled.");
        }
        catch (Exception exception)
        {
            SetStatus($"Screenshot capture failed: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    public void SetCropRegion(Int32Rect? region)
    {
        if (_screenshotImage is null || region is null)
        {
            _cropRegion = null;
        }
        else
        {
            var image = _screenshotImage;
            var x = Math.Clamp(region.Value.X, 0, image.PixelWidth);
            var y = Math.Clamp(region.Value.Y, 0, image.PixelHeight);
            var right = Math.Clamp(
                (long)region.Value.X + region.Value.Width,
                0,
                image.PixelWidth);
            var bottom = Math.Clamp(
                (long)region.Value.Y + region.Value.Height,
                0,
                image.PixelHeight);
            var width = (int)Math.Max(0, right - x);
            var height = (int)Math.Max(0, bottom - y);
            _cropRegion = width > 0 && height > 0
                ? new Int32Rect(x, y, width, height)
                : null;
        }

        OnPropertyChanged(nameof(CropRegion));
        OnPropertyChanged(nameof(CropRegionText));
        OnPropertyChanged(nameof(HasCropRegion));
        RaiseCommandStates();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connectionState.StateChanged -= OnConnectionStateChanged;
        RaiseCommandStates();
    }

    private void SaveOriginal()
    {
        if (_screenshotImage is null)
        {
            return;
        }

        var path = ShowSaveDialog("screenshot");
        if (path is null)
        {
            return;
        }

        try
        {
            ScreenshotBitmapCodec.SavePng(_screenshotImage, path);
            SetStatus($"Saved screenshot: {path}");
        }
        catch (Exception exception)
        {
            SetStatus($"Could not save screenshot: {exception.Message}");
        }
    }

    private void SaveCropped()
    {
        if (_screenshotImage is null || !HasCropRegion || _cropRegion is not { } region)
        {
            return;
        }

        var path = ShowSaveDialog("screenshot-crop");
        if (path is null)
        {
            return;
        }

        try
        {
            var cropped = new CroppedBitmap(_screenshotImage, region);
            cropped.Freeze();
            ScreenshotBitmapCodec.SavePng(cropped, path);
            SetStatus($"Saved crop: {path}");
        }
        catch (Exception exception)
        {
            SetStatus($"Could not save crop: {exception.Message}");
        }
    }

    private static string? ShowSaveDialog(string prefix)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "PNG image|*.png|All files|*.*",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string DescribeCaptureFailure(AdbScreenshotCaptureResult capture)
    {
        var failure = capture.Attempts.Count > 0
            ? capture.Attempts[^1]
            : null;
        if (failure is null)
        {
            return "Screenshot capture failed without a command result.";
        }

        var details = string.Join(
            " ",
            new[] { failure.Error?.Message, failure.Stderr.Trim() }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(details)
            ? $"Screenshot capture failed with exit code {failure.ExitCode}."
            : $"Screenshot capture failed: {details}";
    }

    private void OnConnectionStateChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ConnectionState));
        OnPropertyChanged(nameof(LastVerifiedConnection));
        OnPropertyChanged(nameof(DeviceSummary));
        OnPropertyChanged(nameof(DeviceSummaryDisplay));
        RaiseCommandStates();
    }

    private void SetStatus(string status)
    {
        _statusText = status;
        OnPropertyChanged(nameof(StatusText));
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        OnPropertyChanged(nameof(IsBusy));
        RaiseCommandStates();
    }

    private void NotifyScreenshotPropertiesChanged()
    {
        OnPropertyChanged(nameof(ScreenshotImage));
        OnPropertyChanged(nameof(HasScreenshot));
        OnPropertyChanged(nameof(CaptureDetailsDisplay));
        OnPropertyChanged(nameof(CropRegion));
        OnPropertyChanged(nameof(CropRegionText));
        OnPropertyChanged(nameof(CropRegionTextDisplay));
        OnPropertyChanged(nameof(HasCropRegion));
        OnPropertyChanged(nameof(CaptureDetails));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        if (ConnectCommand is RelayCommand connect)
        {
            connect.RaiseCanExecuteChanged();
        }

        if (CaptureCommand is RelayCommand capture)
        {
            capture.RaiseCanExecuteChanged();
        }

        if (SaveOriginalCommand is RelayCommand saveOriginal)
        {
            saveOriginal.RaiseCanExecuteChanged();
        }

        if (SaveCroppedCommand is RelayCommand saveCropped)
        {
            saveCropped.RaiseCanExecuteChanged();
        }

        if (ClearCropCommand is RelayCommand clearCrop)
        {
            clearCrop.RaiseCanExecuteChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?> _canExecute;

        public RelayCommand(Action<object?> execute, Predicate<object?> canExecute)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute(parameter);

        public void Execute(object? parameter) => _execute(parameter);

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
