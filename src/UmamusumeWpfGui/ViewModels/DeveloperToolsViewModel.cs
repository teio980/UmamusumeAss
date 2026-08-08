using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
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
    private readonly IUmaDatabaseService _umaDatabase;
    private readonly ObservableCollection<DeveloperToolsImageItem> _existingImages = [];
    private AdbScreenshotResult? _screenshot;
    private BitmapSource? _screenshotImage;
    private Int32Rect? _cropRegion;
    private DeveloperToolsImageItem? _selectedUmaImage;
    private string? _activeImagePath;
    private string _statusText = "Ready.";
    private string _captureDetails = string.Empty;
    private bool _isBusy;
    private bool _isLoadingImages;
    private bool _isSavingImage;
    private bool _disposed;

    public DeveloperToolsViewModel(
        IAdbRuntime adbRuntime,
        IConnectionStateService connectionState,
        SettingsViewModel settingsViewModel,
        IUmaDatabaseService umaDatabase)
    {
        ArgumentNullException.ThrowIfNull(adbRuntime);
        ArgumentNullException.ThrowIfNull(connectionState);
        ArgumentNullException.ThrowIfNull(settingsViewModel);
        ArgumentNullException.ThrowIfNull(umaDatabase);

        _adbRuntime = adbRuntime;
        _connectionState = connectionState;
        _settingsViewModel = settingsViewModel;
        _umaDatabase = umaDatabase;
        ExistingImages = new ReadOnlyObservableCollection<DeveloperToolsImageItem>(_existingImages);
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
        RefreshExistingImagesCommand = new RelayCommand(
            _ => _ = RefreshExistingImagesAsync(),
            _ => !_disposed && !_isLoadingImages);
        SaveSelectedImageCommand = new RelayCommand(
            _ => _ = SaveSelectedImageAsync(),
            _ => !_disposed
                && HasSelectedImage
                && HasCropRegion
                && !_isBusy
                && !_isLoadingImages
                && !_isSavingImage);

        _ = RefreshExistingImagesAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand ConnectCommand { get; }
    public ICommand CaptureCommand { get; }
    public ICommand SaveOriginalCommand { get; }
    public ICommand SaveCroppedCommand { get; }
    public ICommand ClearCropCommand { get; }
    public ICommand RefreshExistingImagesCommand { get; }
    public ICommand SaveSelectedImageCommand { get; }

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

    public bool HasSelectedImage => _selectedUmaImage is not null
        && !string.IsNullOrWhiteSpace(_activeImagePath);

    public bool IsLoadingImages => _isLoadingImages;

    public ReadOnlyObservableCollection<DeveloperToolsImageItem> ExistingImages { get; }

    public DeveloperToolsImageItem? SelectedUmaImage
    {
        get => _selectedUmaImage;
        set
        {
            if (ReferenceEquals(_selectedUmaImage, value))
            {
                return;
            }

            _selectedUmaImage = value;
            OnPropertyChanged();
            LoadExistingImage(value);
        }
    }

    public string ExistingImageCountDisplay =>
        $"{_existingImages.Count} image(s)";

    public string SelectedImagePathDisplay => _activeImagePath ?? string.Empty;

    public string SelectedReferenceImagePathDisplay => _selectedUmaImage is { } image
        ? _umaDatabase.GetTraineeReferenceImagePath(image.TraineeId)
        : string.Empty;

    public BitmapSource? ScreenshotImage => _screenshotImage;

    public Int32Rect? CropRegion => _cropRegion;

    public string CropRegionText => _cropRegion is { } region
        ? $"{region.X}, {region.Y} · {region.Width} × {region.Height}"
        : "No crop selected";

    public string CropRegionTextDisplay => _cropRegion is { } region
        ? $"{region.X}, {region.Y} | {region.Width} x {region.Height}"
        : "No crop selected";

    public string CaptureDetailsDisplay => _screenshotImage is not { } image
        ? string.Empty
        : _screenshot is { } screenshot
            ? $"{image.PixelWidth} x {image.PixelHeight} | {screenshot.Method} | {screenshot.Duration.TotalMilliseconds:0} ms"
            : $"{image.PixelWidth} x {image.PixelHeight} | existing image";

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
            if (_selectedUmaImage is null)
            {
                _activeImagePath = null;
            }
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

    public async Task RefreshExistingImagesAsync()
    {
        if (_disposed || _isLoadingImages)
        {
            return;
        }

        _isLoadingImages = true;
        OnPropertyChanged(nameof(IsLoadingImages));
        RaiseCommandStates();

        var selectedId = _selectedUmaImage?.TraineeId;
        try
        {
            var records = _umaDatabase.Trainees
                .ToDictionary(record => record.TraineeId);
            var directory = _umaDatabase.GetTraineeImageDirectory();
            var candidates = await Task.Run(() =>
            {
                if (!Directory.Exists(directory))
                {
                    return Array.Empty<DeveloperToolsImageItem>();
                }

                return Directory.EnumerateFiles(directory)
                    .Where(IsSupportedImagePath)
                    .Select(path => CreateImageItem(path, records))
                    .Where(item => item is not null)
                    .Select(item => item!)
                    .OrderBy(item => item.TraineeId)
                    .ToArray();
            }).ConfigureAwait(true);

            _existingImages.Clear();
            foreach (var item in candidates)
            {
                _existingImages.Add(item);
            }

            OnPropertyChanged(nameof(ExistingImageCountDisplay));
            var selected = selectedId is { } id
                ? _existingImages.FirstOrDefault(item => item.TraineeId == id)
                : null;
            SelectedUmaImage = selected;
            if (selected is null && _screenshot is null)
            {
                _screenshotImage = null;
                _activeImagePath = null;
                _cropRegion = null;
                NotifyScreenshotPropertiesChanged();
            }

            SetStatus(candidates.Length == 0
                ? "No existing Uma images were found."
                : $"Loaded {candidates.Length} existing Uma image(s).");
        }
        catch (Exception exception)
        {
            SetStatus($"Could not load existing Uma images: {exception.Message}");
        }
        finally
        {
            _isLoadingImages = false;
            OnPropertyChanged(nameof(IsLoadingImages));
            RaiseCommandStates();
        }
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

    private async Task SaveSelectedImageAsync()
    {
        if (_screenshotImage is null
            || !HasCropRegion
            || _cropRegion is not { } region
            || string.IsNullOrWhiteSpace(_activeImagePath)
            || !File.Exists(_activeImagePath))
        {
            return;
        }

        if (_isSavingImage)
        {
            return;
        }

        var sourcePath = _activeImagePath!;
        var referencePath = _umaDatabase.GetTraineeReferenceImagePath(
            _selectedUmaImage!.TraineeId);
        var backupPath = CreateBackupPath(referencePath);
        var temporaryPath = referencePath + $".{Guid.NewGuid():N}.tmp";
        _isSavingImage = true;
        RaiseCommandStates();
        try
        {
            var cropped = new CroppedBitmap(_screenshotImage, region);
            cropped.Freeze();
            if (File.Exists(referencePath))
            {
                File.Copy(referencePath, backupPath);
            }
            else
            {
                // The unmodified full image is the effective reference until
                // the first developer crop is saved.
                File.Copy(sourcePath, backupPath);
            }

            UmaImageCodec.Save(cropped, temporaryPath);
            File.Move(temporaryPath, referencePath, overwrite: true);

            await RefreshExistingImagesAsync().ConfigureAwait(true);
            SetCropRegion(null);
            OnPropertyChanged(nameof(SelectedReferenceImagePathDisplay));
            SetStatus($"Saved system reference image to {referencePath}. Backup: {backupPath}");
        }
        catch (Exception exception)
        {
            SetStatus($"Could not replace image: {exception.Message}");
        }
        finally
        {
            TryDelete(temporaryPath);
            _isSavingImage = false;
            RaiseCommandStates();
        }
    }

    private void LoadExistingImage(DeveloperToolsImageItem? item)
    {
        if (item is null)
        {
            _activeImagePath = null;
            if (_screenshot is null)
            {
                _screenshotImage = null;
                _cropRegion = null;
                NotifyScreenshotPropertiesChanged();
            }

            OnPropertyChanged(nameof(HasSelectedImage));
            OnPropertyChanged(nameof(SelectedImagePathDisplay));
            OnPropertyChanged(nameof(SelectedReferenceImagePathDisplay));
            RaiseCommandStates();
            return;
        }

        try
        {
            var hasCapturedScreenshot = _screenshot is not null;
            if (!hasCapturedScreenshot)
            {
                _screenshotImage = UmaImageCodec.Load(item.Path);
            }

            _activeImagePath = item.Path;
            if (!hasCapturedScreenshot)
            {
                _cropRegion = null;
            }
            if (_screenshotImage is not null)
            {
                _captureDetails = hasCapturedScreenshot
                    ? _captureDetails
                    : $"{_screenshotImage.PixelWidth} x {_screenshotImage.PixelHeight} | existing image";
            }

            SetStatus(hasCapturedScreenshot
                ? $"Loaded {item.DisplayName} as the target. The captured screenshot is ready to crop."
                : $"Loaded {item.DisplayName}. Drag on the preview to select a crop region.");
            NotifyScreenshotPropertiesChanged();
            OnPropertyChanged(nameof(HasSelectedImage));
            OnPropertyChanged(nameof(SelectedImagePathDisplay));
            OnPropertyChanged(nameof(SelectedReferenceImagePathDisplay));
        }
        catch (Exception exception)
        {
            _activeImagePath = null;
            _screenshotImage = null;
            _cropRegion = null;
            SetStatus($"Could not load {item.DisplayName}: {exception.Message}");
            NotifyScreenshotPropertiesChanged();
            OnPropertyChanged(nameof(HasSelectedImage));
            OnPropertyChanged(nameof(SelectedImagePathDisplay));
            OnPropertyChanged(nameof(SelectedReferenceImagePathDisplay));
        }
    }

    private static DeveloperToolsImageItem? CreateImageItem(
        string path,
        Dictionary<int, UmaTraineeRecord> records)
    {
        if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out var traineeId))
        {
            return null;
        }

        var name = records.TryGetValue(traineeId, out var record)
            ? record.NameEn
            : traineeId.ToString(CultureInfo.InvariantCulture);
        var thumbnail = UmaImageCodec.Load(path, maxDimension: 96);
        return new DeveloperToolsImageItem(traineeId, name, path, thumbnail);
    }

    private static bool IsSupportedImagePath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".webp" or ".png" or ".jpg" or ".jpeg";

    private static string CreateBackupPath(string sourcePath)
    {
        var directory = Path.Combine(
            Path.GetDirectoryName(sourcePath) ?? AppContext.BaseDirectory,
            "backup");
        Directory.CreateDirectory(directory);
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(directory, $"{stem}_{timestamp}{extension}");
        var suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{stem}_{timestamp}_{suffix++}{extension}");
        }

        return candidate;
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
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
        OnPropertyChanged(nameof(HasSelectedImage));
        OnPropertyChanged(nameof(SelectedImagePathDisplay));
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

        if (RefreshExistingImagesCommand is RelayCommand refreshImages)
        {
            refreshImages.RaiseCanExecuteChanged();
        }

        if (SaveSelectedImageCommand is RelayCommand saveSelectedImage)
        {
            saveSelectedImage.RaiseCanExecuteChanged();
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
