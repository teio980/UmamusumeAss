using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Umamusume.AppCore.Models;
using Umamusume.AppCore.Services;
using Umamusume.Avalonia.Services;
using Umamusume.CoreBridge;

namespace Umamusume.Avalonia.ViewModels;

public sealed partial class DeveloperToolsViewModel : ViewModelBase, IDisposable
{
    private const int MaxHistoryEntries = 100;
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };
    private readonly IUmaAutomationService _automationService;
    private readonly ILogService _logService;
    private readonly IConnectionStateService _connectionStateService;
    private readonly DeveloperToolsService _tools;
    private readonly Stack<string> _pipelineUndo = new();
    private readonly Stack<string> _pipelineRedo = new();
    private CapturedScreenshot? _screenshot;
    private ImageCropRegion? _cropRegion;
    private string _pipelineHistoryBaseline = string.Empty;
    private string _pipelineSavedText = string.Empty;
    private DateTime _lastPipelineEditAt = DateTime.MinValue;
    private bool _restoringPipelineHistory;
    private bool _loadingPipeline;
    private bool _refreshingResources;
    private bool _refreshingResourcesForNativeOperation;
    private bool _resourcesInitialized;
    private bool _restoringScenarioText;
    private CancellationTokenSource? _pipelineCancellation;
    private readonly Dictionary<string, string> _scenarioDrafts = new(StringComparer.OrdinalIgnoreCase);
    private string _resourceRoot = string.Empty;
    private string? _galleryCropSourcePath;
    private string? _pipelineTemplateEditPath;
    private string? _pipelineTemplateEditTask;
    private bool _isSavingSystemReference;
    private bool _isTestingSystemReference;

    public ObservableCollection<DeveloperResourceFile> PipelineFiles { get; } = [];
    public ObservableCollection<DeveloperResourceFile> ScenarioManifests { get; } = [];
    public ObservableCollection<DeveloperResourceFile> ScenarioPackageFiles { get; } = [];
    public ObservableCollection<DeveloperResourceFile> TemplateImages { get; } = [];
    public ObservableCollection<DeveloperResourceFile> TemplateDirectories { get; } = [];
    public ObservableCollection<DeveloperResourceFile> ReplayFixtures { get; } = [];
    public ObservableCollection<string> PipelineTaskNames { get; } = [];
    public ObservableCollection<UmaDatabaseImageItem> UmaDatabaseImages { get; } = [];
    public ObservableCollection<UmaDatabaseImageItem> FilteredUmaDatabaseImages { get; } = [];

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private Bitmap? _screenshotBitmap;
    [ObservableProperty] private string _screenshotInfoText = "先捕获设备画面或导入图片。";
    [ObservableProperty] private bool _hasScreenshot;
    [ObservableProperty] private bool _hasCropRegion;
    [ObservableProperty] private string _cropRegionText = "未选择裁剪区域";
    [ObservableProperty] private string _templatePath = string.Empty;
    [ObservableProperty] private double _matchThreshold = 0.86;
    [ObservableProperty] private TemplateMatchMethod _matchMethod = TemplateMatchMethod.Gray;
    [ObservableProperty] private string _imageMatchStatus = "选择模板图像并捕获截图以运行原生匹配。";
    [ObservableProperty] private bool _isMatchingImage;
    [ObservableProperty] private string _resourceStatusText;
    [ObservableProperty] private DeveloperResourceFile? _selectedTemplateImage;
    [ObservableProperty] private Bitmap? _templatePreviewBitmap;
    [ObservableProperty] private DeveloperResourceFile? _selectedTemplateDirectory;
    [ObservableProperty] private DeveloperResourceFile? _selectedPipelineFile;
    [ObservableProperty] private string _selectedPipelineTaskName = string.Empty;
    [ObservableProperty] private string _pipelineGraphText = "Load a pipeline to preview task transitions.";
    [ObservableProperty] private string _replayOutputText = "No offline replay has run.";
    [ObservableProperty] private DeveloperResourceFile? _selectedReplayFixture;
    [ObservableProperty] private string _pipelineJsonText = string.Empty;
    [ObservableProperty] private string _pipelineStatusText = "选择或创建一个 Pipeline JSON。";
    [ObservableProperty] private string _pipelineEntryTask = string.Empty;
    [ObservableProperty] private string _pipelineRunStatusText = "尚未运行。";
    [ObservableProperty] private bool _isPipelineRunning;
    [ObservableProperty] private bool _isPipelineBusy;
    [ObservableProperty] private bool _isPipelineDirty;
    [ObservableProperty] private DeveloperResourceFile? _selectedScenarioManifest;
    [ObservableProperty] private DeveloperResourceFile? _selectedScenarioFile;
    [ObservableProperty] private string _scenarioJsonText = string.Empty;
    [ObservableProperty] private string _scenarioStatusText = "选择一个场景包清单以查看其 JSON 文件。";
    [ObservableProperty] private string _scenarioInspectionText = "Native Inspector has not run.";
    [ObservableProperty] private bool _isScenarioBusy;
    [ObservableProperty] private string _umaImageSearchText = string.Empty;
    [ObservableProperty] private UmaDatabaseImageItem? _selectedUmaImage;
    [ObservableProperty] private Bitmap? _selectedUmaImagePreviewBitmap;
    [ObservableProperty] private string _umaImageGalleryStatus = "正在读取 UmaDatabase 图像资源。";
    [ObservableProperty] private string _systemReferenceStatus = "选择角色图像后，可将当前截图裁剪保存为 system reference。";

    public IReadOnlyList<TemplateMatchMethod> AvailableMatchMethods { get; } = Enum.GetValues<TemplateMatchMethod>();
    public ImageCropRegion? CropRegion => _cropRegion;
    public bool CanUndoPipeline => _pipelineUndo.Count > 0;
    public bool CanRedoPipeline => _pipelineRedo.Count > 0;
    public bool CanRunPipeline => IsConnected && !IsPipelineRunning && !IsPipelineBusy && !IsPipelineDirty && SelectedPipelineFile is not null;
    public bool CanSavePipeline => !IsPipelineBusy && SelectedPipelineFile is not null && !string.IsNullOrWhiteSpace(PipelineJsonText);
    public bool HasSelectedUmaImage => SelectedUmaImage is not null;
    public bool CanSaveSystemReference => !_isSavingSystemReference && SelectedUmaImage is not null && HasScreenshot && HasCropRegion;
    public bool CanTestSystemReferenceMatch => !_isTestingSystemReference && SelectedUmaImage is not null && HasScreenshot
        && File.Exists(_tools.ResolveResourcePath(SelectedUmaImage.ReferencePath));
    public bool CanSavePipelineTemplate => SelectedPipelineFile is not null && !string.IsNullOrWhiteSpace(SelectedPipelineTaskName)
        && HasScreenshot && HasCropRegion && !IsPipelineBusy;
    public bool CanEditPipelineTemplate => SelectedPipelineFile is not null && !string.IsNullOrWhiteSpace(SelectedPipelineTaskName)
        && GetSelectedPipelineTemplatePath() is { } path && File.Exists(path) && !IsPipelineBusy;
    public string PipelineTemplateSaveLabel => _pipelineTemplateEditPath is null ? "保存裁剪为节点模板" : "替换节点模板（保留 ROI）";

    public DeveloperToolsViewModel(
        IUmaAutomationService automationService,
        IUmaService coreService,
        ILogService logService,
        IConnectionStateService connectionStateService)
    {
        ArgumentNullException.ThrowIfNull(automationService);
        ArgumentNullException.ThrowIfNull(coreService);
        ArgumentNullException.ThrowIfNull(logService);
        ArgumentNullException.ThrowIfNull(connectionStateService);
        _automationService = automationService;
        _logService = logService;
        _connectionStateService = connectionStateService;
        _tools = new DeveloperToolsService(automationService, coreService);
        _resourceRoot = _tools.ResourceRoot;
        _resourceStatusText = $"Resource: {_tools.ResourceRoot}";
        IsConnected = connectionStateService.State == ConnectionState.Connected;
        _connectionStateService.StateChanged += OnConnectionStateChanged;
        _automationService.ScreenshotCaptured += OnScreenshotCaptured;
        _automationService.ResourcesChanged += OnResourcesChanged;
        RefreshResources();
    }

    partial void OnUmaImageSearchTextChanged(string value) => FilterUmaDatabaseImages();

    partial void OnSelectedUmaImageChanged(UmaDatabaseImageItem? value)
    {
        OnPropertyChanged(nameof(HasSelectedUmaImage));
        SelectedUmaImagePreviewBitmap?.Dispose();
        SelectedUmaImagePreviewBitmap = null;
        try
        {
            if (value is not null)
            {
                SelectedUmaImagePreviewBitmap = DeveloperToolsImageCodec.LoadBitmap(value.ImagePath, 360);
                // Preserve a real screenshot when one is present. When the gallery is the only
                // source, load the selected database image into the crop canvas, as the WPF page does.
                if (_screenshot is null || _galleryCropSourcePath is not null)
                {
                    SetScreenshot(DeveloperToolsImageCodec.LoadAsScreenshot(value.ImagePath));
                    _galleryCropSourcePath = value.ImagePath;
                }
                UmaImageGalleryStatus = $"已选择 {value.DisplayName} · reference: {value.ReferencePath}";
            }
            else
            {
                UmaImageGalleryStatus = $"{UmaDatabaseImages.Count} 张图像 · 选择一张以查看预览。";
            }
        }
        catch (Exception ex)
        {
            UmaImageGalleryStatus = $"无法读取所选图像：{ex.Message}";
            _logService.Log($"UmaDatabase 图像预览失败: {ex}", LogEntryKind.Warning);
        }
        NotifyImageCommands();
    }

    partial void OnSelectedPipelineTaskNameChanged(string value)
    {
        if (_pipelineTemplateEditTask is not null && !string.Equals(_pipelineTemplateEditTask, value, StringComparison.Ordinal))
        {
            _pipelineTemplateEditPath = null;
            _pipelineTemplateEditTask = null;
            OnPropertyChanged(nameof(PipelineTemplateSaveLabel));
        }
        OnPropertyChanged(nameof(CanSavePipelineTemplate));
        OnPropertyChanged(nameof(CanEditPipelineTemplate));
    }

    partial void OnSelectedPipelineFileChanged(DeveloperResourceFile? value)
    {
        _pipelineTemplateEditPath = null;
        _pipelineTemplateEditTask = null;
        OnPropertyChanged(nameof(PipelineTemplateSaveLabel));
        OnPropertyChanged(nameof(CanSavePipelineTemplate));
        OnPropertyChanged(nameof(CanEditPipelineTemplate));
        if (value is null || _loadingPipeline) return;
        _ = LoadPipelineAsync(value.Path);
    }

    partial void OnSelectedScenarioManifestChanged(DeveloperResourceFile? value)
    {
        if (_refreshingResources) return;
        _ = LoadScenarioManifestAsync(value);
    }

    partial void OnSelectedScenarioFileChanged(DeveloperResourceFile? value)
    {
        if (value is null)
        {
            _restoringScenarioText = true;
            ScenarioJsonText = string.Empty;
            _restoringScenarioText = false;
            return;
        }
        _restoringScenarioText = true;
        try
        {
            ScenarioJsonText = _scenarioDrafts.TryGetValue(value.Path, out string? draft)
                ? draft
                : DeveloperToolsService.ReadText(value.Path);
            ScenarioStatusText = $"已加载 {value.RelativePath}";
        }
        catch (Exception ex)
        {
            ScenarioJsonText = string.Empty;
            ScenarioStatusText = $"读取失败：{ex.Message}";
        }
        finally
        {
            _restoringScenarioText = false;
        }
    }

    partial void OnScenarioJsonTextChanged(string value)
    {
        if (!_restoringScenarioText && SelectedScenarioFile is { } file)
            _scenarioDrafts[file.Path] = value;
    }

    partial void OnSelectedTemplateImageChanged(DeveloperResourceFile? value)
    {
        if (value is null) return;
        TemplatePath = value.Path;
        try
        {
            TemplatePreviewBitmap?.Dispose();
            TemplatePreviewBitmap = new Bitmap(value.Path);
        }
        catch (Exception ex)
        {
            ImageMatchStatus = $"模板预览失败：{ex.Message}";
        }
    }

    partial void OnPipelineJsonTextChanged(string value)
    {
        if (!_restoringPipelineHistory)
        {
            var now = DateTime.UtcNow;
            if (_pipelineHistoryBaseline != value
                && now - _lastPipelineEditAt > TimeSpan.FromMilliseconds(500))
            {
                PushHistory(_pipelineUndo, _pipelineHistoryBaseline);
                _pipelineRedo.Clear();
                _pipelineHistoryBaseline = value;
                _lastPipelineEditAt = now;
                NotifyHistory();
            }
            else
            {
                _lastPipelineEditAt = now;
            }

            IsPipelineDirty = !string.Equals(value, _pipelineSavedText, StringComparison.Ordinal);
            RefreshPipelineDetails(value);
        }
        OnPropertyChanged(nameof(CanRunPipeline));
        OnPropertyChanged(nameof(CanSavePipeline));
        OnPropertyChanged(nameof(CanSavePipelineTemplate));
        OnPropertyChanged(nameof(CanEditPipelineTemplate));
    }

    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(CanRunPipeline));
    partial void OnIsPipelineRunningChanged(bool value) => OnPropertyChanged(nameof(CanRunPipeline));
    partial void OnIsPipelineBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRunPipeline));
        OnPropertyChanged(nameof(CanSavePipeline));
        OnPropertyChanged(nameof(CanSavePipelineTemplate));
        OnPropertyChanged(nameof(CanEditPipelineTemplate));
    }
    partial void OnIsPipelineDirtyChanged(bool value) => OnPropertyChanged(nameof(CanRunPipeline));

    [RelayCommand]
    private void RefreshResources()
    {
        try
        {
            string? currentPipeline = ResolveOptionalResourcePath(SelectedPipelineFile?.Path);
            string? currentManifest = ResolveOptionalResourcePath(SelectedScenarioManifest?.Path);
            string? currentTemplate = ResolveOptionalResourcePath(
                !string.IsNullOrWhiteSpace(TemplatePath) ? TemplatePath : SelectedTemplateImage?.Path);
            string? currentTemplateDirectory = ResolveOptionalResourcePath(SelectedTemplateDirectory?.Path);
            string? currentReplayFixture = ResolveOptionalResourcePath(SelectedReplayFixture?.Path);
            string? currentScenarioFile = ResolveOptionalResourcePath(SelectedScenarioFile?.Path);
            bool resourceRootChanged = !PathEquals(_resourceRoot, _tools.ResourceRoot);
            bool firstResourceScan = !_resourcesInitialized;

            if (_scenarioDrafts.Count > 0)
            {
                var remappedDrafts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var draft in _scenarioDrafts)
                    remappedDrafts[ResolveOptionalResourcePath(draft.Key)!] = draft.Value;
                _scenarioDrafts.Clear();
                foreach (var draft in remappedDrafts) _scenarioDrafts[draft.Key] = draft.Value;
            }

            PipelineFiles.Clear();
            foreach (var file in _tools.GetPipelineFiles()) PipelineFiles.Add(file);
            TemplateImages.Clear();
            foreach (var file in _tools.GetTemplateImages()) TemplateImages.Add(file);
            TemplateDirectories.Clear();
            foreach (var file in _tools.GetTemplateDirectories()) TemplateDirectories.Add(file);
            ReplayFixtures.Clear();
            foreach (var file in _tools.GetReplayFixtures()) ReplayFixtures.Add(file);
            ScenarioManifests.Clear();
            foreach (var file in _tools.GetScenarioManifests()) ScenarioManifests.Add(file);

            _refreshingResources = true;
            _loadingPipeline = true;
            try
            {
                SelectedPipelineFile = currentPipeline is null && !firstResourceScan
                    ? null
                    : FindOrKeep(PipelineFiles, currentPipeline) ?? PipelineFiles.FirstOrDefault();
                SelectedTemplateImage = FindOrKeep(TemplateImages, currentTemplate)
                    ?? TemplateImages.FirstOrDefault();
                SelectedTemplateDirectory = FindOrKeep(TemplateDirectories, currentTemplateDirectory)
                    ?? TemplateDirectories.FirstOrDefault();
                SelectedReplayFixture = FindOrKeep(ReplayFixtures, currentReplayFixture)
                    ?? ReplayFixtures.FirstOrDefault();
                SelectedScenarioManifest = currentManifest is null && !firstResourceScan
                    ? null
                    : FindOrKeep(ScenarioManifests, currentManifest) ?? ScenarioManifests.FirstOrDefault();
            }
            finally
            {
                _loadingPipeline = false;
                _refreshingResources = false;
            }

            if (currentTemplate is not null)
                TemplatePath = currentTemplate;
            if (SelectedScenarioManifest is { } selectedManifest
                && !_refreshingResourcesForNativeOperation
                && (currentScenarioFile is not null || resourceRootChanged || ScenarioPackageFiles.Count == 0))
                _ = LoadScenarioManifestAsync(selectedManifest);
            _resourceRoot = _tools.ResourceRoot;
            _resourcesInitialized = true;
            ResourceStatusText = $"{PipelineFiles.Count} pipelines · {TemplateImages.Count} 模板图像 · {ScenarioManifests.Count} 场景包";
            if (firstResourceScan || resourceRootChanged)
                RefreshUmaDatabaseImages();
        }
        catch (Exception ex)
        {
            ResourceStatusText = $"资源扫描失败：{ex.Message}";
            _logService.Log($"DeveloperTools 资源扫描失败: {ex}", LogEntryKind.Error);
        }
    }

    private string? ResolveOptionalResourcePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : _tools.ResolveResourcePath(path);

    [RelayCommand]
    private void RefreshUmaDatabaseImages()
    {
        string? selectedKey = SelectedUmaImage?.Key;
        try
        {
            var items = UmaDatabaseImageService.LoadImages(_tools.ResourceRoot);
            foreach (var old in UmaDatabaseImages)
                old.Thumbnail?.Dispose();
            UmaDatabaseImages.Clear();
            FilteredUmaDatabaseImages.Clear();
            foreach (var item in items)
            {
                Bitmap? thumbnail = null;
                try { thumbnail = DeveloperToolsImageCodec.LoadBitmap(item.ImagePath, 96); }
                catch (Exception ex) { _logService.Log($"跳过无法预览的图库图像 {item.ImagePath}: {ex.Message}", LogEntryKind.Warning); }
                UmaDatabaseImages.Add(item with { Thumbnail = thumbnail });
            }
            FilterUmaDatabaseImages();
            SelectedUmaImage = selectedKey is null
                ? null
                : UmaDatabaseImages.FirstOrDefault(item => item.Key == selectedKey);
            if (SelectedUmaImage is null)
                UmaImageGalleryStatus = $"已读取 {UmaDatabaseImages.Count} 张角色图像。";
        }
        catch (Exception ex)
        {
            UmaImageGalleryStatus = $"读取 UmaDatabase 图像失败：{ex.Message}";
            _logService.Log($"读取 UmaDatabase 图像失败: {ex}", LogEntryKind.Error);
        }
        OnPropertyChanged(nameof(CanSaveSystemReference));
        OnPropertyChanged(nameof(CanTestSystemReferenceMatch));
    }

    private void FilterUmaDatabaseImages()
    {
        FilteredUmaDatabaseImages.Clear();
        string query = UmaImageSearchText.Trim();
        foreach (var item in UmaDatabaseImages)
        {
            if (query.Length > 0
                && !item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !item.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !item.ImagePath.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            FilteredUmaDatabaseImages.Add(item);
        }
        UmaImageGalleryStatus = query.Length == 0
            ? $"{FilteredUmaDatabaseImages.Count} 张图像 · 可搜索 trainee 名称、ID 或类型。"
            : $"筛选结果：{FilteredUmaDatabaseImages.Count} / {UmaDatabaseImages.Count} 张图像。";
    }

    [RelayCommand]
    private void UseSelectedUmaImageAsCropSource()
    {
        if (SelectedUmaImage is not { } item) return;
        try
        {
            SetScreenshot(DeveloperToolsImageCodec.LoadAsScreenshot(item.ImagePath));
            _galleryCropSourcePath = item.ImagePath;
            UmaImageGalleryStatus = $"已载入 {item.DisplayName} 作为裁剪源。返回截图页拖动选择区域，再保存为 system reference。";
        }
        catch (Exception ex)
        {
            UmaImageGalleryStatus = $"无法载入图库图像：{ex.Message}";
            _logService.Log($"UmaDatabase 图像载入失败: {ex}", LogEntryKind.Error);
        }
        NotifyImageCommands();
    }

    [RelayCommand]
    private async Task SaveSystemReferenceAsync()
    {
        if (!CanSaveSystemReference || SelectedUmaImage is not { } item || _screenshot is not { } screenshot || _cropRegion is not { } crop)
            return;
        _isSavingSystemReference = true;
        NotifyImageCommands();
        try
        {
            await InitializeCoreForDiagnosticsAsync();
            string targetPath = _tools.ResolveResourcePath(item.ReferencePath);
            byte[]? backupBytes = File.Exists(targetPath) ? await File.ReadAllBytesAsync(targetPath) : null;
            if (backupBytes is not null)
            {
                string backupPath = CreateImageBackupPath(targetPath);
                bool savedBackup = await _automationService.SaveResourceBytesAsync(backupPath, backupBytes);
                if (!savedBackup && IsUnderResourceRoot(backupPath))
                    throw new IOException("无法在 resource-overrides 中备份现有系统参考图像。");
            }

            byte[] imageBytes = DeveloperToolsImageCodec.EncodeScreenshotCrop(screenshot, crop.X, crop.Y, crop.Width, crop.Height, ".webp");
            bool tracked = await _automationService.SaveResourceBytesAsync(targetPath, imageBytes);
            if (!tracked && IsUnderResourceRoot(targetPath))
                throw new IOException("系统参考图像没有写入活动资源视图。");

            string description = item.IsLiveOutfit ? "live outfit system reference" : "race outfit system reference";
            SystemReferenceStatus = backupBytes is null
                ? $"已创建 {description}: {targetPath}"
                : $"已更新 {description}: {targetPath} · 原图已备份。";
            _logService.Log(SystemReferenceStatus, LogEntryKind.Success);
            SetCropRegion(null);
        }
        catch (Exception ex)
        {
            SystemReferenceStatus = $"系统参考图像保存失败：{ex.Message}";
            _logService.Log(SystemReferenceStatus, LogEntryKind.Error);
        }
        finally
        {
            _isSavingSystemReference = false;
            NotifyImageCommands();
        }
    }

    [RelayCommand]
    private async Task TestSystemReferenceMatchAsync()
    {
        if (!CanTestSystemReferenceMatch || SelectedUmaImage is not { } item || _screenshot is not { } screenshot)
            return;
        _isTestingSystemReference = true;
        NotifyImageCommands();
        try
        {
            string path = _tools.ResolveResourcePath(item.ReferencePath);
            double threshold = MatchThreshold;
            var result = await RunCoreExclusiveAsync(ct => Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var screen = RecognitionImageDecoder.DecodeBytes(screenshot.PngBytes)
                    ?? throw new InvalidDataException("当前预览图像无法解码。");
                var template = _tools.LoadTemplateImage(path)
                    ?? throw new InvalidDataException("原生 Core 无法加载 system reference 图像。");
                return _tools.MatchTemplate(screen, template, new TemplateMatchOptions(
                    MatchMethod, null, threshold, screen.Width, screen.Height));
            }, ct));
            SystemReferenceStatus = result.Found
                ? $"原生匹配通过 · score {result.Score:0.000} · {result.X},{result.Y} {result.Width}×{result.Height}"
                : $"未匹配 · best score {result.Score:0.000} / threshold {threshold:0.000}";
            _logService.Log(SystemReferenceStatus, result.Found ? LogEntryKind.Success : LogEntryKind.Normal);
        }
        catch (Exception ex)
        {
            SystemReferenceStatus = $"原生匹配失败：{ex.Message}";
            _logService.Log(SystemReferenceStatus, LogEntryKind.Error);
        }
        finally
        {
            _isTestingSystemReference = false;
            NotifyImageCommands();
        }
    }

    private static DeveloperResourceFile? FindOrKeep(
        IEnumerable<DeveloperResourceFile> files,
        string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return files.FirstOrDefault(file => PathEquals(file.Path, path))
            ?? new DeveloperResourceFile(Path.GetFullPath(path), Path.GetFileName(path));
    }

    [RelayCommand]
    private async Task CaptureScreenshotAsync()
    {
        if (!IsConnected || IsCapturing) return;
        IsCapturing = true;
        try
        {
            var captured = await _automationService.CaptureScreenshotAsync();
            if (captured is null)
                SetImageStatus("原生截图操作没有返回画面。", LogEntryKind.Error);
            else
                SetScreenshot(captured);
        }
        catch (Exception ex)
        {
            SetImageStatus($"截图失败：{ex.Message}", LogEntryKind.Error);
        }
        finally
        {
            IsCapturing = false;
        }
    }

    public void ImportScreenshot(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            var decoded = RecognitionImageDecoder.DecodeBytes(bytes)
                ?? throw new InvalidDataException("原生图像解码器无法读取该文件。");
            using var input = new MemoryStream(bytes, writable: false);
            using var bitmap = new Bitmap(input);
            using var png = new MemoryStream();
            bitmap.Save(png);
            SetScreenshot(new CapturedScreenshot(png.ToArray(), decoded.RgbaPixels, decoded.Width, decoded.Height, File.GetLastWriteTime(path)));
        }
        catch (Exception ex)
        {
            SetImageStatus($"导入图像失败：{ex.Message}", LogEntryKind.Error);
        }
    }

    public void ShowError(string title, string message)
    {
        ImageMatchStatus = $"{title}：{message}";
        _logService.Log(ImageMatchStatus, LogEntryKind.Error);
    }

    public async Task ExportScreenshotAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_screenshot is null) throw new InvalidOperationException("当前没有可导出的截图。");
        await File.WriteAllBytesAsync(path, _screenshot.PngBytes, cancellationToken);
        SetImageStatus($"截图已导出：{path}", LogEntryKind.Success);
    }

    public async Task SaveCropAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_screenshot is null || _cropRegion is not { } crop)
            throw new InvalidOperationException("请先选择一个有效的裁剪区域。");
        cancellationToken.ThrowIfCancellationRequested();
        var source = RecognitionImageDecoder.DecodeBytes(_screenshot.PngBytes)
            ?? throw new InvalidDataException("当前截图无法解码。");
        var result = RecognitionImageDecoder.Transform(source,
            new RecognitionImageTransform(crop.X, crop.Y, crop.Width, crop.Height));
        if (result.RgbaPixels is not { } rgba)
            throw new InvalidDataException("裁剪图像没有 RGBA 像素数据。");
        using var bitmap = CreateBitmap(result.Width, result.Height, rgba);
        await using var stream = File.Create(path);
        bitmap.Save(stream);
        await stream.FlushAsync(cancellationToken);
        SetImageStatus($"裁剪已保存：{path}", LogEntryKind.Success);
    }

    public void SetCropRegion(int x, int y, int width, int height)
    {
        if (_screenshot is null || width <= 0 || height <= 0)
        {
            SetCropRegion(null);
            return;
        }
        int left = Math.Clamp(x, 0, _screenshot.Width - 1);
        int top = Math.Clamp(y, 0, _screenshot.Height - 1);
        int right = Math.Clamp(x + width, left + 1, _screenshot.Width);
        int bottom = Math.Clamp(y + height, top + 1, _screenshot.Height);
        SetCropRegion(new ImageCropRegion(left, top, right - left, bottom - top));
    }

    [RelayCommand]
    private void ClearCrop() => SetCropRegion(null);

    [RelayCommand]
    private async Task MatchImageAsync()
    {
        if (_screenshot is null || IsMatchingImage) return;
        string path = !string.IsNullOrWhiteSpace(TemplatePath)
            ? TemplatePath
            : SelectedTemplateImage?.Path ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SetImageStatus("请选择存在的模板图像文件。", LogEntryKind.Error);
            return;
        }

        IsMatchingImage = true;
        try
        {
            var captured = _screenshot;
            var method = MatchMethod;
            var threshold = MatchThreshold;
            var result = await RunCoreExclusiveAsync(ct => Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var screen = RecognitionImageDecoder.DecodeBytes(captured.PngBytes)
                    ?? throw new InvalidDataException("当前截图无法解码。");
                var template = _tools.LoadTemplateImage(path)
                    ?? throw new InvalidDataException("原生 Core 无法加载模板图像。");
                return _tools.MatchTemplate(screen, template, new TemplateMatchOptions(
                    method, null, threshold, screen.Width, screen.Height));
            }, ct));
            SetImageStatus(result.Found
                ? $"找到模板 · score {result.Score:0.000} · {result.X},{result.Y} {result.Width}×{result.Height}"
                : $"未找到模板 · best score {result.Score:0.000} / threshold {threshold:0.000}",
                result.Found ? LogEntryKind.Success : LogEntryKind.Normal);
        }
        catch (Exception ex)
        {
            SetImageStatus($"原生模板匹配失败：{ex.Message}", LogEntryKind.Error);
        }
        finally
        {
            IsMatchingImage = false;
        }
    }

    [RelayCommand]
    private void NewPipeline()
    {
        SelectedPipelineFile = null;
        PipelineJsonText = "{\n  \"name\": \"new-pipeline\",\n  \"schemaVersion\": 1,\n  \"description\": \"\",\n  \"referenceWidth\": 900,\n  \"referenceHeight\": 1600,\n  \"tasks\": {}\n}";
        PipelineEntryTask = string.Empty;
        _pipelineHistoryBaseline = PipelineJsonText;
        _pipelineSavedText = string.Empty;
        _pipelineUndo.Clear();
        _pipelineRedo.Clear();
        IsPipelineDirty = true;
        PipelineStatusText = "新 Pipeline 草稿；保存到 JSON 文件后才能运行。";
        NotifyHistory();
    }

    [RelayCommand]
    private void LoadPipeline()
    {
        if (SelectedPipelineFile is null) return;
        _ = LoadPipelineAsync(SelectedPipelineFile.Path);
    }

    [RelayCommand]
    private void AddPipelineTask()
    {
        try
        {
            var root = JsonNode.Parse(PipelineJsonText)?.AsObject()
                ?? throw new InvalidDataException("Pipeline JSON root must be an object.");
            var tasks = root["tasks"] as JsonObject ?? new JsonObject();
            root["tasks"] = tasks;
            string name = "newTask";
            int suffix = 1;
            while (tasks.ContainsKey(name)) name = $"newTask{suffix++}";
            tasks[name] = new JsonObject
            {
                ["algorithm"] = "MatchTemplate",
                ["action"] = "ClickSelf",
                ["template"] = "templates/start_game/game_home_selected.png",
                ["templThreshold"] = 0.86,
                ["timeoutMs"] = 10000,
                ["pollIntervalMs"] = 300
            };
            PipelineJsonText = root.ToJsonString(IndentedJsonOptions);
            SelectedPipelineTaskName = name;
            PipelineStatusText = $"已添加节点 {name}。";
        }
        catch (Exception ex) { PipelineStatusText = $"无法添加节点：{ex.Message}"; }
    }

    [RelayCommand]
    private void RemovePipelineTask()
    {
        try
        {
            var root = JsonNode.Parse(PipelineJsonText)?.AsObject()
                ?? throw new InvalidDataException("Pipeline JSON root must be an object.");
            if (root["tasks"] is not JsonObject tasks || !tasks.Remove(SelectedPipelineTaskName)) return;
            PipelineJsonText = root.ToJsonString(IndentedJsonOptions);
            SelectedPipelineTaskName = PipelineTaskNames.FirstOrDefault() ?? string.Empty;
            PipelineStatusText = "已删除选中节点。";
        }
        catch (Exception ex) { PipelineStatusText = $"无法删除节点：{ex.Message}"; }
    }

    [RelayCommand]
    private void UseCropAsPipelineRoi()
    {
        if (_cropRegion is not { } crop || string.IsNullOrWhiteSpace(SelectedPipelineTaskName))
        {
            PipelineStatusText = "先在图像页框选区域，并选择一个 Pipeline 节点。";
            return;
        }
        try
        {
            var root = JsonNode.Parse(PipelineJsonText)?.AsObject()
                ?? throw new InvalidDataException("Pipeline JSON root must be an object.");
            if (root["tasks"] is not JsonObject tasks || tasks[SelectedPipelineTaskName] is not JsonObject task)
                throw new InvalidDataException("所选节点不存在。");
            task["roi"] = new JsonArray(crop.X, crop.Y, crop.Width, crop.Height);
            PipelineJsonText = root.ToJsonString(IndentedJsonOptions);
            PipelineStatusText = $"已将截图裁剪区域应用到 {SelectedPipelineTaskName}.roi。";
        }
        catch (Exception ex) { PipelineStatusText = $"应用 ROI 失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task SavePipelineTemplateAsync()
    {
        if (!CanSavePipelineTemplate || SelectedPipelineFile is null
            || string.IsNullOrWhiteSpace(SelectedPipelineTaskName)
            || _screenshot is not { } screenshot || _cropRegion is not { } crop)
            return;

        string taskName = SelectedPipelineTaskName;
        bool replacing = _pipelineTemplateEditPath is not null;
        try
        {
            await InitializeCoreForDiagnosticsAsync();
            if (SelectedPipelineFile is null || !string.Equals(taskName, SelectedPipelineTaskName, StringComparison.Ordinal))
                throw new InvalidOperationException("Pipeline node selection changed during template save.");

            string pipelinePath = _tools.ResolveResourcePath(SelectedPipelineFile.Path);
            string pipelineDirectory = Path.GetDirectoryName(Path.GetFullPath(pipelinePath))!;
            string targetPath;
            string? candidateJson = null;
            if (replacing)
            {
                if (!string.Equals(_pipelineTemplateEditTask, taskName, StringComparison.Ordinal))
                    throw new InvalidOperationException("The selected node changed; reload its template before replacing it.");
                targetPath = _tools.ResolveResourcePath(_pipelineTemplateEditPath!);
                if (!File.Exists(targetPath))
                    throw new FileNotFoundException("The current node template no longer exists.", targetPath);
            }
            else
            {
                string pipelineSlug = SafeFileName(Path.GetFileNameWithoutExtension(pipelinePath));
                string taskSlug = SafeFileName(taskName);
                targetPath = Path.Combine(pipelineDirectory, "templates", pipelineSlug, taskSlug + ".png");
                var root = JsonNode.Parse(PipelineJsonText)?.AsObject()
                    ?? throw new InvalidDataException("Pipeline JSON root must be an object.");
                if (root["tasks"] is not JsonObject tasks || tasks[taskName] is not JsonObject task)
                    throw new InvalidDataException("The selected pipeline node does not exist.");
                task["template"] = Path.GetRelativePath(pipelineDirectory, targetPath)
                    .Replace(Path.DirectorySeparatorChar, '/');
                candidateJson = root.ToJsonString(IndentedJsonOptions);
                string? error = await RunCoreExclusiveAsync(ct => Task.Run(() => _tools.ValidatePipelineJson(candidateJson), ct));
                if (error is not null)
                    throw new InvalidDataException("Native Core rejected the node template path update: " + error);
            }

            byte[] cropBytes = DeveloperToolsImageCodec.EncodeScreenshotCrop(screenshot, crop.X, crop.Y, crop.Width, crop.Height,
                Path.GetExtension(targetPath));
            if (replacing)
            {
                byte[] original = await File.ReadAllBytesAsync(targetPath);
                string backupPath = CreateImageBackupPath(targetPath);
                bool backedUp = await _automationService.SaveResourceBytesAsync(backupPath, original);
                if (!backedUp && IsUnderResourceRoot(backupPath))
                    throw new IOException("Could not create a resource override backup for the original pipeline template.");
            }

            await _automationService.SaveResourceBytesAsync(targetPath, cropBytes);
            if (candidateJson is not null)
                PipelineJsonText = candidateJson;
            _pipelineTemplateEditPath = null;
            _pipelineTemplateEditTask = null;
            OnPropertyChanged(nameof(PipelineTemplateSaveLabel));
            OnPropertyChanged(nameof(CanEditPipelineTemplate));
            SetCropRegion(null);
            PipelineStatusText = replacing
                ? $"已替换节点 {taskName} 的模板图像；现有 ROI 保持不变。"
                : $"已保存节点模板 {Path.GetRelativePath(pipelineDirectory, targetPath).Replace(Path.DirectorySeparatorChar, '/')}，并写入该节点 template 路径。";
            _logService.Log(PipelineStatusText, LogEntryKind.Success);
        }
        catch (Exception ex)
        {
            PipelineStatusText = $"保存节点模板失败：{ex.Message}";
            _logService.Log(PipelineStatusText, LogEntryKind.Error);
        }
    }

    [RelayCommand]
    private void EditPipelineTemplate()
    {
        if (SelectedPipelineFile is null || string.IsNullOrWhiteSpace(SelectedPipelineTaskName)) return;
        try
        {
            string templatePath = GetSelectedPipelineTemplatePath()
                ?? throw new InvalidDataException("当前节点没有 template 路径。");
            if (!File.Exists(templatePath))
                throw new FileNotFoundException("当前节点模板文件不存在。", templatePath);
            SetScreenshot(DeveloperToolsImageCodec.LoadAsScreenshot(templatePath));
            _pipelineTemplateEditPath = templatePath;
            _pipelineTemplateEditTask = SelectedPipelineTaskName;
            PipelineStatusText = "已载入当前节点模板；在截图页框选新区域后保存，系统会备份旧图并保留现有 ROI。";
            OnPropertyChanged(nameof(PipelineTemplateSaveLabel));
            OnPropertyChanged(nameof(CanSavePipelineTemplate));
            OnPropertyChanged(nameof(CanEditPipelineTemplate));
        }
        catch (Exception ex)
        {
            PipelineStatusText = $"无法编辑当前节点模板：{ex.Message}";
            _logService.Log(PipelineStatusText, LogEntryKind.Error);
        }
    }

    [RelayCommand]
    private void CancelPipelineTemplateEdit()
    {
        _pipelineTemplateEditPath = null;
        _pipelineTemplateEditTask = null;
        OnPropertyChanged(nameof(PipelineTemplateSaveLabel));
        OnPropertyChanged(nameof(CanEditPipelineTemplate));
        PipelineStatusText = "已取消模板替换模式。";
    }

    private string? GetSelectedPipelineTemplatePath()
    {
        if (SelectedPipelineFile is null || string.IsNullOrWhiteSpace(SelectedPipelineTaskName)) return null;
        try
        {
            var root = JsonNode.Parse(PipelineJsonText)?.AsObject();
            if (root?["tasks"] is not JsonObject tasks || tasks[SelectedPipelineTaskName] is not JsonObject task
                || task["template"]?.GetValue<string>() is not { Length: > 0 } relativePath)
                return null;
            string pipelinePath = _tools.ResolveResourcePath(SelectedPipelineFile.Path);
            string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pipelinePath)!,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return _tools.ResolveResourcePath(path);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or IOException)
        {
            return null;
        }
    }

    [RelayCommand]
    private void PreviewPipeline() => RefreshPipelineDetails(PipelineJsonText);

    [RelayCommand]
    private async Task RunOfflineReplayAsync()
    {
        if (SelectedPipelineFile is null)
        {
            PipelineRunStatusText = "先保存或选择一个 Pipeline 文件。";
            return;
        }
        try { await InitializeCoreForDiagnosticsAsync(); }
        catch (Exception ex)
        {
            PipelineRunStatusText = $"Native replay could not initialize Core: {ex.Message}";
            ReplayOutputText = PipelineRunStatusText;
            _logService.Log(PipelineRunStatusText, LogEntryKind.Error);
            return;
        }
        if (SelectedPipelineFile is null || SelectedReplayFixture is null)
        {
            PipelineRunStatusText = "选择一个 replay fixture JSON。";
            return;
        }
        string pipelineSourcePath = SelectedPipelineFile.Path;
        string pipelineJson = PipelineJsonText;
        string? entryTask = string.IsNullOrWhiteSpace(PipelineEntryTask) ? null : PipelineEntryTask;
        string fixtureSourcePath = SelectedReplayFixture.Path;
        IsPipelineRunning = true;
        _pipelineCancellation?.Dispose();
        _pipelineCancellation = new CancellationTokenSource();
        string? temporaryPipelinePath = null;
        try
        {
            string? validationError = await RunCoreExclusiveAsync(
                ct => Task.Run(() => _tools.ValidatePipelineJson(pipelineJson), ct),
                _pipelineCancellation.Token);
            if (validationError is not null)
            {
                PipelineRunStatusText = $"Replay 已阻止：{validationError}";
                return;
            }
            string activePipelinePath = _tools.ResolveResourcePath(pipelineSourcePath);
            string pipelineDirectory = Path.GetDirectoryName(Path.GetFullPath(activePipelinePath))!;
            temporaryPipelinePath = Path.Combine(pipelineDirectory,
                $".{Path.GetFileNameWithoutExtension(activePipelinePath)}.{Guid.NewGuid():N}.replay.json");
            await File.WriteAllTextAsync(
                temporaryPipelinePath,
                pipelineJson,
                new System.Text.UTF8Encoding(false),
                _pipelineCancellation.Token);
            var result = await DeveloperToolsService.RunOfflineReplayAsync(
                temporaryPipelinePath,
                _tools.ResolveResourcePath(fixtureSourcePath),
                entryTask,
                _pipelineCancellation.Token);
            if (!result.Available)
            {
                PipelineRunStatusText = "Native replay unavailable: " + result.StandardError;
                ReplayOutputText = PipelineRunStatusText;
                return;
            }
            if (string.IsNullOrWhiteSpace(result.StandardOutput))
                throw new InvalidDataException(string.IsNullOrWhiteSpace(result.StandardError)
                    ? $"Replay tool exited with code {result.ExitCode} without a result."
                    : result.StandardError.Trim());
            using var replay = JsonDocument.Parse(result.StandardOutput);
            bool succeeded = replay.RootElement.TryGetProperty("success", out var success)
                && success.ValueKind == JsonValueKind.True
                && result.ExitCode == 0;
            ReplayOutputText = JsonSerializer.Serialize(replay.RootElement, IndentedJsonOptions)
                + (string.IsNullOrWhiteSpace(result.StandardError) ? string.Empty : Environment.NewLine + result.StandardError.Trim());
            PipelineRunStatusText = succeeded
                ? "Native offline replay completed successfully."
                : $"Native offline replay failed (exit {result.ExitCode}).";
            PipelineStatusText = PipelineRunStatusText;
            _logService.Log(PipelineRunStatusText, succeeded ? LogEntryKind.Success : LogEntryKind.Error);
        }
        catch (OperationCanceledException)
        {
            PipelineRunStatusText = "Native offline replay cancelled.";
            ReplayOutputText = PipelineRunStatusText;
        }
        catch (Exception ex)
        {
            PipelineRunStatusText = $"Replay failed: {ex.Message}";
            ReplayOutputText = PipelineRunStatusText;
            _logService.Log(PipelineRunStatusText, LogEntryKind.Error);
        }
        finally
        {
            if (temporaryPipelinePath is not null)
            {
                try { File.Delete(temporaryPipelinePath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            IsPipelineRunning = false;
            _pipelineCancellation?.Dispose();
            _pipelineCancellation = null;
        }
    }

    private async Task LoadPipelineAsync(string path)
    {
        IsPipelineBusy = true;
        try
        {
            string json = await File.ReadAllTextAsync(path);
            _loadingPipeline = true;
            SelectedPipelineFile = PipelineFiles.FirstOrDefault(file => PathEquals(file.Path, path))
                ?? new DeveloperResourceFile(Path.GetFullPath(path), Path.GetFileName(path));
            _loadingPipeline = false;
            _restoringPipelineHistory = true;
            PipelineJsonText = json;
            _restoringPipelineHistory = false;
            _pipelineHistoryBaseline = json;
            _pipelineSavedText = json;
            _pipelineUndo.Clear();
            _pipelineRedo.Clear();
            IsPipelineDirty = false;
            PipelineEntryTask = ReadDefaultEntryTask(json);
            RefreshPipelineDetails(json);
            PipelineStatusText = $"已加载 {SelectedPipelineFile.RelativePath}";
            NotifyHistory();
        }
        catch (Exception ex)
        {
            _loadingPipeline = false;
            _restoringPipelineHistory = false;
            PipelineStatusText = $"加载失败：{ex.Message}";
            _logService.Log($"加载 Pipeline 失败: {ex}", LogEntryKind.Error);
        }
        finally
        {
            IsPipelineBusy = false;
        }
    }

    [RelayCommand]
    private async Task SavePipelineAsync()
    {
        if (SelectedPipelineFile is null)
        {
            PipelineStatusText = "新 Pipeline 需要使用“另存为”选择文件路径。";
            return;
        }
        await SavePipelineToAsync(SelectedPipelineFile.Path);
    }

    public async Task SavePipelineToAsync(string path)
    {
        try { await InitializeCoreForDiagnosticsAsync(); }
        catch (Exception ex)
        {
            PipelineStatusText = $"无法初始化原生 Core，未保存 Pipeline：{ex.Message}";
            _logService.Log(PipelineStatusText, LogEntryKind.Error);
            return;
        }
        string json = PipelineJsonText;
        IsPipelineBusy = true;
        try
        {
            string? validationError = await RunCoreExclusiveAsync(
                ct => Task.Run(() => _tools.ValidatePipelineJson(json), ct));
            if (validationError is not null)
            {
                PipelineStatusText = $"保存已阻止；原生 Core 校验失败：{validationError}";
                _logService.Log(PipelineStatusText, LogEntryKind.Error);
                return;
            }
            string fullPath = _tools.ResolveResourcePath(path);
            await _tools.SaveResourceTextAsync(fullPath, json);
            var file = PipelineFiles.FirstOrDefault(existing => PathEquals(existing.Path, fullPath))
                ?? new DeveloperResourceFile(fullPath, Path.GetRelativePath(_tools.ResourceRoot, fullPath));
            if (!PipelineFiles.Any(existing => PathEquals(existing.Path, file.Path))) PipelineFiles.Add(file);
            _loadingPipeline = true;
            SelectedPipelineFile = file;
            _loadingPipeline = false;
            _restoringPipelineHistory = true;
            PipelineJsonText = json;
            _restoringPipelineHistory = false;
            _pipelineHistoryBaseline = json;
            _pipelineSavedText = json;
            IsPipelineDirty = false;
            PipelineStatusText = $"已保存 {file.Path}";
            _logService.Log($"Pipeline JSON 已保存：{file.Path}", LogEntryKind.Success);
        }
        catch (Exception ex)
        {
            _loadingPipeline = false;
            PipelineStatusText = $"保存失败：{ex.Message}";
            _logService.Log($"保存 Pipeline 失败: {ex}", LogEntryKind.Error);
        }
        finally
        {
            IsPipelineBusy = false;
        }
    }

    [RelayCommand]
    private async Task ValidatePipelineAsync()
    {
        try { await InitializeCoreForDiagnosticsAsync(); }
        catch (Exception ex)
        {
            PipelineStatusText = $"原生 Core 初始化失败：{ex.Message}";
            _logService.Log(PipelineStatusText, LogEntryKind.Error);
            return;
        }
        string json = PipelineJsonText;
        IsPipelineBusy = true;
        try
        {
            string? error = await RunCoreExclusiveAsync(
                ct => Task.Run(() => _tools.ValidatePipelineJson(json), ct));
            PipelineStatusText = error is null
                ? "原生 Core 校验通过。"
                : $"原生 Core 校验失败：{error}";
            _logService.Log(PipelineStatusText, error is null ? LogEntryKind.Success : LogEntryKind.Error);
        }
        catch (Exception ex)
        {
            PipelineStatusText = $"原生 Core 校验失败：{ex.Message}";
            _logService.Log($"Pipeline 原生校验失败: {ex}", LogEntryKind.Error);
        }
        finally
        {
            IsPipelineBusy = false;
        }
    }

    [RelayCommand]
    private void UndoPipeline()
    {
        if (_pipelineUndo.Count == 0) return;
        PushHistory(_pipelineRedo, PipelineJsonText);
        RestorePipelineText(_pipelineUndo.Pop());
    }

    [RelayCommand]
    private void RedoPipeline()
    {
        if (_pipelineRedo.Count == 0) return;
        PushHistory(_pipelineUndo, PipelineJsonText);
        RestorePipelineText(_pipelineRedo.Pop());
    }

    [RelayCommand]
    private async Task RunPipelineAsync()
    {
        if (!CanRunPipeline || SelectedPipelineFile is null) return;
        if (_automationService.IsBusy)
        {
            PipelineRunStatusText = "另一个自动化操作正在运行。";
            return;
        }
        try { await InitializeCoreForDiagnosticsAsync(); }
        catch (Exception ex)
        {
            PipelineRunStatusText = $"原生 Core 初始化失败：{ex.Message}";
            _logService.Log(PipelineRunStatusText, LogEntryKind.Error);
            return;
        }
        if (!CanRunPipeline || SelectedPipelineFile is null)
            return;
        if (IsPipelineDirty)
        {
            PipelineRunStatusText = "先保存编辑，再运行已保存的文件。";
            return;
        }

        _pipelineCancellation?.Dispose();
        _pipelineCancellation = new CancellationTokenSource();
        string pipelinePath = SelectedPipelineFile.Path;
        string pipelineJson = PipelineJsonText;
        string? entryTask = string.IsNullOrWhiteSpace(PipelineEntryTask) ? null : PipelineEntryTask;
        IsPipelineRunning = true;
        PipelineRunStatusText = "正在运行原生 Pipeline…";
        try
        {
            string? validationError = await RunCoreExclusiveAsync(
                ct => Task.Run(() => _tools.ValidatePipelineJson(pipelineJson), ct),
                _pipelineCancellation.Token);
            if (validationError is not null)
            {
                PipelineRunStatusText = $"运行已阻止：{validationError}";
                return;
            }
            var result = await RunCoreExclusiveAsync(
                ct => _tools.RunPipelineAsync(
                    pipelinePath,
                    entryTask,
                    ct),
                _pipelineCancellation.Token);
            PipelineRunStatusText = result.Succeeded
                ? $"Pipeline 完成：{result.TotalSteps} 步，结束于 {result.FinalTask}。"
                : $"Pipeline 失败：{result.FinalTask} — {result.Message ?? "Core 返回失败结果。"}";
            _logService.Log(PipelineRunStatusText, result.Succeeded ? LogEntryKind.Success : LogEntryKind.Error);
        }
        catch (OperationCanceledException)
        {
            PipelineRunStatusText = "Pipeline 已取消。";
            _logService.Log(PipelineRunStatusText, LogEntryKind.Warning);
        }
        catch (Exception ex)
        {
            PipelineRunStatusText = $"Pipeline 运行失败：{ex.Message}";
            _logService.Log($"Pipeline 运行失败: {ex}", LogEntryKind.Error);
        }
        finally
        {
            IsPipelineRunning = false;
            _pipelineCancellation?.Dispose();
            _pipelineCancellation = null;
        }
    }

    [RelayCommand]
    private void StopPipeline()
    {
        if (!IsPipelineRunning) return;
        PipelineRunStatusText = "正在请求原生 Core 停止 Pipeline…";
        _pipelineCancellation?.Cancel();
    }

    [RelayCommand]
    private async Task SaveScenarioFileAsync()
    {
        if (SelectedScenarioFile is null) return;
        IsScenarioBusy = true;
        try
        {
            await InitializeCoreForDiagnosticsAsync();
            if (SelectedScenarioFile is null || SelectedScenarioManifest is null)
                throw new InvalidOperationException("请先选择场景包文件。");
            await LoadScenarioManifestAsync(SelectedScenarioManifest);
            string selectedPath = SelectedScenarioFile.Path;
            string manifestPath = SelectedScenarioManifest.Path;
            string scenarioJson = ScenarioJsonText;
            var drafts = new Dictionary<string, string>(_scenarioDrafts, StringComparer.OrdinalIgnoreCase)
            {
                [selectedPath] = scenarioJson
            };
            selectedPath = _tools.ResolveResourcePath(selectedPath);
            manifestPath = _tools.ResolveResourcePath(manifestPath);
            var inspection = await _tools.InspectScenarioPackageAsync(manifestPath, drafts);
            if (!inspection.Available)
            {
                ScenarioStatusText = "保存已阻止；Native Inspector unavailable: " + inspection.StandardError;
                ScenarioInspectionText = ScenarioStatusText;
                _logService.Log(ScenarioStatusText, LogEntryKind.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(inspection.StandardOutput))
                throw new InvalidDataException(string.IsNullOrWhiteSpace(inspection.StandardError)
                    ? $"Native Inspector exited with code {inspection.ExitCode} without a result."
                    : inspection.StandardError.Trim());
            using var parsedInspection = JsonDocument.Parse(inspection.StandardOutput);
            bool valid = parsedInspection.RootElement.TryGetProperty("valid", out var validValue)
                && validValue.ValueKind == JsonValueKind.True;
            if (!valid || inspection.ExitCode != 0)
            {
                var diagnostics = parsedInspection.RootElement.TryGetProperty("diagnostics", out var diagnosticArray)
                    && diagnosticArray.ValueKind == JsonValueKind.Array
                    ? string.Join(Environment.NewLine, diagnosticArray.EnumerateArray().Select(item => item.ToString()))
                    : inspection.StandardOutput.Trim();
                ScenarioInspectionText = $"Native Inspector rejected the package (exit {inspection.ExitCode}).{Environment.NewLine}{diagnostics}{Environment.NewLine}{inspection.StandardError}".Trim();
                ScenarioStatusText = "保存已阻止；Native Inspector 未接受场景包。原文件未修改。";
                _logService.Log(ScenarioStatusText, LogEntryKind.Error);
                return;
            }

            await _tools.SaveResourceTextAsync(selectedPath, scenarioJson);
            _scenarioDrafts.Remove(selectedPath);
            ScenarioStatusText = $"已保存 {Path.GetRelativePath(_tools.ResourceRoot, selectedPath)} · Native Inspector 校验通过";
            ScenarioInspectionText = JsonSerializer.Serialize(parsedInspection.RootElement, IndentedJsonOptions);
            _logService.Log($"场景包 JSON 已保存：{selectedPath}", LogEntryKind.Success);
            RefreshResources();
        }
        catch (Exception ex)
        {
            ScenarioStatusText = $"保存失败：{ex.Message}";
            _logService.Log($"场景包 JSON 保存失败: {ex}", LogEntryKind.Error);
        }
        finally
        {
            IsScenarioBusy = false;
        }
    }

    [RelayCommand]
    private async Task ValidateScenarioPackageAsync()
    {
        if (SelectedScenarioManifest is null) return;
        IsScenarioBusy = true;
        try
        {
            await InitializeCoreForDiagnosticsAsync();
            if (SelectedScenarioManifest is null) return;
            await LoadScenarioManifestAsync(SelectedScenarioManifest);
            string manifestPath = SelectedScenarioManifest.Path;
            var overrides = new Dictionary<string, string>(_scenarioDrafts, StringComparer.OrdinalIgnoreCase);
            if (SelectedScenarioFile is { } selected)
                overrides[selected.Path] = ScenarioJsonText;
            var result = await _tools.InspectScenarioPackageAsync(
                _tools.ResolveResourcePath(manifestPath), overrides);
            if (!result.Available)
            {
                ScenarioStatusText = "Native Inspector unavailable: " + result.StandardError;
                ScenarioInspectionText = ScenarioStatusText;
                _logService.Log(ScenarioStatusText, LogEntryKind.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(result.StandardOutput))
                throw new InvalidDataException(string.IsNullOrWhiteSpace(result.StandardError)
                    ? $"Native Inspector exited with code {result.ExitCode} without a result."
                    : result.StandardError.Trim());
            using var inspection = JsonDocument.Parse(result.StandardOutput);
            bool valid = inspection.RootElement.TryGetProperty("valid", out var validValue)
                && validValue.ValueKind == JsonValueKind.True;
            var diagnostics = inspection.RootElement.TryGetProperty("diagnostics", out var diagnosticArray)
                && diagnosticArray.ValueKind == JsonValueKind.Array
                ? string.Join(Environment.NewLine, diagnosticArray.EnumerateArray().Select(item => item.ToString()))
                : string.Empty;
            ScenarioInspectionText = $"Native Inspector · valid={valid} · exit={result.ExitCode}{Environment.NewLine}{diagnostics}{Environment.NewLine}{result.StandardError}".Trim();
            bool accepted = valid && result.ExitCode == 0;
            ScenarioStatusText = accepted
                ? $"Native Inspector accepted the package ({ScenarioPackageFiles.Count} JSON files)."
                : $"Native Inspector rejected the package (exit {result.ExitCode}).";
            _logService.Log(ScenarioStatusText, accepted ? LogEntryKind.Success : LogEntryKind.Error);
        }
        catch (Exception ex)
        {
            ScenarioStatusText = $"场景包检查失败：{ex.Message}";
            _logService.Log($"场景包检查失败: {ex}", LogEntryKind.Error);
        }
        finally
        {
            IsScenarioBusy = false;
        }
    }

    [RelayCommand]
    private async Task ReloadNativeResourcesAsync()
    {
        try
        {
            await RunCoreExclusiveAsync(async ct =>
            {
                await _tools.ReloadResourcesAsync(ct);
                return true;
            });
            ResourceStatusText = $"Native resource snapshot reloaded from {_tools.ResourceRoot}";
            _logService.Log(ResourceStatusText, LogEntryKind.Success);
        }
        catch (Exception ex)
        {
            ResourceStatusText = $"资源重载失败：{ex.Message}";
            _logService.Log($"重载原生资源失败: {ex}", LogEntryKind.Error);
        }
    }

    private async Task LoadScenarioManifestAsync(DeveloperResourceFile? manifest)
    {
        string? currentScenarioPath = ResolveOptionalResourcePath(SelectedScenarioFile?.Path);
        ScenarioPackageFiles.Clear();
        SelectedScenarioFile = null;
        if (manifest is null) return;
        IsScenarioBusy = true;
        try
        {
            var files = await Task.Run(() => _tools.GetScenarioPackageFiles(manifest.Path));
            foreach (var file in files) ScenarioPackageFiles.Add(file);
            SelectedScenarioFile = ScenarioPackageFiles.FirstOrDefault(file => currentScenarioPath is not null
                && PathEquals(file.Path, currentScenarioPath))
                ?? ScenarioPackageFiles.FirstOrDefault();
            ScenarioStatusText = $"已加载 {manifest.RelativePath} · {ScenarioPackageFiles.Count} 个 JSON 文件";
        }
        catch (Exception ex)
        {
            ScenarioStatusText = $"读取场景包失败：{ex.Message}";
            _logService.Log($"读取场景包失败: {ex}", LogEntryKind.Error);
        }
        finally
        {
            IsScenarioBusy = false;
        }
    }

    private void OnScreenshotCaptured(CapturedScreenshot captured) =>
        Dispatcher.UIThread.Post(() => SetScreenshot(captured));

    private void OnResourcesChanged(string _) => Dispatcher.UIThread.Post(() =>
    {
        // RefreshResources keeps the active pipeline text/history and scenario drafts,
        // while remapping their paths into the newly committed immutable view.
        RefreshResources();
        OnPropertyChanged(nameof(CanTestSystemReferenceMatch));
        OnPropertyChanged(nameof(CanSaveSystemReference));
    });

    private Task<T> RunCoreExclusiveAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default) =>
        _automationService.RunExclusiveAsync(async ct =>
        {
            await _automationService.InitializeAsync(ct);
            if (!PathEquals(_resourceRoot, _tools.ResourceRoot))
            {
                _refreshingResourcesForNativeOperation = true;
                try { RefreshResources(); }
                finally { _refreshingResourcesForNativeOperation = false; }
                if (!IsPipelineDirty && SelectedPipelineFile is { } activePipeline)
                    await LoadPipelineAsync(activePipeline.Path);
                if (SelectedScenarioManifest is { } activeManifest)
                    await LoadScenarioManifestAsync(activeManifest);
            }
            return await operation(ct);
        }, cancellationToken);

    private async Task InitializeCoreForDiagnosticsAsync()
    {
        await RunCoreExclusiveAsync(_ => Task.FromResult(true));
    }

    private void SetScreenshot(CapturedScreenshot captured)
    {
        try
        {
            _galleryCropSourcePath = null;
            _pipelineTemplateEditPath = null;
            _pipelineTemplateEditTask = null;
            using var stream = new MemoryStream(captured.PngBytes, writable: false);
            ScreenshotBitmap?.Dispose();
            ScreenshotBitmap = new Bitmap(stream);
            _screenshot = captured;
            HasScreenshot = true;
            SetCropRegion(null);
            ScreenshotInfoText = $"{captured.Width}×{captured.Height} px · {captured.CapturedAt:HH:mm:ss.fff}";
            ImageMatchStatus = "截图就绪；选择模板图像后运行原生匹配。";
            OnPropertyChanged(nameof(PipelineTemplateSaveLabel));
            OnPropertyChanged(nameof(CanEditPipelineTemplate));
            NotifyImageCommands();
        }
        catch (Exception ex)
        {
            ScreenshotInfoText = $"截图图像无法显示：{ex.Message}";
        }
    }

    private void SetCropRegion(ImageCropRegion? crop)
    {
        _cropRegion = crop;
        OnPropertyChanged(nameof(CropRegion));
        HasCropRegion = crop is { Width: > 0, Height: > 0 };
        CropRegionText = crop is { } region
            ? $"X {region.X}, Y {region.Y} · {region.Width} × {region.Height} px"
            : "未选择裁剪区域";
        OnPropertyChanged(nameof(CanSavePipelineTemplate));
        NotifyImageCommands();
    }

    private void NotifyImageCommands()
    {
        OnPropertyChanged(nameof(CanSaveSystemReference));
        OnPropertyChanged(nameof(CanTestSystemReferenceMatch));
        OnPropertyChanged(nameof(CanSavePipelineTemplate));
        OnPropertyChanged(nameof(CanEditPipelineTemplate));
    }

    private string CreateImageBackupPath(string sourcePath)
    {
        string directory = Path.Combine(Path.GetDirectoryName(sourcePath) ?? _tools.ResourceRoot, "backup");
        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        string extension = Path.GetExtension(sourcePath);
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string candidate = Path.Combine(directory, $"{stem}_{timestamp}{extension}");
        int suffix = 1;
        while (File.Exists(candidate)) candidate = Path.Combine(directory, $"{stem}_{timestamp}_{suffix++}{extension}");
        return candidate;
    }

    private bool IsUnderResourceRoot(string path)
    {
        string root = Path.GetFullPath(_tools.ResourceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars()
            .Concat("<>:\"/\\|?*")
            .ToHashSet();
        string safe = new(value.Select(character => invalid.Contains(character) || character is '/' or '\\' ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "template" : safe;
    }

    private void RestorePipelineText(string value)
    {
        _restoringPipelineHistory = true;
        PipelineJsonText = value;
        _restoringPipelineHistory = false;
        _pipelineHistoryBaseline = value;
        IsPipelineDirty = !string.Equals(value, _pipelineSavedText, StringComparison.Ordinal);
        RefreshPipelineDetails(value);
        NotifyHistory();
    }

    private void RefreshPipelineDetails(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("tasks", out var tasks)
                || tasks.ValueKind != JsonValueKind.Object)
            {
                PipelineTaskNames.Clear();
                PipelineGraphText = "No tasks are defined.";
                return;
            }

            var graph = new List<string>();
            PipelineTaskNames.Clear();
            foreach (var task in tasks.EnumerateObject())
            {
                PipelineTaskNames.Add(task.Name);
                var transitions = new List<string>();
                AddTransitions(task.Value, "next", "success", transitions);
                AddTransitions(task.Value, "onErrorNext", "error", transitions);
                string detail = transitions.Count == 0 ? "terminal" : string.Join(" · ", transitions);
                graph.Add($"{task.Name} → {detail}");
            }
            if (!PipelineTaskNames.Contains(SelectedPipelineTaskName))
                SelectedPipelineTaskName = PipelineTaskNames.FirstOrDefault() ?? string.Empty;
            PipelineGraphText = graph.Count == 0 ? "No tasks are defined." : string.Join(Environment.NewLine, graph);
        }
        catch (JsonException ex)
        {
            PipelineTaskNames.Clear();
            PipelineGraphText = $"Graph preview is unavailable until JSON parses: {ex.Message}";
        }

        static void AddTransitions(JsonElement task, string property, string label, ICollection<string> target)
        {
            if (!task.TryGetProperty(property, out var value)) return;
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { } single)
                target.Add($"{label}: {single}");
            else if (value.ValueKind == JsonValueKind.Array)
                target.Add($"{label}: {string.Join(", ", value.EnumerateArray().Select(item => item.ToString()))}");
        }
    }

    private static void PushHistory(Stack<string> stack, string value)
    {
        if (stack.Count == 0 || !string.Equals(stack.Peek(), value, StringComparison.Ordinal)) stack.Push(value);
        while (stack.Count > MaxHistoryEntries)
        {
            var values = stack.Take(MaxHistoryEntries).Reverse().ToArray();
            stack.Clear();
            foreach (var item in values) stack.Push(item);
        }
    }

    private void NotifyHistory()
    {
        OnPropertyChanged(nameof(CanUndoPipeline));
        OnPropertyChanged(nameof(CanRedoPipeline));
    }

    private void SetImageStatus(string status, LogEntryKind kind)
    {
        ImageMatchStatus = status;
        _logService.Log(status, kind);
    }

    private static string ReadDefaultEntryTask(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("start", out var start) && start.ValueKind == JsonValueKind.String)
                return start.GetString() ?? string.Empty;
            if (document.RootElement.TryGetProperty("tasks", out var tasks)
                && tasks.ValueKind == JsonValueKind.Object)
                return tasks.EnumerateObject().FirstOrDefault().Name ?? string.Empty;
        }
        catch (JsonException) { }
        return string.Empty;
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static WriteableBitmap CreateBitmap(int width, int height, byte[] rgba)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var framebuffer = bitmap.Lock();
        int rowBytes = checked(width * 4);
        for (int y = 0; y < height; y++)
            Marshal.Copy(rgba, y * rowBytes, IntPtr.Add(framebuffer.Address, y * framebuffer.RowBytes), rowBytes);
        return bitmap;
    }

    private void OnConnectionStateChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => IsConnected = _connectionStateService.State == ConnectionState.Connected);

    public void Dispose()
    {
        _connectionStateService.StateChanged -= OnConnectionStateChanged;
        _automationService.ScreenshotCaptured -= OnScreenshotCaptured;
        _automationService.ResourcesChanged -= OnResourcesChanged;
        _pipelineCancellation?.Cancel();
        _pipelineCancellation?.Dispose();
        _pipelineCancellation = null;
        ScreenshotBitmap?.Dispose();
        TemplatePreviewBitmap?.Dispose();
        SelectedUmaImagePreviewBitmap?.Dispose();
        foreach (var item in UmaDatabaseImages)
            item.Thumbnail?.Dispose();
    }

    public override string ToString() => "Developer Tools";
}

