using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.ViewModels;

public sealed class DeveloperToolsViewModel : INotifyPropertyChanged, IDisposable, IGrassTaskLogSink
{
    private const double ImageMatchTestThreshold = 0.86;
    private const double SystemReferenceMatchThreshold =
        DailyRaceRunnerSelector.MinimumSystemReferenceMatchScore;
    private static readonly double[] SystemReferenceScaleCandidates =
        [0.32, 0.40, 0.46, 0.50, 0.54, 0.58, 0.62, 0.66, 0.70, 0.74, 0.78, 0.84, 1.00];

    private static readonly JsonSerializerOptions PipelineJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IAdbRuntime _adbRuntime;
    private readonly IConnectionStateService _connectionState;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly IUmaDatabaseService _umaDatabase;
    private readonly HachimiJsonPipelineRunner _pipelineRunner;
    private readonly ObservableCollection<DeveloperToolsImageItem> _existingImages = [];
    private readonly ObservableCollection<string> _pipelineFiles = [];
    private readonly ObservableCollection<HachimiPipelineTaskEditorItem> _pipelineTasks = [];
    private readonly ObservableCollection<PipelineResourceOption> _pipelineResourceOptions = [];
    private readonly ObservableCollection<LogEntry> _pipelineRunLogs = [];
    private readonly Stack<PipelineEditorSnapshot> _pipelineUndo = [];
    private readonly Stack<PipelineEditorSnapshot> _pipelineRedo = [];
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
    private string _imageMatchTestStatus = "No image detection test has been run.";
    private BitmapSource? _imageMatchTestImage;
    private TemplateMatchResult? _imageMatchTestMatch;
    private bool _isPipelineBusy;
    private bool _isPipelineRunning;
    private bool _disposed;
    private CancellationTokenSource? _pipelineRunCancellation;
    private HachimiPipelineDefinition? _pipelineDefinition;
    private string? _selectedPipelineFile;
    private HachimiPipelineTaskEditorItem? _selectedPipelineTask;
    private string _pipelineName = "new-pipeline";
    private string _pipelineDescription = string.Empty;
    private string _pipelineReferenceWidthText = "900";
    private string _pipelineReferenceHeightText = "1600";
    private string _pipelineStatusText = "Select a JSON flow to edit.";
    private string _pipelineGraphText = "Load or create a flow to preview its transitions.";
    private string _pipelineSimulationText = "No simulation has been run.";
    private string _pipelineEntryTaskName = string.Empty;
    private string _pipelineRunStatusText = "No test run has started.";
    private string? _pipelineTemplateEditPath;
    private bool _isEditingPipelineTemplate;
    private PipelineResourceOption? _selectedPipelineResource;
    private HachimiPipelineTimingEditorItem _pipelineTiming =
        HachimiPipelineTimingEditorItem.CreateDefault();
    private PipelineEditorSnapshot? _pipelineHistoryBaseline;
    private bool _pipelineHistoryInitialized;
    private bool _isRestoringPipelineHistory;

    public DeveloperToolsViewModel(
        IAdbRuntime adbRuntime,
        IConnectionStateService connectionState,
        SettingsViewModel settingsViewModel,
        IUmaDatabaseService umaDatabase,
        HachimiJsonPipelineRunner pipelineRunner)
    {
        ArgumentNullException.ThrowIfNull(adbRuntime);
        ArgumentNullException.ThrowIfNull(connectionState);
        ArgumentNullException.ThrowIfNull(settingsViewModel);
        ArgumentNullException.ThrowIfNull(umaDatabase);
        ArgumentNullException.ThrowIfNull(pipelineRunner);

        _adbRuntime = adbRuntime;
        _connectionState = connectionState;
        _settingsViewModel = settingsViewModel;
        _umaDatabase = umaDatabase;
        _pipelineRunner = pipelineRunner;
        ExistingImages = new ReadOnlyObservableCollection<DeveloperToolsImageItem>(_existingImages);
        PipelineFiles = new ReadOnlyObservableCollection<string>(_pipelineFiles);
        PipelineTasks = new ReadOnlyObservableCollection<HachimiPipelineTaskEditorItem>(_pipelineTasks);
        PipelineResourceOptions = new ReadOnlyObservableCollection<PipelineResourceOption>(
            _pipelineResourceOptions);
        PipelineRunLogs = new ReadOnlyObservableCollection<LogEntry>(_pipelineRunLogs);
        _pipelineTiming.PropertyChanged += OnPipelineTimingPropertyChanged;
        _connectionState.StateChanged += OnConnectionStateChanged;
        _umaDatabase.DatabaseLoaded += OnUmaDatabaseLoaded;

        ConnectCommand = new RelayCommand(
            _ => _ = EnsureConnectedAsync(),
            _ => !_disposed && !_isBusy && !_isPipelineRunning);
        CaptureCommand = new RelayCommand(
            _ => _ = CaptureScreenshotAsync(),
            _ => !_disposed && !_isBusy && !_isPipelineRunning);
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
                && !_isEditingPipelineTemplate
                && !_isBusy
                && !_isLoadingImages
                && !_isSavingImage);
        TestImageMatchCommand = new RelayCommand(
            _ => _ = TestCurrentImageMatchAsync(),
            _ => !_disposed
                && !_isBusy
                && !_isPipelineRunning
                && !_isLoadingImages
                && !_isSavingImage
                && HasCropRegion);
        TestReferenceImageCommand = new RelayCommand(
            _ => _ = TestSelectedReferenceMatchAsync(),
            _ => !_disposed
                && !_isBusy
                && !_isPipelineRunning
                && !_isLoadingImages
                && !_isSavingImage
                && HasRunnerReferenceImage);

        RefreshPipelineFilesCommand = new RelayCommand(
            _ => RefreshPipelineFiles(),
            _ => !_disposed && !_isPipelineBusy);
        LoadPipelineCommand = new RelayCommand(
            _ => _ = LoadSelectedPipelineAsync(),
            _ => !_disposed && !_isPipelineBusy && !string.IsNullOrWhiteSpace(SelectedPipelineFile));
        NewPipelineCommand = new RelayCommand(
            _ => CreateNewPipeline(),
            _ => !_disposed && !_isPipelineBusy);
        SavePipelineCommand = new RelayCommand(
            _ => SavePipeline(),
            _ => !_disposed && !_isPipelineBusy && HasPipelineDefinition);
        ValidatePipelineCommand = new RelayCommand(
            _ => ValidatePipeline(),
            _ => !_disposed && !_isPipelineBusy && HasPipelineDefinition);
        AddPipelineTaskCommand = new RelayCommand(
            _ => AddPipelineTask(),
            _ => !_disposed && !_isPipelineBusy && HasPipelineDefinition);
        RemovePipelineTaskCommand = new RelayCommand(
            _ => RemoveSelectedPipelineTask(),
            _ => !_disposed && !_isPipelineBusy && SelectedPipelineTask is not null);
        UseScreenshotRoiCommand = new RelayCommand(
            _ => UseScreenshotRoi(),
            _ => !_disposed
                && !_isPipelineBusy
                && !_isEditingPipelineTemplate
                && SelectedPipelineTask is not null
                && HasCropRegion);
        ClearPipelineRoiCommand = new RelayCommand(
            _ => ClearPipelineRoi(),
            _ => !_disposed && !_isPipelineBusy && SelectedPipelineTask is not null);
        SavePipelineTemplateCommand = new RelayCommand(
            _ => SavePipelineTemplate(),
            _ => !_disposed
                && !_isPipelineBusy
                && SelectedPipelineTask is not null
                && HasScreenshot
                && HasCropRegion
                && HasPipelineDefinition);
        EditPipelineTemplateCommand = new RelayCommand(
            _ => EditPipelineTemplate(),
            _ => !_disposed
                && !_isPipelineBusy
                && HasPipelineDefinition
                && SelectedPipelineTask is not null
                && File.Exists(GetSelectedPipelineTemplatePath()));
        UndoPipelineCommand = new RelayCommand(
            _ => UndoPipelineChange(),
            _ => !_disposed && !_isPipelineBusy && CanUndoPipeline);
        RedoPipelineCommand = new RelayCommand(
            _ => RedoPipelineChange(),
            _ => !_disposed && !_isPipelineBusy && CanRedoPipeline);
        PreviewPipelineCommand = new RelayCommand(
            _ => PreviewPipeline(),
            _ => !_disposed && !_isPipelineBusy && HasPipelineDefinition);
        SimulatePipelineCommand = new RelayCommand(
            _ => SimulatePipeline(),
            _ => !_disposed && !_isPipelineBusy && HasPipelineDefinition);
        RunPipelineCommand = new RelayCommand(
            _ => _ = RunPipelineAsync(),
            _ => !_disposed
                && !_isPipelineBusy
                && !_isPipelineRunning
                && !_isBusy
                && HasPipelineDefinition
                && _pipelineTasks.Count > 0);
        StopPipelineCommand = new RelayCommand(
            _ => StopPipeline(),
            _ => !_disposed && _isPipelineRunning);

        _ = RefreshExistingImagesAsync();
        RefreshPipelineResourceOptions();
        RefreshPipelineFiles();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand ConnectCommand { get; }
    public ICommand CaptureCommand { get; }
    public ICommand SaveOriginalCommand { get; }
    public ICommand SaveCroppedCommand { get; }
    public ICommand ClearCropCommand { get; }
    public ICommand RefreshExistingImagesCommand { get; }
    public ICommand SaveSelectedImageCommand { get; }
    public ICommand TestImageMatchCommand { get; }
    public ICommand TestReferenceImageCommand { get; }
    public ICommand RefreshPipelineFilesCommand { get; }
    public ICommand LoadPipelineCommand { get; }
    public ICommand NewPipelineCommand { get; }
    public ICommand SavePipelineCommand { get; }
    public ICommand ValidatePipelineCommand { get; }
    public ICommand AddPipelineTaskCommand { get; }
    public ICommand RemovePipelineTaskCommand { get; }
    public ICommand UseScreenshotRoiCommand { get; }
    public ICommand ClearPipelineRoiCommand { get; }
    public ICommand SavePipelineTemplateCommand { get; }
    public ICommand EditPipelineTemplateCommand { get; }
    public ICommand UndoPipelineCommand { get; }
    public ICommand RedoPipelineCommand { get; }
    public ICommand PreviewPipelineCommand { get; }
    public ICommand SimulatePipelineCommand { get; }
    public ICommand RunPipelineCommand { get; }
    public ICommand StopPipelineCommand { get; }

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

    // The reference path is resolved when the test starts so a stale database
    // path cannot leave the button permanently disabled.
    public bool HasRunnerReferenceImage =>
        _selectedUmaImage is not null;

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
        ? image.IsLiveOutfit
            ? _umaDatabase.GetTraineeLiveOutfitReferenceImagePath(image.BaseCharacterId)
            : image.TraineeId is { } traineeId
                ? _umaDatabase.GetMaintenanceTraineeReferenceImagePath(traineeId)
                : string.Empty
        : string.Empty;

    public BitmapSource? ScreenshotImage => _screenshotImage;

    public BitmapSource? UmaImagePreviewImage => _imageMatchTestImage ?? _screenshotImage;

    public TemplateMatchResult? ImageMatchTestMatch => _imageMatchTestMatch;

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

    public string ImageMatchTestStatus => _imageMatchTestStatus;

    public ReadOnlyObservableCollection<string> PipelineFiles { get; }

    public ReadOnlyObservableCollection<HachimiPipelineTaskEditorItem> PipelineTasks { get; }

    public ReadOnlyObservableCollection<PipelineResourceOption> PipelineResourceOptions { get; }

    public ReadOnlyObservableCollection<LogEntry> PipelineRunLogs { get; }

    public PipelineResourceOption? SelectedPipelineResource
    {
        get => _selectedPipelineResource;
        set
        {
            if (ReferenceEquals(_selectedPipelineResource, value))
                return;

            _selectedPipelineResource = value;
            OnPropertyChanged();
            if (!_isRestoringPipelineHistory)
            {
                RefreshPipelineFiles();
            }
        }
    }

    public string? SelectedPipelineFile
    {
        get => _selectedPipelineFile;
        set
        {
            if (string.Equals(_selectedPipelineFile, value, StringComparison.OrdinalIgnoreCase))
                return;

            _selectedPipelineFile = value;
            OnPropertyChanged();
            RaisePipelineCommandStates();
        }
    }

    public HachimiPipelineTaskEditorItem? SelectedPipelineTask
    {
        get => _selectedPipelineTask;
        set
        {
            if (ReferenceEquals(_selectedPipelineTask, value))
                return;

            if (_isEditingPipelineTemplate)
                CancelPipelineTemplateEditing();

            _selectedPipelineTask = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPipelineTask));
            OnPropertyChanged(nameof(PipelineRoiText));
            RaisePipelineCommandStates();
        }
    }

    public string PipelineName
    {
        get => _pipelineName;
        set => SetPipelineProperty(ref _pipelineName, value);
    }

    public string PipelineDescription
    {
        get => _pipelineDescription;
        set => SetPipelineProperty(ref _pipelineDescription, value);
    }

    public string PipelineReferenceWidthText
    {
        get => _pipelineReferenceWidthText;
        set => SetPipelineProperty(ref _pipelineReferenceWidthText, value);
    }

    public string PipelineReferenceHeightText
    {
        get => _pipelineReferenceHeightText;
        set => SetPipelineProperty(ref _pipelineReferenceHeightText, value);
    }

    public HachimiPipelineTimingEditorItem PipelineTiming => _pipelineTiming;

    public string PipelineStatusText => _pipelineStatusText;

    public bool HasPipelineDefinition => _pipelineDefinition is not null;

    public bool HasPipelineTask => SelectedPipelineTask is not null;

    public string PipelineRoiText => SelectedPipelineTask?.RoiText ?? "No ROI selected";

    public string PipelineGraphText => _pipelineGraphText;

    public string PipelineSimulationText => _pipelineSimulationText;

    public string PipelineEntryTaskName
    {
        get => _pipelineEntryTaskName;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(_pipelineEntryTaskName, normalized, StringComparison.Ordinal))
                return;

            _pipelineEntryTaskName = normalized;
            OnPropertyChanged();
            RaisePipelineCommandStates();
        }
    }

    public string PipelineRunStatusText => _pipelineRunStatusText;

    public bool IsPipelineRunning => _isPipelineRunning;

    public bool IsEditingPipelineTemplate => _isEditingPipelineTemplate;

    public bool CanUndoPipeline => _pipelineUndo.Count > 0;

    public bool CanRedoPipeline => _pipelineRedo.Count > 0;

    public bool IsPipelineBusy => _isPipelineBusy;

    public void SetPipelineRoiFromSelection(Int32Rect? region)
    {
        SetCropRegion(region);
        if (_isEditingPipelineTemplate)
        {
            SetPipelineStatus(
                HasCropRegion
                    ? "Template crop selected. Save the edited template to update its ROI automatically."
                    : "Template crop cleared.");
            return;
        }

        if (SelectedPipelineTask is not null && region is { Width: > 0, Height: > 0 })
        {
            var screenshotWidth = _screenshotImage?.PixelWidth ?? 0;
            var screenshotHeight = _screenshotImage?.PixelHeight ?? 0;
            var referenceWidth = ParsePositiveInt(PipelineReferenceWidthText, screenshotWidth);
            var referenceHeight = ParsePositiveInt(PipelineReferenceHeightText, screenshotHeight);
            var x = ScaleToReference(region.Value.X, screenshotWidth, referenceWidth);
            var y = ScaleToReference(region.Value.Y, screenshotHeight, referenceHeight);
            var right = ScaleToReference(
                region.Value.X + region.Value.Width,
                screenshotWidth,
                referenceWidth);
            var bottom = ScaleToReference(
                region.Value.Y + region.Value.Height,
                screenshotHeight,
                referenceHeight);
            var width = Math.Max(1, right - x);
            var height = Math.Max(1, bottom - y);
            SelectedPipelineTask.RoiText =
                $"{x}, {y}, {width}, {height}";
            OnPropertyChanged(nameof(PipelineRoiText));
            SetPipelineStatus($"ROI filled from screenshot: {SelectedPipelineTask.RoiText}");
        }

        RaisePipelineCommandStates();
    }

    public void RefreshPipelineFiles()
    {
        var directory = GetPipelineDirectory();
        _pipelineFiles.Clear();
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json")
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                _pipelineFiles.Add(path);
            }
        }

        if (_selectedPipelineFile is null
            || !_pipelineFiles.Any(path => path.Equals(
                _selectedPipelineFile,
                StringComparison.OrdinalIgnoreCase)))
        {
            SelectedPipelineFile = _pipelineFiles.FirstOrDefault();
        }

        SetPipelineStatus(_pipelineFiles.Count == 0
            ? "No JSON flow definitions were found. Create a new flow to begin."
            : $"Found {_pipelineFiles.Count} JSON flow definition(s). Select one and click Load.");
    }

    public void RefreshPipelineResourceOptions()
    {
        var previousDirectory = _selectedPipelineResource?.Directory;
        _pipelineResourceOptions.Clear();

        var runtimeDirectory = GetRuntimePipelineDirectory();
        var sourceDirectory = FindSourcePipelineDirectory();
        var preferSource = sourceDirectory is not null
            && IsProjectBuildOutput(sourceDirectory);

        if (preferSource && sourceDirectory is not null)
        {
            AddPipelineResourceOption("Project source resources", sourceDirectory);
        }

        AddPipelineResourceOption("Runtime resources", runtimeDirectory);

        if (!preferSource
            && sourceDirectory is not null
            && !sourceDirectory.Equals(runtimeDirectory, StringComparison.OrdinalIgnoreCase))
        {
            AddPipelineResourceOption("Project source resources", sourceDirectory);
        }

        var selected = _pipelineResourceOptions.FirstOrDefault(option =>
            option.Directory.Equals(previousDirectory, StringComparison.OrdinalIgnoreCase))
            ?? _pipelineResourceOptions.FirstOrDefault();
        if (!ReferenceEquals(_selectedPipelineResource, selected))
        {
            _selectedPipelineResource = selected;
            OnPropertyChanged(nameof(SelectedPipelineResource));
        }
    }

    public async Task LoadSelectedPipelineAsync()
    {
        if (_disposed || _isPipelineBusy || string.IsNullOrWhiteSpace(SelectedPipelineFile))
            return;

        SetPipelineBusy(true);
        try
        {
            var path = Path.GetFullPath(SelectedPipelineFile);
            var definition = await HachimiPipelineDefinitionLoader.LoadAsync(path)
                .ConfigureAwait(true);
            if (definition is null)
            {
                SetPipelineStatus(
                    Path.GetFileName(path).Equals("start_game.json", StringComparison.OrdinalIgnoreCase)
                        ? "start_game.json uses the startup compatibility schema and is not editable by the ordinary flow editor."
                        : "The selected JSON is invalid, unsupported, or contains an unknown property.");
                return;
            }

            _pipelineDefinition = definition;
            PipelineName = definition.Name;
            PipelineDescription = definition.Description;
            PipelineReferenceWidthText = definition.ReferenceWidth.ToString(CultureInfo.InvariantCulture);
            PipelineReferenceHeightText = definition.ReferenceHeight.ToString(CultureInfo.InvariantCulture);
            SetPipelineTiming(definition.Timing ?? new HachimiPipelineTiming());
            ClearPipelineTaskItems();
            foreach (var pair in definition.Tasks)
            {
                var item = HachimiPipelineTaskEditorItem.FromTask(pair.Key, pair.Value);
                _pipelineTasks.Add(item);
                AttachPipelineTaskItem(item);
            }

            SelectedPipelineTask = _pipelineTasks.FirstOrDefault();
            PipelineEntryTaskName = _pipelineTasks.FirstOrDefault()?.Name ?? string.Empty;
            OnPropertyChanged(nameof(HasPipelineDefinition));
            OnPropertyChanged(nameof(PipelineTasks));
            SetPipelineStatus(
                $"Loaded {Path.GetFileName(path)}: {_pipelineTasks.Count} task(s). Drag on the screenshot to fill the selected task's ROI.");
            InitializePipelineHistory();
            RaisePipelineCommandStates();
        }
        catch (Exception exception)
        {
            SetPipelineStatus($"Could not load the JSON flow: {exception.Message}");
        }
        finally
        {
            SetPipelineBusy(false);
        }
    }

    public void CreateNewPipeline()
    {
        if (_disposed || _isPipelineBusy)
            return;

        _pipelineDefinition = new HachimiPipelineDefinition
        {
            Name = "new-pipeline",
            SchemaVersion = 1,
            Description = "",
            ReferenceWidth = 900,
            ReferenceHeight = 1600,
            BaseDirectory = GetPipelineDirectory(),
        };
        SelectedPipelineFile = null;
        PipelineName = _pipelineDefinition.Name;
        PipelineDescription = string.Empty;
            PipelineReferenceWidthText = "900";
            PipelineReferenceHeightText = "1600";
            SetPipelineTiming(new HachimiPipelineTiming());
        ClearPipelineTaskItems();
        var startTask = HachimiPipelineTaskEditorItem.Create("start");
        _pipelineTasks.Add(startTask);
        AttachPipelineTaskItem(startTask);
        SelectedPipelineTask = _pipelineTasks[0];
        PipelineEntryTaskName = startTask.Name;
        OnPropertyChanged(nameof(HasPipelineDefinition));
        SetPipelineStatus("New ordinary flow created. Fill the task fields, validate, then save.");
        InitializePipelineHistory();
        RaisePipelineCommandStates();
    }

    public void AddPipelineTask()
    {
        if (_pipelineDefinition is null)
            return;

        var baseName = "task";
        var index = 1;
        while (_pipelineTasks.Any(item => item.Name.Equals(
                   $"{baseName}{index}",
                   StringComparison.OrdinalIgnoreCase)))
        {
            index++;
        }

        RecordPipelineMutation();
        var item = HachimiPipelineTaskEditorItem.Create($"{baseName}{index}");
        _pipelineTasks.Add(item);
        AttachPipelineTaskItem(item);
        SelectedPipelineTask = item;
        SetPipelineStatus($"Added task '{item.Name}'.");
        CompletePipelineMutation();
    }

    public void RemoveSelectedPipelineTask()
    {
        if (SelectedPipelineTask is not { } item)
            return;

        RecordPipelineMutation();
        var index = _pipelineTasks.IndexOf(item);
        DetachPipelineTaskItem(item);
        _pipelineTasks.Remove(item);
        SelectedPipelineTask = _pipelineTasks.Count == 0
            ? null
            : _pipelineTasks[Math.Clamp(index, 0, _pipelineTasks.Count - 1)];
        SetPipelineStatus($"Removed task '{item.Name}'. Check transitions before saving.");
        CompletePipelineMutation();
    }

    public void UseScreenshotRoi()
    {
        if (SelectedPipelineTask is null || CropRegion is not { } region)
            return;

        SetPipelineRoiFromSelection(region);
    }

    public void ClearPipelineRoi()
    {
        if (SelectedPipelineTask is null)
            return;

        SelectedPipelineTask.RoiText = string.Empty;
        OnPropertyChanged(nameof(PipelineRoiText));
        SetPipelineStatus($"Cleared ROI for task '{SelectedPipelineTask.Name}'.");
        RaisePipelineCommandStates();
    }

    public void SavePipelineTemplate()
    {
        if (_isEditingPipelineTemplate)
        {
            SaveEditedPipelineTemplate();
            return;
        }

        if (_pipelineDefinition is null
            || SelectedPipelineTask is null
            || _screenshotImage is null
            || CropRegion is not { } region)
        {
            return;
        }

        try
        {
            var pipelineSlug = MakeSafeFileName(Path.GetFileNameWithoutExtension(
                SelectedPipelineFile ?? PipelineName));
            var taskSlug = MakeSafeFileName(SelectedPipelineTask.Name);
            var templateDirectory = Path.Combine(
                _pipelineDefinition.BaseDirectory,
                "templates",
                pipelineSlug);
            Directory.CreateDirectory(templateDirectory);
            var absolutePath = Path.Combine(templateDirectory, $"{taskSlug}.png");
            var cropped = new CroppedBitmap(_screenshotImage, region);
            cropped.Freeze();
            ScreenshotBitmapCodec.SavePng(cropped, absolutePath);
            SelectedPipelineTask.Template =
                Path.GetRelativePath(_pipelineDefinition.BaseDirectory, absolutePath)
                    .Replace(Path.DirectorySeparatorChar, '/');
            SetPipelineStatus($"Saved template and filled template path: {SelectedPipelineTask.Template}");
        }
        catch (Exception exception)
        {
            SetPipelineStatus($"Could not save the template: {exception.Message}");
        }
    }

    public void EditPipelineTemplate()
    {
        if (_pipelineDefinition is null || SelectedPipelineTask is null)
            return;

        var templatePath = GetSelectedPipelineTemplatePath();
        if (!File.Exists(templatePath))
        {
            SetPipelineStatus("The selected task template does not exist on disk.");
            return;
        }

        try
        {
            var bitmap = UmaImageCodec.Load(templatePath);
            _screenshot = null;
            _screenshotImage = bitmap;
            _cropRegion = null;
            _activeImagePath = null;
            _pipelineTemplateEditPath = templatePath;
            _isEditingPipelineTemplate = true;
            _captureDetails =
                $"{bitmap.PixelWidth} x {bitmap.PixelHeight} | existing pipeline template";
            OnPropertyChanged(nameof(IsEditingPipelineTemplate));
            SetStatus(
                "Template loaded. Drag a smaller crop in the preview, then save to update the template and ROI.");
            SetPipelineStatus(
                "Editing the current template. The existing ROI will be scaled to the new crop when saved.");
            NotifyScreenshotPropertiesChanged();
        }
        catch (Exception exception)
        {
            SetPipelineStatus($"Could not load the current template: {exception.Message}");
        }
    }

    private void SaveEditedPipelineTemplate()
    {
        if (_pipelineDefinition is null
            || SelectedPipelineTask is null
            || _screenshotImage is null
            || CropRegion is not { } crop
            || string.IsNullOrWhiteSpace(_pipelineTemplateEditPath))
        {
            return;
        }

        var templatePath = _pipelineTemplateEditPath;
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(templatePath) ?? _pipelineDefinition.BaseDirectory,
            $".{Path.GetFileNameWithoutExtension(templatePath)}.{Guid.NewGuid():N}.tmp"
                + Path.GetExtension(templatePath));
        string? backupPath = null;
        try
        {
            var oldWidth = _screenshotImage.PixelWidth;
            var oldHeight = _screenshotImage.PixelHeight;
            var cropped = new CroppedBitmap(_screenshotImage, crop);
            cropped.Freeze();

            backupPath = CreateBackupPath(templatePath);
            File.Copy(templatePath, backupPath);
            UmaImageCodec.Save(cropped, temporaryPath);
            File.Move(temporaryPath, templatePath, overwrite: true);

            var oldRoi = TryParseRect(SelectedPipelineTask.RoiText);
            var automaticRoi = oldRoi is not null
                ? MapTemplateCropToRoi(
                    crop,
                    oldWidth,
                    oldHeight,
                    oldRoi,
                    ParsePositiveInt(PipelineReferenceWidthText, 900),
                    ParsePositiveInt(PipelineReferenceHeightText, 1600))
                : null;
            if (automaticRoi is not null)
            {
                SelectedPipelineTask.RoiText = FormatRect(automaticRoi);
                OnPropertyChanged(nameof(PipelineRoiText));
                SetPipelineStatus(
                    $"Updated template and automatic ROI: {SelectedPipelineTask.RoiText}. Backup: {backupPath}");
            }
            else
            {
                SetPipelineStatus(
                    $"Updated template. Existing ROI was empty or invalid, so it was left unchanged. Backup: {backupPath}");
            }

            CancelPipelineTemplateEditing();
        }
        catch (Exception exception)
        {
            SetPipelineStatus($"Could not save the edited template: {exception.Message}");
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public void ValidatePipeline()
    {
        if (_pipelineDefinition is null)
            return;

        try
        {
            var definition = BuildPipelineDefinition();
            var errors = ValidateDefinition(definition);
            SetPipelineStatus(errors.Count == 0
                ? $"Validation passed: {definition.Tasks.Count} task(s), schema version {definition.SchemaVersion}."
                : "Validation failed: " + string.Join(" | ", errors));
        }
        catch (Exception exception)
        {
            SetPipelineStatus($"Validation failed: {exception.Message}");
        }
    }

    public void SavePipeline()
    {
        if (_pipelineDefinition is null)
            return;

        try
        {
            var definition = BuildPipelineDefinition();
            var errors = ValidateDefinition(definition);
            if (errors.Count > 0)
            {
                SetPipelineStatus("Save blocked: " + string.Join(" | ", errors));
                return;
            }

            var path = SelectedPipelineFile;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = ShowPipelineSaveDialog();
                if (path is null)
                    return;
            }

            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(definition, PipelineJsonOptions));
            _pipelineDefinition = definition;
            SelectedPipelineFile = path;
            RefreshPipelineFiles();
            SetPipelineStatus($"Saved flow: {path}");
        }
        catch (Exception exception)
        {
            SetPipelineStatus($"Could not save the JSON flow: {exception.Message}");
        }
    }

    private HachimiPipelineDefinition BuildPipelineDefinition()
    {
        if (_pipelineDefinition is null)
            throw new InvalidOperationException("No pipeline is loaded.");

        if (!int.TryParse(PipelineReferenceWidthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            || width <= 0)
        {
            throw new FormatException("Reference width must be a positive integer.");
        }

        if (!int.TryParse(PipelineReferenceHeightText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            || height <= 0)
        {
            throw new FormatException("Reference height must be a positive integer.");
        }

        var definition = new HachimiPipelineDefinition
        {
            Name = string.IsNullOrWhiteSpace(PipelineName) ? "new-pipeline" : PipelineName.Trim(),
            SchemaVersion = 1,
            Description = PipelineDescription?.Trim() ?? string.Empty,
            ReferenceWidth = width,
            ReferenceHeight = height,
            Templates = _pipelineDefinition.Templates ?? new HachimiPipelineTemplates(),
            Uma = _pipelineDefinition.Uma,
            Timing = _pipelineTiming.ToTiming(),
            BaseDirectory = _pipelineDefinition.BaseDirectory,
        };

        foreach (var item in _pipelineTasks)
        {
            if (definition.Tasks.ContainsKey(item.Name))
                throw new FormatException($"Duplicate task name '{item.Name}'.");

            definition.Tasks[item.Name] = item.ToTask();
        }

        return definition;
    }

    private static List<string> ValidateDefinition(HachimiPipelineDefinition definition)
    {
        var errors = new List<string>();
        var supportedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "",
            "back",
            "capturescreenshot",
            "clickrect",
            "clickself",
            "donothing",
            "runpipeline",
            "screenshot",
            "selectdailyracerunner",
            "stop",
            "swipe",
            "wait",
        };
        if (definition.Tasks.Count == 0)
            errors.Add("the flow needs at least one task");

        foreach (var pair in definition.Tasks)
        {
            var name = pair.Key;
            var task = pair.Value;
            var action = Normalize(task.Action);
            var algorithm = Normalize(task.Algorithm);
            if (!supportedActions.Contains(action))
                errors.Add($"{name}: unsupported action '{task.Action}'");
            if (task.Template is not null && !File.Exists(Path.Combine(definition.BaseDirectory, task.Template)))
            {
                errors.Add($"{name}: template not found ({task.Template})");
            }

            if (action is "clickself" or "wait" && string.IsNullOrWhiteSpace(task.Template))
                errors.Add($"{name}: action {task.Action} requires a template");
            if (action == "clickrect" && !HasArray(task.SpecificRect, 4))
                errors.Add($"{name}: ClickRect requires specificRect with 4 values");
            if (action == "swipe" && !HasArray(task.Swipe, 5))
                errors.Add($"{name}: Swipe requires 5 coordinates");
            if (action == "runpipeline" && string.IsNullOrWhiteSpace(task.Pipeline))
                errors.Add($"{name}: RunPipeline requires pipeline");
            if (algorithm is "parallelmonitor" or "raceresultmonitor"
                && (task.MonitorTasks.Count == 0 || string.IsNullOrWhiteSpace(task.SuccessTask)))
            {
                errors.Add($"{name}: monitor requires monitorTasks and successTask");
            }

            foreach (var reference in task.Next
                         .Concat(task.OnErrorNext)
                         .Concat(task.ExceededNext)
                         .Concat(task.Sub)
                         .Concat(task.MonitorTasks)
                         .Append(task.SuccessTask ?? string.Empty)
                         .Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                if (!definition.Tasks.ContainsKey(reference))
                    errors.Add($"{name}: transition references missing task '{reference}'");
            }
        }

        return errors;
    }

    private static bool HasArray(int[]? values, int length) =>
        values is not null && values.Length == length;

    private static string Normalize(string? value) =>
        value?.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant()
        ?? string.Empty;

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character =>
            invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "pipeline" : result;
    }

    private static int ParsePositiveInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
            ? parsed
            : Math.Max(1, fallback);

    private static int ScaleToReference(int value, int actual, int reference) =>
        actual <= 0
            ? value
            : (int)Math.Round(value * (double)reference / actual);

    private string GetSelectedPipelineTemplatePath()
    {
        if (_pipelineDefinition is null
            || SelectedPipelineTask is null
            || string.IsNullOrWhiteSpace(SelectedPipelineTask.Template))
        {
            return string.Empty;
        }

        var template = SelectedPipelineTask.Template.Trim();
        return Path.GetFullPath(Path.IsPathRooted(template)
            ? template
            : Path.Combine(_pipelineDefinition.BaseDirectory, template));
    }

    private void ClearPipelineTemplateEditState()
    {
        if (!_isEditingPipelineTemplate && _pipelineTemplateEditPath is null)
            return;

        _pipelineTemplateEditPath = null;
        _isEditingPipelineTemplate = false;
        OnPropertyChanged(nameof(IsEditingPipelineTemplate));
        RaisePipelineCommandStates();
    }

    private void CancelPipelineTemplateEditing()
    {
        if (!_isEditingPipelineTemplate)
            return;

        ClearPipelineTemplateEditState();
        _screenshot = null;
        _screenshotImage = null;
        _cropRegion = null;
        _captureDetails = string.Empty;
        NotifyScreenshotPropertiesChanged();
    }

    private static int[]? TryParseRect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var values = text
            .Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
        if (values.Length != 4
            || !values.All(value => int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _)))
        {
            return null;
        }

        var rect = values
            .Select(value => int.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();
        return rect[2] > 0 && rect[3] > 0 && rect[0] >= 0 && rect[1] >= 0
            ? rect
            : null;
    }

    private static int[]? MapTemplateCropToRoi(
        Int32Rect crop,
        int templateWidth,
        int templateHeight,
        int[] oldRoi,
        int referenceWidth,
        int referenceHeight)
    {
        if (templateWidth <= 0
            || templateHeight <= 0
            || oldRoi.Length != 4
            || oldRoi[2] <= 0
            || oldRoi[3] <= 0)
        {
            return null;
        }

        var left = oldRoi[0] + (int)Math.Round(
            crop.X * oldRoi[2] / (double)templateWidth);
        var top = oldRoi[1] + (int)Math.Round(
            crop.Y * oldRoi[3] / (double)templateHeight);
        var right = oldRoi[0] + (int)Math.Round(
            (crop.X + crop.Width) * oldRoi[2] / (double)templateWidth);
        var bottom = oldRoi[1] + (int)Math.Round(
            (crop.Y + crop.Height) * oldRoi[3] / (double)templateHeight);

        left = Math.Clamp(left, 0, Math.Max(0, referenceWidth - 1));
        top = Math.Clamp(top, 0, Math.Max(0, referenceHeight - 1));
        right = Math.Clamp(right, left + 1, Math.Max(left + 1, referenceWidth));
        bottom = Math.Clamp(bottom, top + 1, Math.Max(top + 1, referenceHeight));
        return [left, top, right - left, bottom - top];
    }

    private static string FormatRect(IReadOnlyList<int> rect) =>
        string.Join(", ", rect);

    private string GetPipelineDirectory() =>
        _selectedPipelineResource?.Directory ?? GetRuntimePipelineDirectory();

    private static string GetRuntimePipelineDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "resource", "hachimi");

    private static string? FindSourcePipelineDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "resource", "hachimi");
            if (File.Exists(Path.Combine(directory.FullName, "CMakePresets.json"))
                && Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool IsProjectBuildOutput(string sourcePipelineDirectory)
    {
        var sourceRoot = Directory.GetParent(sourcePipelineDirectory)?.Parent?.FullName;
        if (string.IsNullOrWhiteSpace(sourceRoot))
            return false;

        var buildOutputRoot = Path.Combine(
            sourceRoot,
            "src",
            "UmamusumeWpfGui",
            "bin");
        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedBuildOutputRoot = Path.GetFullPath(buildOutputRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return baseDirectory.StartsWith(
            normalizedBuildOutputRoot,
            StringComparison.OrdinalIgnoreCase);
    }

    private void AddPipelineResourceOption(string displayName, string directory)
    {
        if (!Directory.Exists(directory))
            return;

        _pipelineResourceOptions.Add(new PipelineResourceOption(
            $"{displayName}: {directory}",
            directory));
    }

    private string? ShowPipelineSaveDialog()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Hachimi JSON flow|*.json|All files|*.*",
            DefaultExt = ".json",
            AddExtension = true,
            InitialDirectory = GetPipelineDirectory(),
            FileName = "new_pipeline.json",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void SetPipelineProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        OnPropertyChanged(propertyName);
        RecordPipelineMutation();
        CompletePipelineMutation();
        RefreshPipelinePreviewText();
    }

    private void SetPipelineTiming(HachimiPipelineTiming timing)
    {
        SetPipelineTiming(HachimiPipelineTimingEditorItem.FromTiming(timing));
    }

    private void SetPipelineTiming(HachimiPipelineTimingEditorItem timing)
    {
        _pipelineTiming.PropertyChanged -= OnPipelineTimingPropertyChanged;
        _pipelineTiming = timing.Clone();
        _pipelineTiming.PropertyChanged += OnPipelineTimingPropertyChanged;
        OnPropertyChanged(nameof(PipelineTiming));
        RefreshPipelinePreviewText();
    }

    private void OnPipelineTimingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isRestoringPipelineHistory)
            return;

        RecordPipelineMutation();
        CompletePipelineMutation();
        RaisePipelineCommandStates();
    }

    private void AttachPipelineTaskItem(HachimiPipelineTaskEditorItem item) =>
        item.PropertyChanged += OnPipelineTaskEditorPropertyChanged;

    private void DetachPipelineTaskItem(HachimiPipelineTaskEditorItem item) =>
        item.PropertyChanged -= OnPipelineTaskEditorPropertyChanged;

    private void ClearPipelineTaskItems()
    {
        foreach (var item in _pipelineTasks)
            DetachPipelineTaskItem(item);

        _pipelineTasks.Clear();
    }

    private void OnPipelineTaskEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isRestoringPipelineHistory)
            return;

        RecordPipelineMutation();
        CompletePipelineMutation();
        OnPropertyChanged(nameof(PipelineRoiText));
        RefreshPipelinePreviewText();
        RaisePipelineCommandStates();
    }

    private void InitializePipelineHistory()
    {
        _pipelineUndo.Clear();
        _pipelineRedo.Clear();
        _pipelineHistoryInitialized = true;
        _pipelineHistoryBaseline = CapturePipelineSnapshot();
        RefreshPipelinePreviewText();
        RaisePipelineCommandStates();
    }

    private void RecordPipelineMutation()
    {
        if (!_pipelineHistoryInitialized || _isRestoringPipelineHistory)
            return;

        _pipelineUndo.Push(_pipelineHistoryBaseline ?? CapturePipelineSnapshot());
        _pipelineRedo.Clear();
    }

    private void CompletePipelineMutation()
    {
        if (!_pipelineHistoryInitialized || _isRestoringPipelineHistory)
            return;

        _pipelineHistoryBaseline = CapturePipelineSnapshot();
        RaisePipelineCommandStates();
    }

    private PipelineEditorSnapshot CapturePipelineSnapshot() => new(
        SelectedPipelineFile,
        _pipelineName,
        _pipelineDescription,
        _pipelineReferenceWidthText,
        _pipelineReferenceHeightText,
        _pipelineTiming.Clone(),
        _pipelineTasks.Select(item => item.Clone()).ToList(),
        SelectedPipelineTask?.Name);

    private void UndoPipelineChange()
    {
        if (!CanUndoPipeline)
            return;

        _pipelineRedo.Push(CapturePipelineSnapshot());
        RestorePipelineSnapshot(_pipelineUndo.Pop());
        SetPipelineStatus("Undid the last flow edit.");
    }

    private void RedoPipelineChange()
    {
        if (!CanRedoPipeline)
            return;

        _pipelineUndo.Push(CapturePipelineSnapshot());
        RestorePipelineSnapshot(_pipelineRedo.Pop());
        SetPipelineStatus("Redid the flow edit.");
    }

    private void RestorePipelineSnapshot(PipelineEditorSnapshot snapshot)
    {
        _isRestoringPipelineHistory = true;
        try
        {
            SelectedPipelineFile = snapshot.SelectedPipelineFile;
            _pipelineName = snapshot.PipelineName;
            _pipelineDescription = snapshot.PipelineDescription;
            _pipelineReferenceWidthText = snapshot.PipelineReferenceWidthText;
            _pipelineReferenceHeightText = snapshot.PipelineReferenceHeightText;
            OnPropertyChanged(nameof(PipelineName));
            OnPropertyChanged(nameof(PipelineDescription));
            OnPropertyChanged(nameof(PipelineReferenceWidthText));
            OnPropertyChanged(nameof(PipelineReferenceHeightText));
            SetPipelineTiming(snapshot.Timing);
            ClearPipelineTaskItems();
            foreach (var item in snapshot.Tasks.Select(item => item.Clone()))
            {
                _pipelineTasks.Add(item);
                AttachPipelineTaskItem(item);
            }

            SelectedPipelineTask = snapshot.SelectedTaskName is { Length: > 0 } name
                ? _pipelineTasks.FirstOrDefault(item => item.Name.Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase))
                : _pipelineTasks.FirstOrDefault();
            if (!_pipelineTasks.Any(item => item.Name.Equals(
                    PipelineEntryTaskName,
                    StringComparison.OrdinalIgnoreCase)))
            {
                PipelineEntryTaskName = _pipelineTasks.FirstOrDefault()?.Name ?? string.Empty;
            }
            _pipelineHistoryBaseline = CapturePipelineSnapshot();
            RefreshPipelineFiles();
            RefreshPipelinePreviewText();
        }
        finally
        {
            _isRestoringPipelineHistory = false;
            OnPropertyChanged(nameof(CanUndoPipeline));
            OnPropertyChanged(nameof(CanRedoPipeline));
            RaisePipelineCommandStates();
        }
    }

    private void PreviewPipeline()
    {
        RefreshPipelinePreviewText();
        SetPipelineStatus("Flow preview refreshed.");
    }

    private void SimulatePipeline()
    {
        try
        {
            var definition = BuildPipelineDefinition();
            var current = definition.Tasks.Keys.FirstOrDefault();
            if (current is null)
            {
                SetPipelineStatus("Simulation blocked: the flow has no tasks.");
                return;
            }

            var lines = new List<string> { $"Entry: {current}" };
            var visited = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var guard = Math.Max(100, definition.Tasks.Count * 10);
            for (var index = 0; index < guard && !string.IsNullOrWhiteSpace(current); index++)
            {
                if (!definition.TryGetTask(current, out var task) || task is null)
                {
                    lines.Add($"STOP: missing task '{current}'");
                    break;
                }

                visited[current] = visited.TryGetValue(current, out var count) ? count + 1 : 1;
                lines.Add($"{index + 1:00}. {current} [{task.Action}] -> {string.Join(", ", task.Next)}");
                if (task.Success)
                {
                    lines.Add("STOP: task marked success");
                    break;
                }

                if (Normalize(task.Action) == "stop")
                {
                    lines.Add("STOP: task requests stop");
                    break;
                }

                var next = task.Next.FirstOrDefault(name => definition.Tasks.ContainsKey(name));
                if (next is null)
                {
                    lines.Add("STOP: no success transition");
                    break;
                }

                if (visited.TryGetValue(next, out var nextCount) && nextCount >= 1)
                {
                    lines.Add($"LOOP: '{next}' would repeat; runtime maxTimes/overrides decide when it exits.");
                    break;
                }

                current = next;
            }

            _pipelineSimulationText = string.Join(Environment.NewLine, lines);
            OnPropertyChanged(nameof(PipelineSimulationText));
            SetPipelineStatus("Offline simulation completed. It follows the first success transition and does not touch the emulator.");
        }
        catch (Exception exception)
        {
            _pipelineSimulationText = $"Simulation failed: {exception.Message}";
            OnPropertyChanged(nameof(PipelineSimulationText));
            SetPipelineStatus(_pipelineSimulationText);
        }
    }

    public async Task RunPipelineAsync()
    {
        if (_disposed
            || _isPipelineBusy
            || _isPipelineRunning
            || _isBusy
            || _pipelineDefinition is null)
        {
            return;
        }

        HachimiPipelineDefinition definition;
        try
        {
            definition = BuildPipelineDefinition();
            var errors = ValidateDefinition(definition);
            if (errors.Count > 0)
            {
                SetPipelineRunStatus("Test blocked: " + string.Join(" | ", errors));
                SetPipelineStatus("Test blocked by validation errors. Fix the flow before running it.");
                return;
            }

            var entryTask = PipelineEntryTaskName;
            if (string.IsNullOrWhiteSpace(entryTask))
            {
                entryTask = definition.Tasks.Keys.FirstOrDefault() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(entryTask)
                || !definition.Tasks.ContainsKey(entryTask))
            {
                SetPipelineRunStatus("Test blocked: choose a valid entry task.");
                SetPipelineStatus("Test blocked: the selected entry task does not exist.");
                return;
            }

            PipelineEntryTaskName = entryTask;
        }
        catch (Exception exception)
        {
            SetPipelineRunStatus($"Test blocked: {exception.Message}");
            SetPipelineStatus($"Test blocked: {exception.Message}");
            return;
        }

        _pipelineRunLogs.Clear();
        OnPropertyChanged(nameof(PipelineRunLogs));
        SetPipelineRunStatus("Connecting to the emulator...");
        _pipelineRunCancellation = new CancellationTokenSource();
        _isPipelineRunning = true;
        OnPropertyChanged(nameof(IsPipelineRunning));
        SetPipelineBusy(true);
        RaiseCommandStates();

        try
        {
            Add("Developer test", $"Starting '{definition.Name}' from entry task '{PipelineEntryTaskName}'.");
            await _settingsViewModel.ConnectAsync(_pipelineRunCancellation.Token)
                .ConfigureAwait(true);
            var connection = LastVerifiedConnection;
            if (ConnectionState != ConnectionState.Connected || connection is null)
            {
                Add(
                    "Developer test",
                    "No verified emulator connection is available.",
                    LogEntryKind.Failure);
                SetPipelineRunStatus("Test stopped: connect the emulator in Settings first.");
                return;
            }

            SetPipelineRunStatus($"Running '{definition.Name}'...");
            var result = await _pipelineRunner.RunAsync(
                    connection,
                    definition,
                    PipelineEntryTaskName,
                    logSink: this,
                    cancellationToken: _pipelineRunCancellation.Token)
                .ConfigureAwait(true);

            SetPipelineRunStatus(result.Succeeded
                ? $"Test passed: {result.Message}"
                : $"Test failed: {result.Message}");
            SetPipelineStatus(SetPipelineRunSummary(result));
        }
        catch (OperationCanceledException)
        {
            Add("Developer test", "Cancellation requested.", LogEntryKind.Info);
            SetPipelineRunStatus("Test canceled.");
            SetPipelineStatus("JSON test run canceled.");
        }
        catch (Exception exception)
        {
            Add("Developer test", exception.Message, LogEntryKind.Failure);
            SetPipelineRunStatus($"Test failed: {exception.Message}");
            SetPipelineStatus($"JSON test run failed: {exception.Message}");
        }
        finally
        {
            _pipelineRunCancellation?.Dispose();
            _pipelineRunCancellation = null;
            _isPipelineRunning = false;
            OnPropertyChanged(nameof(IsPipelineRunning));
            SetPipelineBusy(false);
            RaiseCommandStates();
        }
    }

    public void StopPipeline()
    {
        if (!_isPipelineRunning)
            return;

        SetPipelineRunStatus("Stopping test run...");
        Add("Developer test", "Stop requested by the user.");
        _pipelineRunCancellation?.Cancel();
    }

    public void Add(string type, string details, LogEntryKind kind = LogEntryKind.Info)
    {
        if (_disposed)
            return;

        var entry = new LogEntry(DateTimeOffset.UtcNow, type, details, kind);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            try
            {
                dispatcher.Invoke(() => AppendPipelineRunLog(entry));
            }
            catch (InvalidOperationException)
            {
                // The WPF dispatcher may already be shutting down.
            }

            return;
        }

        AppendPipelineRunLog(entry);
    }

    private void AppendPipelineRunLog(LogEntry entry)
    {
        if (_disposed)
            return;

        _pipelineRunLogs.Add(entry);
        if (_pipelineRunLogs.Count > 500)
            _pipelineRunLogs.RemoveAt(0);

        OnPropertyChanged(nameof(PipelineRunLogs));
    }

    private void SetPipelineRunStatus(string status)
    {
        _pipelineRunStatusText = status;
        OnPropertyChanged(nameof(PipelineRunStatusText));
    }

    private static string SetPipelineRunSummary(HachimiPipelineRunResult result) =>
        result.Succeeded
            ? $"JSON test passed: {result.Message} Completed tasks: {result.CompletedUnits}."
            : $"JSON test failed: {result.Message} Last task: {result.LastTask ?? "none"}.";

    private void RefreshPipelinePreviewText()
    {
        if (_pipelineTasks.Count == 0)
        {
            _pipelineGraphText = "No tasks to preview.";
        }
        else
        {
            var lines = new List<string>();
            foreach (var task in _pipelineTasks)
            {
                var branches = new List<string>();
                AddGraphBranch(branches, "next", task.NextText);
                AddGraphBranch(branches, "error", task.OnErrorNextText);
                AddGraphBranch(branches, "exceeded", task.ExceededNextText);
                lines.Add(branches.Count == 0
                    ? $"{task.Name} [{task.Action}]"
                    : $"{task.Name} [{task.Action}]  {string.Join("  ", branches)}");
            }

            _pipelineGraphText = string.Join(Environment.NewLine, lines);
        }

        OnPropertyChanged(nameof(PipelineGraphText));
    }

    private static void AddGraphBranch(List<string> branches, string label, string text)
    {
        var values = text
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToArray();
        if (values.Length > 0)
            branches.Add($"--{label}--> {string.Join(", ", values)}");
    }

    private void SetPipelineStatus(string status)
    {
        _pipelineStatusText = status;
        OnPropertyChanged(nameof(PipelineStatusText));
    }

    private void SetPipelineBusy(bool busy)
    {
        _isPipelineBusy = busy;
        OnPropertyChanged(nameof(IsPipelineBusy));
        RaisePipelineCommandStates();
    }

    private void RaisePipelineCommandStates()
    {
        foreach (var command in new ICommand?[]
        {
            RefreshPipelineFilesCommand,
            LoadPipelineCommand,
            NewPipelineCommand,
            SavePipelineCommand,
            ValidatePipelineCommand,
            AddPipelineTaskCommand,
            RemovePipelineTaskCommand,
            UseScreenshotRoiCommand,
            ClearPipelineRoiCommand,
            SavePipelineTemplateCommand,
            EditPipelineTemplateCommand,
            UndoPipelineCommand,
            RedoPipelineCommand,
            PreviewPipelineCommand,
            SimulatePipelineCommand,
            RunPipelineCommand,
            StopPipelineCommand,
        })
        {
            if (command is RelayCommand relayCommand)
                relayCommand.RaiseCanExecuteChanged();
        }
    }

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
            ClearImageMatchPreview();
            SetImageMatchTestStatus("No image detection test has been run.");
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

    public async Task TestCurrentImageMatchAsync()
    {
        if (_disposed
            || _isBusy
            || _isPipelineRunning
            || _isLoadingImages
            || _isSavingImage
            || !HasCropRegion
            || _cropRegion is not { } region)
        {
            return;
        }

        SetBusy(true);
        ClearImageMatchPreview();
        try
        {
            var templateSource = GetImageMatchTemplateSource();
            var template = templateSource is null
                ? null
                : GrayImageCodec.Crop(templateSource, region);
            if (template is null)
            {
                SetImageMatchTestStatus(
                    "The selected crop could not be converted into a template.");
                return;
            }

            await _settingsViewModel.ConnectAsync().ConfigureAwait(true);
            var connection = LastVerifiedConnection;
            if (ConnectionState != ConnectionState.Connected || connection is null)
            {
                SetImageMatchTestStatus(
                    "Test blocked: connect the emulator in Settings first.");
                return;
            }

            SetImageMatchTestStatus("Testing the selected crop on the current emulator page...");
            var capture = await _adbRuntime.CaptureBestScreenshotAsync(
                connection.AdbPath,
                connection.Serial).ConfigureAwait(true);
            if (!capture.Succeeded || capture.Screenshot is null)
            {
                SetImageMatchTestStatus(DescribeCaptureFailure(capture));
                return;
            }

            var screen = GrayImageCodec.FromScreenshot(capture.Screenshot);
            if (screen is null)
            {
                SetImageMatchTestStatus(
                    "The emulator returned an unsupported screenshot format.");
                return;
            }

            var match = await Task.Run(() => TemplateMatcher.Find(
                screen,
                template,
                roi: null,
                threshold: ImageMatchTestThreshold,
                referenceWidth: screen.Width,
                referenceHeight: screen.Height)).ConfigureAwait(true);
            _imageMatchTestImage = ScreenshotBitmapCodec.ToBitmapSource(capture.Screenshot);
            _imageMatchTestMatch = match;
            OnPropertyChanged(nameof(UmaImagePreviewImage));
            OnPropertyChanged(nameof(ImageMatchTestMatch));
            var score = match.Score.ToString("0.000", CultureInfo.InvariantCulture);
            var threshold = ImageMatchTestThreshold.ToString("0.000", CultureInfo.InvariantCulture);
            SetImageMatchTestStatus(match.Found
                ? $"Detected on the current page. Score: {score} / threshold {threshold}; "
                  + $"position: ({match.X}, {match.Y}); template: {template.Width} x {template.Height}."
                : $"Not detected on the current page. Best score: {score} / threshold {threshold}; "
                  + $"best position: ({match.X}, {match.Y}); template: {template.Width} x {template.Height}.");
            SetStatus(match.Found
                ? "The current emulator page detected the selected crop."
                : "The current emulator page did not detect the selected crop.");
        }
        catch (OperationCanceledException)
        {
            SetImageMatchTestStatus("Image detection test canceled.");
        }
        catch (Exception exception)
        {
            SetImageMatchTestStatus($"Image detection test failed: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    public async Task TestSelectedReferenceMatchAsync()
    {
        if (_disposed
            || _isBusy
            || _isPipelineRunning
            || _isLoadingImages
            || _isSavingImage
            || !HasRunnerReferenceImage)
        {
            return;
        }

        SetBusy(true);
        ClearImageMatchPreview();
        try
        {
            var referencePath = GetSelectedSystemReferencePath();
            if (referencePath is null)
            {
                SetImageMatchTestStatus(
                    "The selected Uma image has no system reference image.");
                return;
            }

            await _settingsViewModel.ConnectAsync().ConfigureAwait(true);
            var connection = LastVerifiedConnection;
            if (ConnectionState != ConnectionState.Connected || connection is null)
            {
                SetImageMatchTestStatus(
                    "Test blocked: connect the emulator in Settings first.");
                return;
            }

            SetImageMatchTestStatus(
                $"Testing {Path.GetFileName(referencePath)} across the current emulator page...");
            var capture = await _adbRuntime.CaptureBestScreenshotAsync(
                connection.AdbPath,
                connection.Serial).ConfigureAwait(true);
            if (!capture.Succeeded || capture.Screenshot is null)
            {
                SetImageMatchTestStatus(DescribeCaptureFailure(capture));
                return;
            }

            var screen = GrayImageCodec.FromScreenshot(capture.Screenshot);
            if (screen is null)
            {
                SetImageMatchTestStatus(
                    "The emulator returned an unsupported screenshot format.");
                return;
            }

            var referenceTemplate = GrayImageCodec.FromFile(referencePath);
            if (referenceTemplate is null)
            {
                SetImageMatchTestStatus(
                    $"Could not decode the reference image {Path.GetFileName(referencePath)}.");
                return;
            }

            var match = await Task.Run(() =>
                TemplateMatcher.FindScaled(
                    screen,
                    referenceTemplate,
                    roi: null,
                    threshold: SystemReferenceMatchThreshold,
                    referenceWidth: screen.Width,
                    referenceHeight: screen.Height,
                    SystemReferenceScaleCandidates))
                .ConfigureAwait(true);

            _imageMatchTestImage = ScreenshotBitmapCodec.ToBitmapSource(capture.Screenshot);
            _imageMatchTestMatch = match;
            OnPropertyChanged(nameof(UmaImagePreviewImage));
            OnPropertyChanged(nameof(ImageMatchTestMatch));
            var score = match.Score.ToString("0.000", CultureInfo.InvariantCulture);
            var threshold = SystemReferenceMatchThreshold
                .ToString("0.000", CultureInfo.InvariantCulture);
            SetImageMatchTestStatus(match.Found
                ? $"Detected on the current page. Score: {score} / threshold {threshold}; "
                  + $"position: ({match.X}, {match.Y}); "
                  + $"reference: {referenceTemplate.Width} x {referenceTemplate.Height}."
                : $"Not detected on the current page. Best score: {score} / threshold {threshold}; "
                  + $"best position: ({match.X}, {match.Y}); "
                  + $"reference: {referenceTemplate.Width} x {referenceTemplate.Height}.");
            SetStatus(match.Found
                ? "The current emulator page detected the selected system reference."
                : "The current emulator page did not detect the selected system reference.");
        }
        catch (OperationCanceledException)
        {
            SetImageMatchTestStatus("Image detection test canceled.");
        }
        catch (Exception exception)
        {
            SetImageMatchTestStatus($"Image detection test failed: {exception.Message}");
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
        ClearImageMatchPreview();
        SetImageMatchTestStatus("No image detection test has been run.");
        RaiseCommandStates();
        RaisePipelineCommandStates();
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

        var selectedKey = _selectedUmaImage?.Key;
        try
        {
            var records = _umaDatabase.Trainees
                .ToDictionary(record => record.TraineeId);
            var baseRecords = _umaDatabase.BaseCharacters
                .ToDictionary(record => record.BaseCharacterId);
            var traineeDirectory = _umaDatabase.GetTraineeImageDirectory();
            var liveOutfitDirectory = _umaDatabase.GetTraineeLiveOutfitImageDirectory();
            var candidates = await Task.Run(() =>
            {
                var items = new List<DeveloperToolsImageItem>();
                if (Directory.Exists(traineeDirectory))
                {
                    items.AddRange(
                        Directory.EnumerateFiles(traineeDirectory)
                            .Where(IsSupportedImagePath)
                            .Select(path => CreateRaceOutfitImageItem(path, records))
                            .Where(item => item is not null)
                            .Select(item => item!));
                }

                if (Directory.Exists(liveOutfitDirectory))
                {
                    items.AddRange(
                        Directory.EnumerateFiles(liveOutfitDirectory)
                            .Where(IsSupportedImagePath)
                            .Select(path => CreateLiveOutfitImageItem(path, baseRecords))
                            .Where(item => item is not null)
                            .Select(item => item!));
                }

                return items
                    .OrderBy(item => item.BaseCharacterId)
                    .ThenBy(item => item.IsLiveOutfit ? 1 : 0)
                    .ThenBy(item => item.TraineeId)
                    .ToArray();
            }).ConfigureAwait(true);

            _existingImages.Clear();
            foreach (var item in candidates)
            {
                _existingImages.Add(item);
            }

            OnPropertyChanged(nameof(ExistingImageCountDisplay));
            var selected = selectedKey is { } key
                ? _existingImages.FirstOrDefault(item => item.Key == key)
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
                : $"Loaded {candidates.Length} Uma image template(s), including race outfits and live outfits.");
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
        _pipelineRunCancellation?.Cancel();
        _pipelineRunCancellation?.Dispose();
        _pipelineRunCancellation = null;
        _connectionState.StateChanged -= OnConnectionStateChanged;
        _umaDatabase.DatabaseLoaded -= OnUmaDatabaseLoaded;
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
        if (_selectedUmaImage is not { } selectedImage
            || _screenshotImage is null
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

        var referencePath = selectedImage.IsLiveOutfit
            ? _umaDatabase.GetTraineeLiveOutfitReferenceImagePath(selectedImage.BaseCharacterId)
            : selectedImage.TraineeId is { } traineeId
                ? _umaDatabase.GetMaintenanceTraineeReferenceImagePath(traineeId)
                : string.Empty;
        if (string.IsNullOrWhiteSpace(referencePath))
            return;

        var targetDescription = selectedImage.IsLiveOutfit
            ? "live outfit crop"
            : "system reference image";
        var hasExistingReference = File.Exists(referencePath);
        var backupPath = hasExistingReference
            ? CreateBackupPath(referencePath)
            : null;
        var temporaryPath = referencePath + $".{Guid.NewGuid():N}.tmp";
        _isSavingImage = true;
        RaiseCommandStates();
        try
        {
            var cropped = new CroppedBitmap(_screenshotImage, region);
            cropped.Freeze();
            if (hasExistingReference)
            {
                File.Copy(referencePath, backupPath!);
            }

            UmaImageCodec.Save(cropped, temporaryPath);
            File.Move(temporaryPath, referencePath, overwrite: true);

            await RefreshExistingImagesAsync().ConfigureAwait(true);
            SetCropRegion(null);
            OnPropertyChanged(nameof(SelectedReferenceImagePathDisplay));
            SetStatus(hasExistingReference
                ? $"Saved {targetDescription} to {referencePath}. Backup: {backupPath}"
                : $"Saved new {targetDescription} to {referencePath}");
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
        ClearImageMatchPreview();
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
            OnPropertyChanged(nameof(HasRunnerReferenceImage));
            OnPropertyChanged(nameof(SelectedImagePathDisplay));
            OnPropertyChanged(nameof(SelectedReferenceImagePathDisplay));
            RaiseCommandStates();
            return;
        }

        try
        {
            // A live outfit item is cropped from its downloaded transparent source
            // image. Race-outfit items keep the existing screenshot-first
            // workflow so they can still produce a system reference crop.
            var hasCapturedScreenshot = _screenshot is not null
                && !item.IsLiveOutfit;
            if (!hasCapturedScreenshot)
            {
                _screenshotImage = UmaImageCodec.Load(item.Path);
            }
            else if (_screenshot is { } screenshot)
            {
                _screenshotImage = ScreenshotBitmapCodec.ToBitmapSource(screenshot);
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
            OnPropertyChanged(nameof(HasRunnerReferenceImage));
            OnPropertyChanged(nameof(SelectedImagePathDisplay));
            OnPropertyChanged(nameof(SelectedReferenceImagePathDisplay));
            RaiseCommandStates();
        }
        catch (Exception exception)
        {
            _activeImagePath = null;
            _screenshotImage = null;
            _cropRegion = null;
            SetStatus($"Could not load {item.DisplayName}: {exception.Message}");
            NotifyScreenshotPropertiesChanged();
            OnPropertyChanged(nameof(HasSelectedImage));
            OnPropertyChanged(nameof(HasRunnerReferenceImage));
            OnPropertyChanged(nameof(SelectedImagePathDisplay));
            OnPropertyChanged(nameof(SelectedReferenceImagePathDisplay));
            RaiseCommandStates();
        }
    }

    private GrayImage? GetImageMatchTemplateSource()
    {
        if (_selectedUmaImage?.IsLiveOutfit == true
            && !string.IsNullOrWhiteSpace(_activeImagePath))
        {
            return GrayImageCodec.FromFile(_activeImagePath);
        }

        if (_screenshot is { } screenshot)
        {
            return GrayImageCodec.FromScreenshot(screenshot);
        }

        return string.IsNullOrWhiteSpace(_activeImagePath)
            ? null
            : GrayImageCodec.FromFile(_activeImagePath);
    }

    private string? GetSelectedSystemReferencePath()
    {
        if (_selectedUmaImage is not { } selectedImage)
            return null;

        if (selectedImage.IsLiveOutfit)
        {
            var liveFileName = selectedImage.BaseCharacterId.ToString(
                CultureInfo.InvariantCulture) + "_live.webp";
            var liveCandidates = new[]
            {
                _umaDatabase.GetTraineeLiveOutfitReferenceImagePath(selectedImage.BaseCharacterId),
                Path.Combine(AppContext.BaseDirectory, "resource", "uma", "system_reference", liveFileName),
                Path.Combine(Directory.GetCurrentDirectory(), "resource", "uma", "system_reference", liveFileName),
            };
            return liveCandidates.FirstOrDefault(File.Exists);
        }

        if (selectedImage.TraineeId is not { } traineeId)
            return null;

        var fileName = traineeId.ToString(CultureInfo.InvariantCulture) + ".webp";
        var candidates = new[]
        {
            _umaDatabase.GetMaintenanceTraineeReferenceImagePath(traineeId),
            _umaDatabase.GetTraineeReferenceImagePath(traineeId),
            Path.Combine(AppContext.BaseDirectory, "resource", "uma", "system_reference", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "resource", "uma", "system_reference", fileName),
            Path.Combine(AppContext.BaseDirectory, "resource", "uma", "maintenance", "system_reference", fileName),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static DeveloperToolsImageItem? CreateRaceOutfitImageItem(
        string path,
        Dictionary<int, UmaTraineeRecord> records)
    {
        if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out var traineeId))
        {
            return null;
        }

        var hasRecord = records.TryGetValue(traineeId, out var record);
        var baseCharacterId = hasRecord
            ? record!.BaseCharacterId
            : traineeId / 100;
        var name = hasRecord
            ? record!.NameEn
            : traineeId.ToString(CultureInfo.InvariantCulture);
        var thumbnail = UmaImageCodec.Load(path, maxDimension: 96);
        return new DeveloperToolsImageItem(
            traineeId,
            baseCharacterId,
            name,
            path,
            thumbnail,
            DeveloperToolsImageKind.RaceOutfit);
    }

    private static DeveloperToolsImageItem? CreateLiveOutfitImageItem(
        string path,
        Dictionary<int, UmaBaseCharacterRecord> records)
    {
        if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out var baseCharacterId))
        {
            return null;
        }

        var name = records.TryGetValue(baseCharacterId, out var record)
            ? record.NameEn
            : baseCharacterId.ToString(CultureInfo.InvariantCulture);
        var thumbnail = UmaImageCodec.Load(path, maxDimension: 96);
        return new DeveloperToolsImageItem(
            null,
            baseCharacterId,
            name,
            path,
            thumbnail,
            DeveloperToolsImageKind.LiveOutfit);
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

    private void OnUmaDatabaseLoaded(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // UmaDatabaseService loads its JSON with ConfigureAwait(false), so
        // DatabaseLoaded can be raised on a thread-pool thread. Refreshing the
        // WPF-bound image collection and command states must happen on the UI
        // dispatcher or the buttons can remain disabled after the list loads.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            _ = RefreshExistingImagesAsync();
            return;
        }

        _ = dispatcher.InvokeAsync(async () => await RefreshExistingImagesAsync());
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
        OnPropertyChanged(nameof(UmaImagePreviewImage));
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
        RaisePipelineCommandStates();
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

        if (TestImageMatchCommand is RelayCommand testImageMatch)
        {
            testImageMatch.RaiseCanExecuteChanged();
        }

        if (TestReferenceImageCommand is RelayCommand testReferenceImage)
        {
            testReferenceImage.RaiseCanExecuteChanged();
        }
    }

    private void SetImageMatchTestStatus(string status)
    {
        _imageMatchTestStatus = status;
        OnPropertyChanged(nameof(ImageMatchTestStatus));
    }

    private void ClearImageMatchPreview()
    {
        if (_imageMatchTestImage is null && _imageMatchTestMatch is null)
        {
            return;
        }

        _imageMatchTestImage = null;
        _imageMatchTestMatch = null;
        OnPropertyChanged(nameof(UmaImagePreviewImage));
        OnPropertyChanged(nameof(ImageMatchTestMatch));
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

    private sealed record PipelineEditorSnapshot(
        string? SelectedPipelineFile,
        string PipelineName,
        string PipelineDescription,
        string PipelineReferenceWidthText,
        string PipelineReferenceHeightText,
        HachimiPipelineTimingEditorItem Timing,
        IReadOnlyList<HachimiPipelineTaskEditorItem> Tasks,
        string? SelectedTaskName);
}
