using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Umamusume.CoreBridge;
using IUmaService = Umamusume.CoreBridge.IUmaService;
using TemplateMatchResult = UmamusumeWpfGui.Models.TemplateMatchResult;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.ViewModels;

public sealed class DeveloperToolsViewModel : INotifyPropertyChanged, IDisposable, IGrassTaskLogSink
{
    private const double ImageMatchTestThreshold = 0.86;
    private const double SystemReferenceMatchThreshold = 0.70;
    private static readonly double[] SystemReferenceScaleCandidates =
        [0.32, 0.36, 0.40, 0.44, 0.48, 0.52, 0.56, 0.60, 0.64, 0.68, 0.72, 0.76, 0.80, 0.84, 0.88, 0.92, 0.96, 1.00];

    private static readonly JsonSerializerOptions PipelineJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IUmaVisualDeviceFacade _nativeDevice;
    private readonly IConnectionStateService _connectionState;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly IUmaDatabaseService _umaDatabase;
    private readonly ObservableCollection<DeveloperToolsImageItem> _existingImages = [];
    private readonly ObservableCollection<string> _pipelineFiles = [];
    private readonly ObservableCollection<ScenarioPackageFileEditorItem> _scenarioPackageFiles = [];
    private readonly ObservableCollection<HachimiPipelineTaskEditorItem> _pipelineTasks = [];
    private readonly ObservableCollection<PipelineResourceOption> _pipelineResourceOptions = [];
    private readonly ObservableCollection<LogEntry> _pipelineRunLogs = [];
    private readonly Stack<PipelineEditorSnapshot> _pipelineUndo = [];
    private readonly Stack<PipelineEditorSnapshot> _pipelineRedo = [];
    private GrayImage? _screenshot;
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
    private string? _pipelineDefinitionPath;
    private string? _loadedScenarioManifestPath;
    private ScenarioPackageProjection? _loadedScenarioPackage;
    private ScenarioPackageFileEditorItem? _selectedScenarioPackageFile;
    private string _scenarioPackageStatusText = "Load a scenario manifest to edit its complete package.";
    private string? _selectedPipelineFile;
    private HachimiPipelineTaskEditorItem? _selectedPipelineTask;
    private string _pipelineName = "new-pipeline";
    private string _pipelineDescription = string.Empty;
    private string _pipelineReferenceWidthText = "900";
    private string _pipelineReferenceHeightText = "1600";
    private string _pipelineStatusText = "Select a JSON flow to edit.";
    private string _pipelineGraphText = "Load or create a flow to preview its transitions.";
    private string _pipelineSimulationText = "No simulation has been run.";
    private string _replayFixturePath = string.Empty;
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
    private readonly IActivityRegistry? _activityRegistry;
    private readonly IUmaService? _umaService;
    private IDisposable? _busyActivityLease;

    public DeveloperToolsViewModel(
        IUmaVisualDeviceFacade nativeDevice,
        IConnectionStateService connectionState,
        SettingsViewModel settingsViewModel,
        IUmaDatabaseService umaDatabase,
        IActivityRegistry? activityRegistry = null,
        IUmaService? umaService = null)
    {
        ArgumentNullException.ThrowIfNull(nativeDevice);
        ArgumentNullException.ThrowIfNull(connectionState);
        ArgumentNullException.ThrowIfNull(settingsViewModel);
        ArgumentNullException.ThrowIfNull(umaDatabase);

        _nativeDevice = nativeDevice;
        _connectionState = connectionState;
        _settingsViewModel = settingsViewModel;
        _umaDatabase = umaDatabase;
        _activityRegistry = activityRegistry;
        _umaService = umaService;
        ExistingImages = new ReadOnlyObservableCollection<DeveloperToolsImageItem>(_existingImages);
        PipelineFiles = new ReadOnlyObservableCollection<string>(_pipelineFiles);
        ScenarioPackageFiles = new ReadOnlyObservableCollection<ScenarioPackageFileEditorItem>(
            _scenarioPackageFiles);
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
            _ => _ = SavePipelineAsync(),
            _ => !_disposed && !_isPipelineBusy && HasPipelineDefinition);
        ValidatePipelineCommand = new RelayCommand(
            _ => _ = ValidatePipelineAsync(),
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
            _ => _ = ReplayPipelineAsync(),
            _ => !_disposed && !_isPipelineBusy && HasPipelineDefinition && !string.IsNullOrWhiteSpace(ReplayFixturePath));
        SelectReplayFixtureCommand = new RelayCommand(
            _ => SelectReplayFixture(),
            _ => !_disposed && !_isPipelineBusy);
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
        SaveScenarioPackageFileCommand = new RelayCommand(
            _ => _ = SaveSelectedScenarioPackageFileAsync(),
            _ => !_disposed
                && !_isPipelineBusy
                && SelectedScenarioPackageFile is not null);
        ValidateScenarioPackageCommand = new RelayCommand(
            _ => _ = ValidateScenarioPackageAsync(),
            _ => !_disposed
                && !_isPipelineBusy
                && _loadedScenarioManifestPath is not null
                && _scenarioPackageFiles.Count > 0);

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
    public ICommand SelectReplayFixtureCommand { get; }
    public ICommand RunPipelineCommand { get; }
    public ICommand StopPipelineCommand { get; }
    public ICommand SaveScenarioPackageFileCommand { get; }
    public ICommand ValidateScenarioPackageCommand { get; }

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
        : _screenshot is not null
            ? $"{image.PixelWidth} x {image.PixelHeight} | Native Core"
            : $"{image.PixelWidth} x {image.PixelHeight} | existing image";

    public string StatusText => _statusText;

    public string CaptureDetails => _captureDetails;

    public string ImageMatchTestStatus => _imageMatchTestStatus;

    public ReadOnlyObservableCollection<string> PipelineFiles { get; }

    public ReadOnlyObservableCollection<ScenarioPackageFileEditorItem> ScenarioPackageFiles { get; }

    public ScenarioPackageFileEditorItem? SelectedScenarioPackageFile
    {
        get => _selectedScenarioPackageFile;
        set
        {
            if (ReferenceEquals(_selectedScenarioPackageFile, value))
                return;

            _selectedScenarioPackageFile = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasScenarioPackageFile));
            OnPropertyChanged(nameof(HasRawScenarioPackageFile));
            OnPropertyChanged(nameof(ScenarioPackageJsonText));
            RaisePipelineCommandStates();
        }
    }

    public bool HasScenarioPackageFile => SelectedScenarioPackageFile is not null;

    public bool HasRawScenarioPackageFile => SelectedScenarioPackageFile is { IsRawJsonEditable: true };

    public string ScenarioPackageJsonText
    {
        get => SelectedScenarioPackageFile?.JsonText ?? string.Empty;
        set
        {
            if (SelectedScenarioPackageFile is null
                || !SelectedScenarioPackageFile.IsRawJsonEditable)
            {
                return;
            }

            SelectedScenarioPackageFile.JsonText = value;
            OnPropertyChanged();
        }
    }

    public string ScenarioPackageStatusText => _scenarioPackageStatusText;

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

    public string ReplayFixturePath
    {
        get => _replayFixturePath;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (string.Equals(_replayFixturePath, normalized, StringComparison.Ordinal))
                return;
            _replayFixturePath = normalized;
            OnPropertyChanged();
            if (SimulatePipelineCommand is RelayCommand command)
                command.RaiseCanExecuteChanged();
        }
    }

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
                    ? "Template crop selected. Saving will preserve the existing search ROI."
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
            var ordinaryPipelines = Directory.EnumerateFiles(
                    directory,
                    "*.json",
                    SearchOption.TopDirectoryOnly);
            var hachimiRoot = Directory.GetParent(directory)?.FullName;
            var scenarioManifest = hachimiRoot is null
                ? null
                : Path.Combine(hachimiRoot, "ura", "manifest.json");
            IEnumerable<string> scenarioManifests = scenarioManifest is not null
                && File.Exists(scenarioManifest)
                ? new[] { scenarioManifest! }
                : [];
            foreach (var path in ordinaryPipelines
                         .Concat(scenarioManifests)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
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
            : $"Found {_pipelineFiles.Count} JSON flow or scenario package definition(s). Select one and click Load.");
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
            HachimiPipelineDefinition? definition;
            string definitionPath;
            string loadedDescription;
            string? scenarioManifestPath = null;
            ScenarioPackageProjection? scenarioPackage = null;
            if (IsScenarioManifest(path))
            {
                var inspection = await InspectCareerPackageAsync(path, new Dictionary<string, string>())
                    .ConfigureAwait(true);
                using (inspection.Document)
                {
                    var inspectionRoot = inspection.Document.RootElement;
                    if (!ReadInspectorBoolean(inspectionRoot, "valid"))
                    {
                        SetPipelineStatus("The selected scenario package is invalid: "
                            + FormatInspectorDiagnostics(inspectionRoot, inspection.StandardError));
                        return;
                    }

                    scenarioPackage = ProjectScenarioPackage(path, inspectionRoot);
                definition = ReadScenarioExecutionDefinition(inspectionRoot);
                if (definition is not null)
                    definition.BaseDirectory = Path.Combine(
                        ReadInspectorString(inspectionRoot, "resource_root"),
                        "hachimi",
                        "career");
                    LoadScenarioPackageFiles(path, inspectionRoot, scenarioPackage);
                }
                definitionPath = scenarioPackage!.ExecutionPath;
                loadedDescription =
                    $"Loaded {scenarioPackage.DisplayName} ({scenarioPackage.ScenarioId}) scenario package; "
                    + $"editing its native execution projection ({definition?.Tasks.Count ?? 0} task(s)).";
                scenarioManifestPath = path;
            }
            else
            {
                definition = await HachimiPipelineDefinitionLoader.LoadAsync(path)
                    .ConfigureAwait(true);
                definitionPath = path;
                loadedDescription =
                    $"Loaded {Path.GetFileName(path)}: "
                    + $"{definition?.Tasks.Count ?? 0} task(s).";
                ClearScenarioPackageFiles();
            }
            if (definition is null)
            {
                SetPipelineStatus(
                    Path.GetFileName(path).Equals("start_game.json", StringComparison.OrdinalIgnoreCase)
                        ? "start_game.json uses the startup compatibility schema and is not editable by the ordinary flow editor."
                        : IsScenarioManifest(path)
                            ? "The selected scenario package is invalid or incomplete."
                            : "The selected JSON is invalid, unsupported, or contains an unknown property.");
                return;
            }

            _pipelineDefinition = definition;
            _pipelineDefinitionPath = definitionPath;
            _loadedScenarioManifestPath = scenarioManifestPath;
            _loadedScenarioPackage = scenarioPackage;
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
                loadedDescription + " Drag on the screenshot to fill the selected task's ROI.");
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
        _pipelineDefinitionPath = null;
        _loadedScenarioManifestPath = null;
        ClearScenarioPackageFiles();
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
            var absolutePath = Path.Combine(templateDirectory, $"{taskSlug}.png");
            var writePath = ResourcePathRuntime.ResolveWritePath(absolutePath);
            Directory.CreateDirectory(Path.GetDirectoryName(writePath)!);
            var cropped = new CroppedBitmap(_screenshotImage, region);
            cropped.Freeze();
            ScreenshotBitmapCodec.SavePng(cropped, writePath);
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
                "Template loaded. Drag a crop in the preview, then save to replace the template while preserving the search ROI.");
            SetPipelineStatus(
                "Editing the current template. Saving will preserve the existing search ROI.");
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
        var temporaryPath = ResourcePathRuntime.ResolveWritePath(Path.Combine(
            Path.GetDirectoryName(templatePath) ?? _pipelineDefinition.BaseDirectory,
            $".{Path.GetFileNameWithoutExtension(templatePath)}.{Guid.NewGuid():N}.tmp"
                + Path.GetExtension(templatePath)));
        var writeTemplatePath = ResourcePathRuntime.ResolveWritePath(templatePath);
        string? backupPath = null;
        try
        {
            var cropped = new CroppedBitmap(_screenshotImage, crop);
            cropped.Freeze();

            backupPath = ResourcePathRuntime.ResolveWritePath(CreateBackupPath(templatePath));
            File.Copy(templatePath, backupPath);
            UmaImageCodec.Save(cropped, temporaryPath);
            Directory.CreateDirectory(Path.GetDirectoryName(writeTemplatePath)!);
            File.Move(temporaryPath, writeTemplatePath, overwrite: true);

            var searchRoi = string.IsNullOrWhiteSpace(SelectedPipelineTask.RoiText)
                ? "none"
                : SelectedPipelineTask.RoiText.Trim();
            SetPipelineStatus(
                $"Updated template; preserved search ROI: {searchRoi}. Backup: {backupPath}");

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
            var error = ValidateWithCore(definition);
            SetPipelineStatus(error is null
                ? $"Validation passed: {definition.Tasks.Count} task(s), schema version {definition.SchemaVersion}."
                : "Validation failed: " + error);
        }
        catch (Exception exception)
        {
            SetPipelineStatus($"Validation failed: {exception.Message}");
        }
    }

    private async Task ValidatePipelineAsync()
    {
        if (_pipelineDefinition is null)
            return;
        if (_loadedScenarioManifestPath is null)
        {
            ValidatePipeline();
            return;
        }

        if (_isPipelineBusy)
            return;
        SetPipelineBusy(true);
        try
        {
            var staged = StageScenarioExecutionDefinition(BuildPipelineDefinition());
            var overlays = GetScenarioPackageOverlays(staged);
            using var inspection = await InspectCareerPackageAsync(
                    _loadedScenarioManifestPath,
                    overlays)
                .ConfigureAwait(true);
            var root = inspection.Document.RootElement;
            _scenarioPackageStatusText = ReadInspectorBoolean(root, "valid")
                ? $"Native package validation passed: {ReadInspectorArrayLength(root, "files")} source document(s)."
                : "Native package validation failed: "
                    + FormatInspectorDiagnostics(root, inspection.StandardError);
            OnPropertyChanged(nameof(ScenarioPackageStatusText));
            SetPipelineStatus(_scenarioPackageStatusText);
        }
        catch (Exception exception)
        {
            _scenarioPackageStatusText = $"Native package validation failed: {exception.Message}";
            OnPropertyChanged(nameof(ScenarioPackageStatusText));
            SetPipelineStatus(_scenarioPackageStatusText);
        }
        finally
        {
            SetPipelineBusy(false);
        }
    }

    private string? ValidateWithCore(HachimiPipelineDefinition definition)
    {
        if (_umaService is null)
            return "The native Core service is unavailable.";
        var json = JsonSerializer.Serialize(definition, PipelineJsonOptions);
        return _umaService.ValidatePipelineJson(json);
    }

    private async Task SavePipelineAsync()
    {
        if (_pipelineDefinition is null || _isPipelineBusy)
            return;

        SetPipelineBusy(true);
        try
        {
            var definition = BuildPipelineDefinition();
            var path = _pipelineDefinitionPath ?? SelectedPipelineFile;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = ShowPipelineSaveDialog();
                if (path is null)
                    return;
            }

            path = Path.GetFullPath(path);
            if (_loadedScenarioPackage is not null)
            {
                await SaveScenarioExecutionDefinitionAsync(definition).ConfigureAwait(true);
                return;
            }

            var error = ValidateWithCore(definition);
            if (error is not null)
            {
                SetPipelineStatus("Save blocked: " + error);
                return;
            }

            var writePath = ResourcePathRuntime.ResolveWritePath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(writePath)!);
            File.WriteAllText(writePath, JsonSerializer.Serialize(definition, PipelineJsonOptions));
            _pipelineDefinition = definition;
            _pipelineDefinitionPath = path;
            SelectedPipelineFile = _loadedScenarioManifestPath ?? path;
            RefreshPipelineFiles();
            SetPipelineStatus(
                _loadedScenarioManifestPath is not null
                    ? $"Saved scenario execution definition: {path}"
                    : $"Saved flow: {path}");
        }
        catch (Exception exception)
        {
            SetPipelineStatus($"Could not save the JSON flow: {exception.Message}");
        }
        finally
        {
            SetPipelineBusy(false);
        }
    }

    private async Task SaveScenarioExecutionDefinitionAsync(HachimiPipelineDefinition definition)
    {
        var package = _loadedScenarioPackage
            ?? throw new InvalidOperationException("No native scenario projection is loaded.");
        var stagedFragments = StageScenarioExecutionDefinition(definition);
        using (var inspection = await InspectCareerPackageAsync(
                   package.ManifestPath,
                   GetScenarioPackageOverlays(stagedFragments))
               .ConfigureAwait(true))
        {
            if (!ReadInspectorBoolean(inspection.Document.RootElement, "valid"))
            {
                throw new InvalidDataException("Native package validation blocked the save: "
                    + FormatInspectorDiagnostics(inspection.Document.RootElement, inspection.StandardError));
            }
        }

        foreach (var (sourcePath, jsonText) in stagedFragments)
        {
            var writePath = ResourcePathRuntime.ResolveWritePath(sourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(writePath)!);
            await File.WriteAllTextAsync(
                    writePath,
                    jsonText,
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                .ConfigureAwait(true);
            if (FindScenarioPackageFile(sourcePath) is { } editorFile)
                editorFile.JsonText = jsonText;
        }

        using var updatedInspection = await InspectCareerPackageAsync(
                package.ManifestPath, GetScenarioPackageOverlays())
            .ConfigureAwait(true);
        var updated = ProjectScenarioPackage(package.ManifestPath, updatedInspection.Document.RootElement);
        _loadedScenarioPackage = updated;
        _pipelineDefinition = ReadScenarioExecutionDefinition(updatedInspection.Document.RootElement);
        if (_pipelineDefinition is not null)
            _pipelineDefinition.BaseDirectory = package.ResourceCareerDirectory;
        _pipelineDefinitionPath = updated.ExecutionPath;
        SetPipelineStatus(
            $"Saved scenario execution across {updated.ExecutionFragmentPaths.Count} native source fragment(s). "
            + "Core validated the complete package and retained task provenance.");
        RefreshPipelineFiles();
    }

    private static void MergeTaskProperties(JsonObject original, JsonObject edited)
    {
        foreach (var property in edited)
            original[property.Key] = property.Value?.DeepClone();

        foreach (var propertyName in new[]
        {
            "pipeline", "entry", "template", "templateCollection", "keyCode", "successTask", "countAs", "outcome",
        })
        {
            if (!edited.ContainsKey(propertyName))
                original.Remove(propertyName);
        }
    }

    internal static void PreserveSourceRelativeTemplatePaths(
        JsonObject serializedTask,
        JsonObject? originalTask,
        string sourcePath,
        HachimiPipelineDefinition definition)
    {
        var sourceDirectory = Path.GetDirectoryName(sourcePath)!;
        if (serializedTask["template"] is JsonValue templateValue
            && templateValue.TryGetValue<string>(out var template)
            && !string.IsNullOrWhiteSpace(template))
        {
            var editedPath = ResolveEditorResourcePath(definition.BaseDirectory, template);
            var original = ReadString(originalTask, "template");
            serializedTask["template"] = original is not null
                    && IsSameEditorResource(original, editedPath, sourceDirectory, definition.BaseDirectory)
                ? original
                : Path.GetRelativePath(sourceDirectory, editedPath).Replace(Path.DirectorySeparatorChar, '/');
        }

        if (serializedTask["transitionTemplates"] is not JsonArray transitionArray)
            return;
        var originalTransitions = originalTask?["transitionTemplates"] as JsonArray;
        for (var index = 0; index < transitionArray.Count; index++)
        {
            if (transitionArray[index] is not JsonValue transitionValue
                || !transitionValue.TryGetValue<string>(out var transition)
                || string.IsNullOrWhiteSpace(transition))
            {
                continue;
            }

            var editedPath = ResolveEditorResourcePath(definition.BaseDirectory, transition);
            var original = index < (originalTransitions?.Count ?? 0)
                ? ReadString(originalTransitions![index])
                : null;
            if (original is not null
                && IsSameEditorResource(original, editedPath, sourceDirectory, definition.BaseDirectory))
            {
                transitionArray[index] = original;
            }
            else
            {
                transitionArray[index] = Path.GetRelativePath(sourceDirectory, editedPath)
                    .Replace(Path.DirectorySeparatorChar, '/');
            }
        }
    }

    private static bool IsSameEditorResource(
        string original,
        string editedPath,
        string sourceDirectory,
        string definitionBaseDirectory)
    {
        var originalCandidates = new[]
        {
            ResolveEditorResourcePath(sourceDirectory, original),
            ResolveEditorResourcePath(definitionBaseDirectory, original),
        };
        return originalCandidates.Any(path => path.Equals(editedPath, StringComparison.OrdinalIgnoreCase));
    }

    private Dictionary<string, string> StageScenarioExecutionDefinition(HachimiPipelineDefinition definition)
    {
        var package = _loadedScenarioPackage
            ?? throw new InvalidOperationException("No native scenario projection is loaded.");
        var executionFiles = package.ExecutionFragmentPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (executionFiles.Count == 0)
            throw new InvalidDataException("The native manifest projection has no execution fragments.");

        var definitionsBySource = new Dictionary<string, List<KeyValuePair<string, HachimiPipelineTask>>>(
            StringComparer.OrdinalIgnoreCase);
        var selectedExecutionSource = SelectedScenarioPackageFile is { IsExecution: true } selectedFile
            ? Path.GetFullPath(selectedFile.FullPath)
            : package.ExecutionPath;
        if (!executionFiles.Contains(selectedExecutionSource))
            selectedExecutionSource = package.ExecutionPath;
        foreach (var pair in definition.Tasks)
        {
            var sourcePath = package.TaskSourceFiles.TryGetValue(pair.Key, out var originalSource)
                ? originalSource
                : selectedExecutionSource;
            if (!executionFiles.Contains(sourcePath))
                throw new InvalidDataException(
                    $"Task '{pair.Key}' has provenance outside the native manifest's execution fragments.");
            if (!definitionsBySource.TryGetValue(sourcePath, out var tasks))
                definitionsBySource[sourcePath] = tasks = [];
            tasks.Add(pair);
        }

        var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourcePath in package.ExecutionFragmentPaths)
        {
            var sourceText = FindScenarioPackageFile(sourcePath)?.JsonText ?? File.ReadAllText(sourcePath);
            var root = JsonNode.Parse(sourceText) as JsonObject
                ?? throw new InvalidDataException($"Execution fragment '{sourcePath}' must have an object root.");
            if (sourcePath.Equals(package.ExecutionPath, StringComparison.OrdinalIgnoreCase))
            {
                root["name"] = definition.Name;
                root["schemaVersion"] = definition.SchemaVersion;
                root["description"] = definition.Description;
                root["referenceWidth"] = definition.ReferenceWidth;
                root["referenceHeight"] = definition.ReferenceHeight;
                if (definition.Timing is not null)
                    root["timing"] = JsonSerializer.SerializeToNode(definition.Timing, PipelineJsonOptions);
            }

            var taskMap = root["tasks"] as JsonObject
                ?? throw new InvalidDataException($"Execution fragment '{sourcePath}' has no tasks object.");
            foreach (var source in package.TaskSourceFiles)
            {
                if (source.Value.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)
                    && !definition.Tasks.ContainsKey(source.Key))
                {
                    taskMap.Remove(source.Key);
                }
            }

            if (definitionsBySource.TryGetValue(sourcePath, out var currentTasks))
            {
                foreach (var pair in currentTasks)
                {
                    var originalTask = taskMap[pair.Key] as JsonObject;
                    if (originalTask is null)
                        taskMap[pair.Key] = originalTask = new JsonObject();
                    var serialized = JsonSerializer.SerializeToNode(pair.Value, PipelineJsonOptions) as JsonObject
                        ?? throw new InvalidDataException($"Task '{pair.Key}' could not be serialized.");
                    PreserveSourceRelativeTemplatePaths(serialized, originalTask, sourcePath, definition);
                    MergeTaskProperties(originalTask, serialized);
                }
            }

            staged[sourcePath] = JsonSerializer.Serialize(root, PipelineJsonOptions);
        }
        return staged;
    }

    private Dictionary<string, string> GetScenarioPackageOverlays(
        IReadOnlyDictionary<string, string>? stagedExecutionFragments = null)
        => BuildScenarioPackageOverlays(_scenarioPackageFiles, stagedExecutionFragments);

    internal static Dictionary<string, string> BuildScenarioPackageOverlays(
        IEnumerable<ScenarioPackageFileEditorItem> editorFiles,
        IReadOnlyDictionary<string, string>? stagedExecutionFragments = null)
    {
        var overlays = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in editorFiles)
            overlays[Path.GetFullPath(file.FullPath)] = file.JsonText;
        if (stagedExecutionFragments is not null)
        {
            foreach (var (path, text) in stagedExecutionFragments)
                overlays[Path.GetFullPath(path)] = text;
        }
        return overlays;
    }

    internal static string CreateCareerPackageInspectionRequestJson(
        string resourceRoot,
        IReadOnlyDictionary<string, string> overlays)
    {
        var request = new
        {
            resource_root = Path.GetFullPath(resourceRoot),
            files = overlays.Select(pair => new
            {
                path = Path.GetFullPath(pair.Key),
                raw_json = pair.Value,
            }).ToArray(),
        };
        return JsonSerializer.Serialize(request);
    }

    private static string ResolveEditorResourcePath(string baseDirectory, string relativeOrAbsolutePath) =>
        Path.GetFullPath(Path.IsPathRooted(relativeOrAbsolutePath)
            ? relativeOrAbsolutePath
            : Path.Combine(baseDirectory, relativeOrAbsolutePath));

    private ScenarioPackageFileEditorItem? FindScenarioPackageFile(string fullPath) =>
        _scenarioPackageFiles.FirstOrDefault(file =>
            Path.GetFullPath(file.FullPath).Equals(Path.GetFullPath(fullPath), StringComparison.OrdinalIgnoreCase));

    private void LoadScenarioPackageFiles(
        string manifestPath,
        JsonElement inspectionRoot,
        ScenarioPackageProjection package)
    {
        _scenarioPackageFiles.Clear();
        var rootDirectory = package.RootDirectory;
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (inspectionRoot.TryGetProperty("files", out var sourceFiles)
            && sourceFiles.ValueKind == JsonValueKind.Array)
        {
            foreach (var sourceFile in sourceFiles.EnumerateArray())
            {
                var path = ReadInspectorString(sourceFile, "path");
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                path = Path.GetFullPath(path);
                var rawJson = ReadInspectorString(sourceFile, "raw_json");
                files[path] = rawJson;
            }
        }
        if (!files.ContainsKey(Path.GetFullPath(manifestPath)) && File.Exists(manifestPath))
            files[Path.GetFullPath(manifestPath)] = File.ReadAllText(manifestPath);

        foreach (var (path, rawJson) in files.OrderBy(pair =>
                     pair.Key.Equals(Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                 .ThenBy(pair => Path.GetRelativePath(rootDirectory, pair.Key), StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = Path.GetRelativePath(rootDirectory, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            var text = rawJson;
            try
            {
                using var document = JsonDocument.Parse(text);
                text = JsonSerializer.Serialize(
                    document.RootElement,
                    PipelineJsonOptions);
            }
            catch (JsonException)
            {
                // Keep the original text visible; validation will report the
                // malformed document without destroying the user's content.
            }

            _scenarioPackageFiles.Add(new ScenarioPackageFileEditorItem(
                relativePath,
                path,
                text,
                package.ExecutionFragmentPaths.Contains(path, StringComparer.OrdinalIgnoreCase)));
        }

        SelectedScenarioPackageFile = _scenarioPackageFiles.FirstOrDefault();
        _scenarioPackageStatusText =
            $"Loaded complete scenario package: {_scenarioPackageFiles.Count} JSON file(s).";
        OnPropertyChanged(nameof(ScenarioPackageStatusText));
        OnPropertyChanged(nameof(ScenarioPackageFiles));
        RaisePipelineCommandStates();
    }

    private void ClearScenarioPackageFiles()
    {
        _scenarioPackageFiles.Clear();
        SelectedScenarioPackageFile = null;
        _loadedScenarioPackage = null;
        _scenarioPackageStatusText = "Load a scenario manifest to edit its complete package.";
        OnPropertyChanged(nameof(ScenarioPackageStatusText));
        OnPropertyChanged(nameof(ScenarioPackageFiles));
        RaisePipelineCommandStates();
    }

    private async Task ValidateScenarioPackageAsync()
    {
        if (_loadedScenarioManifestPath is null || _isPipelineBusy)
            return;
        SetPipelineBusy(true);
        try
        {
            // This command validates the source editor. The separate pipeline
            // command stages task-card edits; staging here would overwrite raw edits.
            using var inspection = await InspectCareerPackageAsync(
                    _loadedScenarioManifestPath,
                    GetScenarioPackageOverlays())
                .ConfigureAwait(true);
            var root = inspection.Document.RootElement;
            _scenarioPackageStatusText = ReadInspectorBoolean(root, "valid")
                ? $"Native package validation passed: {ReadInspectorArrayLength(root, "files")} source document(s)."
                : "Native package validation failed: "
                    + FormatInspectorDiagnostics(root, inspection.StandardError);
            OnPropertyChanged(nameof(ScenarioPackageStatusText));
            SetPipelineStatus(_scenarioPackageStatusText);
        }
        catch (Exception exception)
        {
            _scenarioPackageStatusText = $"Native package validation failed: {exception.Message}";
            OnPropertyChanged(nameof(ScenarioPackageStatusText));
            SetPipelineStatus(_scenarioPackageStatusText);
        }
        finally
        {
            SetPipelineBusy(false);
        }
    }

    private async Task SaveSelectedScenarioPackageFileAsync()
    {
        var file = SelectedScenarioPackageFile;
        if (file is null || _isPipelineBusy || _loadedScenarioManifestPath is null)
            return;

        SetPipelineBusy(true);
        try
        {
            using var parsed = JsonDocument.Parse(file.JsonText);
            var formatted = JsonSerializer.Serialize(parsed.RootElement, PipelineJsonOptions);
            var overlays = GetScenarioPackageOverlays();
            overlays[Path.GetFullPath(file.FullPath)] = formatted;
            using var inspection = await InspectCareerPackageAsync(_loadedScenarioManifestPath, overlays)
                .ConfigureAwait(true);
            if (!ReadInspectorBoolean(inspection.Document.RootElement, "valid"))
            {
                _scenarioPackageStatusText = "Save blocked by native package validation: "
                    + FormatInspectorDiagnostics(inspection.Document.RootElement, inspection.StandardError);
                OnPropertyChanged(nameof(ScenarioPackageStatusText));
                return;
            }

            await File.WriteAllTextAsync(
                    ResourcePathRuntime.ResolveWritePath(file.FullPath),
                    formatted,
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                .ConfigureAwait(true);
            file.JsonText = formatted;
            _scenarioPackageStatusText = $"Saved source document {file.RelativePath}.";
            RefreshLoadedScenarioProjection(inspection.Document.RootElement);
            OnPropertyChanged(nameof(ScenarioPackageStatusText));
        }
        catch (JsonException exception)
        {
            _scenarioPackageStatusText =
                $"Save blocked for {file.RelativePath}: invalid JSON. {exception.Message}";
            OnPropertyChanged(nameof(ScenarioPackageStatusText));
        }
        catch (Exception exception)
        {
            _scenarioPackageStatusText = $"Could not save {file.RelativePath}: {exception.Message}";
            OnPropertyChanged(nameof(ScenarioPackageStatusText));
        }
        finally
        {
            SetPipelineBusy(false);
        }
    }

    private void RefreshLoadedScenarioProjection(JsonElement inspectionRoot)
    {
        var package = ProjectScenarioPackage(_loadedScenarioManifestPath!, inspectionRoot);
        var definition = ReadScenarioExecutionDefinition(inspectionRoot);
        if (definition is null)
            return;
        definition.BaseDirectory = package.ResourceCareerDirectory;
        _loadedScenarioPackage = package;
        _pipelineDefinition = definition;
        _pipelineDefinitionPath = package.ExecutionPath;
        ClearPipelineTaskItems();
        foreach (var pair in definition.Tasks)
        {
            var item = HachimiPipelineTaskEditorItem.FromTask(pair.Key, pair.Value);
            AttachPipelineTaskItem(item);
            _pipelineTasks.Add(item);
        }
        SelectedPipelineTask = _pipelineTasks.FirstOrDefault();
        OnPropertyChanged(nameof(HasPipelineDefinition));
    }

    private async Task<CareerPackageInspectionResult> InspectCareerPackageAsync(
        string manifestPath,
        IReadOnlyDictionary<string, string> overlays,
        CancellationToken cancellationToken = default)
    {
        var toolPath = FindPipelineReplayTool();
        if (toolPath is null)
        {
            throw new FileNotFoundException(
                "The native career package inspector is unavailable. Stage uma_pipeline_replay.exe to inspect career packages.");
        }

        var resourceRoot = ResolveCareerResourceRoot(manifestPath);
        var requestPath = Path.Combine(
            Path.GetTempPath(),
            $"career-package-inspect-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(
                    requestPath,
                    CreateCareerPackageInspectionRequestJson(resourceRoot, overlays),
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);

            var toolResult = await RunNativeToolAsync(
                    toolPath,
                    ["--inspect-career-package", Path.GetFullPath(manifestPath), requestPath],
                    TimeSpan.FromSeconds(30),
                    cancellationToken)
                .ConfigureAwait(false);
            var output = toolResult.StandardOutput;
            var error = toolResult.StandardError;
            if (string.IsNullOrWhiteSpace(output))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? $"Native career package inspector exited with code {toolResult.ExitCode} without a result."
                    : error.Trim());
            }

            return new CareerPackageInspectionResult(JsonDocument.Parse(output), error, toolResult.ExitCode);
        }
        finally
        {
            TryDelete(requestPath);
        }
    }

    private string ResolveCareerResourceRoot(string manifestPath)
    {
        if (!string.IsNullOrWhiteSpace(_umaService?.ResourcePath))
            return Path.GetFullPath(_umaService.ResourcePath);

        for (var directory = new DirectoryInfo(Path.GetFullPath(manifestPath)).Parent;
             directory is not null;
             directory = directory.Parent)
        {
            if (directory.Name.Equals("resource", StringComparison.OrdinalIgnoreCase))
                return directory.FullName;
        }

        var runtimeResource = Path.Combine(AppContext.BaseDirectory, "resource");
        if (Directory.Exists(runtimeResource))
            return Path.GetFullPath(runtimeResource);
        throw new DirectoryNotFoundException(
            "The active top-level resource directory could not be determined for native package inspection.");
    }

    private static ScenarioPackageProjection ProjectScenarioPackage(
        string manifestPath,
        JsonElement inspectionRoot)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var rootDirectory = Path.GetDirectoryName(fullManifestPath)
            ?? throw new InvalidDataException("The native career manifest has no parent directory.");
        var resourceRoot = ReadInspectorString(inspectionRoot, "resource_root");
        if (string.IsNullOrWhiteSpace(resourceRoot))
            throw new InvalidDataException("The native inspector did not return resource_root.");
        resourceRoot = Path.GetFullPath(resourceRoot);
        if (!inspectionRoot.TryGetProperty("execution", out var execution)
            || execution.ValueKind != JsonValueKind.Object
            || !execution.TryGetProperty("fragment_paths", out var fragmentPaths)
            || fragmentPaths.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The native inspector did not return execution fragment paths.");
        }

        var fragments = fragmentPaths.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => NormalizeInspectorPath(value.GetString()!, Path.Combine(resourceRoot, "hachimi", "career")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (fragments.Length == 0)
            throw new InvalidDataException("The native manifest declares no execution fragments.");

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (inspectionRoot.TryGetProperty("files", out var nativeFiles)
            && nativeFiles.ValueKind == JsonValueKind.Array)
        {
            foreach (var sourceFile in nativeFiles.EnumerateArray())
            {
                var path = ReadInspectorString(sourceFile, "path");
                if (!string.IsNullOrWhiteSpace(path))
                    files[Path.GetFullPath(path)] = ReadInspectorString(sourceFile, "raw_json");
            }
        }

        var taskSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fragment in fragments)
        {
            if (!files.TryGetValue(fragment, out var rawJson))
                continue;
            try
            {
                using var document = JsonDocument.Parse(rawJson);
                if (!document.RootElement.TryGetProperty("tasks", out var tasks)
                    || tasks.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                foreach (var task in tasks.EnumerateObject())
                    taskSources.TryAdd(task.Name, fragment);
            }
            catch (JsonException)
            {
                // The native inspector reports syntax diagnostics; this projection
                // only records which source fragment owns each task key.
            }
        }

        var manifest = TryGetPropertyCaseInsensitive(inspectionRoot, "manifest", out var manifestElement)
            ? manifestElement
            : default;
        return new ScenarioPackageProjection(
            fullManifestPath,
            rootDirectory,
            resourceRoot,
            ReadString(manifest, "scenarioId") ?? Path.GetFileName(rootDirectory),
            ReadString(manifest, "displayName") ?? Path.GetFileName(rootDirectory),
            fragments[0],
            fragments,
            taskSources);
    }

    private static HachimiPipelineDefinition? ReadScenarioExecutionDefinition(JsonElement inspectionRoot)
    {
        if (!inspectionRoot.TryGetProperty("execution", out var execution)
            || !execution.TryGetProperty("definition", out var definitionElement)
            || definitionElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var definition = JsonSerializer.Deserialize<HachimiPipelineDefinition>(definitionElement.GetRawText());
        if (definition is null)
            return null;
        definition.Tasks = new Dictionary<string, HachimiPipelineTask>(
            definition.Tasks,
            StringComparer.OrdinalIgnoreCase);
        if (execution.TryGetProperty("task_order", out var taskOrder)
            && taskOrder.ValueKind == JsonValueKind.Array)
        {
            var ordered = new Dictionary<string, HachimiPipelineTask>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in taskOrder.EnumerateArray())
            {
                var taskName = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
                if (taskName is not null && definition.Tasks.TryGetValue(taskName, out var task))
                    ordered.TryAdd(taskName, task);
            }
            foreach (var pair in definition.Tasks)
                ordered.TryAdd(pair.Key, pair.Value);
            definition.Tasks = ordered;
        }
        return definition;
    }

    private static string NormalizeInspectorPath(string path, string baseDirectory) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));

    private static bool ReadInspectorBoolean(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static int ReadInspectorArrayLength(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    private static string ReadInspectorString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string FormatInspectorDiagnostics(JsonElement root, string standardError)
    {
        var diagnostics = new List<string>();
        if (root.TryGetProperty("diagnostics", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            diagnostics.AddRange(value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item)));
        }
        if (!string.IsNullOrWhiteSpace(standardError))
            diagnostics.Add(standardError.Trim());
        return diagnostics.Count == 0 ? "No native diagnostics were returned." : string.Join(" | ", diagnostics);
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string property) =>
        TryGetPropertyCaseInsensitive(element, property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static string? ReadString(JsonNode? node, string property) =>
        node is JsonObject obj
        && obj.TryGetPropertyValue(property, out var value)
        && value is JsonValue jsonValue
        && jsonValue.TryGetValue<string>(out var text)
            ? text
            : null;

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
            ? text
            : null;

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

    private static bool IsScenarioManifest(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.GetFileName(path).Equals("manifest.json", StringComparison.OrdinalIgnoreCase);

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

    private string GetPipelineDirectory() =>
        _selectedPipelineResource?.Directory ?? GetRuntimePipelineDirectory();

    private static string GetRuntimePipelineDirectory() =>
        ResourcePathRuntime.Resolve(HachimiResourcePaths.PipelinesRoot);

    private static string? FindSourcePipelineDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                HachimiResourcePaths.PipelinesRoot.Replace('/', Path.DirectorySeparatorChar));
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
        var sourceRoot = new DirectoryInfo(sourcePipelineDirectory);
        while (sourceRoot is not null
            && !File.Exists(Path.Combine(sourceRoot.FullName, "CMakePresets.json")))
        {
            sourceRoot = sourceRoot.Parent;
        }

        if (sourceRoot is null)
            return false;

        var buildOutputRoot = Path.Combine(
            sourceRoot.FullName,
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

    private void SelectReplayFixture()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Core replay fixture|*.json|JSON files|*.json|All files|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = File.Exists(ReplayFixturePath)
                ? Path.GetDirectoryName(ReplayFixturePath)
                : GetPipelineDirectory(),
        };
        if (dialog.ShowDialog() == true)
            ReplayFixturePath = Path.GetFullPath(dialog.FileName);
    }

    private async Task ReplayPipelineAsync()
    {
        if (_disposed || _isPipelineBusy || !HasPipelineDefinition)
            return;
        if (_umaService is null)
        {
            SetPipelineStatus("Core replay is unavailable because the native Core service is not initialized.");
            return;
        }
        if (!File.Exists(ReplayFixturePath))
        {
            SetPipelineStatus("Choose an existing Core replay fixture JSON file first.");
            return;
        }

        string? temporaryPipelinePath = null;
        try
        {
            var definition = BuildPipelineDefinition();
            var validationError = _umaService.ValidatePipelineJson(
                JsonSerializer.Serialize(definition, PipelineJsonOptions));
            if (!string.IsNullOrWhiteSpace(validationError))
                throw new InvalidDataException($"Core parser: {validationError}");

            var entryTask = string.IsNullOrWhiteSpace(PipelineEntryTaskName)
                ? definition.Tasks.Keys.FirstOrDefault() ?? string.Empty
                : PipelineEntryTaskName;
            if (string.IsNullOrWhiteSpace(entryTask)
                || !definition.Tasks.ContainsKey(entryTask))
                throw new InvalidDataException("Choose a valid entry task before replay.");

            var toolPath = FindPipelineReplayTool();
            if (toolPath is null)
                throw new FileNotFoundException(
                    "The native offline replay tool is unavailable. Build or stage uma_pipeline_replay.exe to use replay.");

            var definitionDirectory = Path.GetFullPath(definition.BaseDirectory);
            Directory.CreateDirectory(ResourcePathRuntime.ResolveWritePath(definitionDirectory));
            var fileName = $".{MakeSafeFileName(definition.Name)}.{Guid.NewGuid():N}.replay.json";
            temporaryPipelinePath = ResourcePathRuntime.ResolveWritePath(Path.Combine(definitionDirectory, fileName));
            await File.WriteAllTextAsync(
                    temporaryPipelinePath,
                    JsonSerializer.Serialize(definition, PipelineJsonOptions),
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                .ConfigureAwait(true);

            SetPipelineBusy(true);
            _isPipelineRunning = true;
            OnPropertyChanged(nameof(IsPipelineRunning));
            RaiseCommandStates();
            SetPipelineRunStatus("Running the native Core replay...");

            _pipelineRunCancellation = new CancellationTokenSource();
            var token = _pipelineRunCancellation.Token;
            var toolResult = await RunNativeToolAsync(
                    toolPath,
                    [temporaryPipelinePath, Path.GetFullPath(ReplayFixturePath), entryTask],
                    TimeSpan.FromSeconds(30),
                    token)
                .ConfigureAwait(true);
            if (toolResult.ExitCode != 0 && string.IsNullOrWhiteSpace(toolResult.StandardOutput))
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(toolResult.StandardError)
                    ? $"Replay tool exited with code {toolResult.ExitCode}."
                    : toolResult.StandardError.Trim());

            using var replay = JsonDocument.Parse(toolResult.StandardOutput);
            _pipelineSimulationText = FormatReplayResult(
                replay.RootElement, toolResult.StandardError, toolResult.ExitCode);
            OnPropertyChanged(nameof(PipelineSimulationText));
            var succeeded = replay.RootElement.TryGetProperty("success", out var success)
                && success.ValueKind == JsonValueKind.True;
            SetPipelineRunStatus(succeeded
                ? "Native offline replay completed successfully."
                : $"Native offline replay failed (exit {toolResult.ExitCode}).");
            SetPipelineStatus(_pipelineRunStatusText);
        }
        catch (OperationCanceledException)
        {
            SetPipelineRunStatus("Native offline replay canceled.");
            SetPipelineStatus("Native offline replay canceled.");
        }
        catch (Exception exception)
        {
            _pipelineSimulationText = $"Replay failed: {exception.Message}";
            OnPropertyChanged(nameof(PipelineSimulationText));
            SetPipelineRunStatus($"Replay failed: {exception.Message}");
            SetPipelineStatus(_pipelineRunStatusText);
        }
        finally
        {
            _pipelineRunCancellation?.Dispose();
            _pipelineRunCancellation = null;
            _isPipelineRunning = false;
            OnPropertyChanged(nameof(IsPipelineRunning));
            SetPipelineBusy(false);
            RaiseCommandStates();
            if (temporaryPipelinePath is not null)
                TryDelete(temporaryPipelinePath);
        }
    }

    private static string? FindPipelineReplayTool()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "uma_pipeline_replay.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "uma_pipeline_replay.exe"),
        };
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "CMakePresets.json")))
                continue;
            foreach (var config in new[] { "Debug", "Release" })
                candidates.Add(Path.Combine(directory.FullName, "build", "msvc-debug", "tools", config,
                    "uma_pipeline_replay.exe"));
            break;
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private sealed record NativeToolResult(int ExitCode, string StandardOutput, string StandardError);

    private static async Task<NativeToolResult> RunNativeToolAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        var started = false;
        try
        {
            started = process.Start();
            if (!started)
                throw new InvalidOperationException($"Could not start native tool '{executablePath}'.");

            // Drain both pipes through process exit, including after cancellation kills it.
            var outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    throw;
                throw new TimeoutException($"Native tool exceeded the {timeout.TotalSeconds:0}-second time limit.");
            }

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return new NativeToolResult(process.ExitCode, output, error);
        }
        finally
        {
            if (started)
            {
                TryKillProcessTree(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the HasExited check and Kill.
        }
        catch (Win32Exception)
        {
            // The process can already be in the process of exiting.
        }
    }

    private static string FormatReplayResult(JsonElement root, string standardError, int exitCode)
    {
        var lines = new List<string>
        {
            $"Success: {ReadBoolean(root, "success")}",
            $"Status: {ReadString(root, "status")}",
            $"Final task: {ReadString(root, "finalTask")}",
            $"Steps: {ReadInt(root, "totalSteps")}",
            $"Captures: {ReadInt(root, "captures")}; exhausted: {ReadBoolean(root, "exhausted")}; budget exceeded: {ReadBoolean(root, "budgetExceeded")}",
        };
        if (!string.IsNullOrWhiteSpace(ReadString(root, "error")))
            lines.Add($"Error: {ReadString(root, "error")}");
        if (root.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
        {
            foreach (var action in actions.EnumerateArray())
                lines.Add($"Action: {JsonSerializer.Serialize(action)}");
        }
        if (root.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in events.EnumerateArray())
                lines.Add($"Event: {ReadString(item, "task")} · {ReadString(item, "type")} · {ReadString(item, "message")}");
        }
        if (!string.IsNullOrWhiteSpace(standardError))
            lines.Add($"Tool diagnostics: {standardError.Trim()}");
        if (exitCode != 0)
            lines.Add($"Exit code: {exitCode}");
        return string.Join(Environment.NewLine, lines);

        static string ReadString(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty : string.Empty;
        static bool ReadBoolean(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
        static int ReadInt(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : 0;
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
            var validationError = ValidateWithCore(definition);
            if (validationError is not null)
            {
                SetPipelineRunStatus("Test blocked: " + validationError);
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
            if (_umaService is null)
                throw new InvalidOperationException("The native Core service is unavailable.");
            void OnPipelineEvent(ConnectionEvent connectionEvent)
            {
                switch (connectionEvent)
                {
                    case TaskProgressEvent progress:
                        Add("Core task", $"{progress.TaskName}: {progress.Message}",
                            progress.Succeeded ? LogEntryKind.Info : LogEntryKind.Failure);
                        break;
                    case RuntimeDiagnosticEvent diagnostic:
                        Add(diagnostic.Level.ToString(), diagnostic.Message,
                            diagnostic.Level == RuntimeDiagnosticLevel.Error
                                ? LogEntryKind.Failure : LogEntryKind.Info);
                        break;
                }
            }

            _umaService.ConnectionEventReceived += OnPipelineEvent;
            PipelineRunResult result;
            try
            {
                result = await _umaService.RunPipelineAsync(
                        JsonSerializer.Serialize(definition, PipelineJsonOptions),
                        PipelineEntryTaskName,
                        _pipelineRunCancellation.Token)
                    .ConfigureAwait(true);
            }
            finally
            {
                _umaService.ConnectionEventReceived -= OnPipelineEvent;
            }

            SetPipelineRunStatus(result.Succeeded
                ? $"Test passed: Core completed {result.TotalSteps} step(s) at '{result.FinalTask}'."
                : $"Test failed at '{result.FinalTask}': {result.Message}");
            SetPipelineStatus(_pipelineRunStatusText);
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
            SaveScenarioPackageFileCommand,
            ValidateScenarioPackageCommand,
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
            var capture = await _nativeDevice.CaptureGrayAsync(connection).ConfigureAwait(true);
            if (capture is null)
            {
                SetStatus("The native Core screenshot capture returned no image.");
                return;
            }

            var bitmap = ScreenshotBitmapCodec.ToBitmapSource(capture);
            if (bitmap is null)
            {
                SetStatus("The emulator returned an unsupported screenshot format.");
                return;
            }

            _screenshot = capture;
            _screenshotImage = bitmap;
            _cropRegion = null;
            ClearImageMatchPreview();
            SetImageMatchTestStatus("No image detection test has been run.");
            if (_selectedUmaImage is null)
            {
                _activeImagePath = null;
            }
            _captureDetails =
                $"{bitmap.PixelWidth} × {bitmap.PixelHeight} · Native Core";
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
            var capture = await _nativeDevice.CaptureGrayAsync(connection).ConfigureAwait(true);
            if (capture is null)
            {
                SetImageMatchTestStatus("The native Core screenshot capture returned no image.");
                return;
            }

            var screen = capture;

            var match = await Task.Run(() => TemplateMatcher.Find(
                screen,
                template,
                roi: null,
                threshold: ImageMatchTestThreshold,
                referenceWidth: screen.Width,
                referenceHeight: screen.Height)).ConfigureAwait(true);
            _imageMatchTestImage = ScreenshotBitmapCodec.ToBitmapSource(capture);
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
            var capture = await _nativeDevice.CaptureGrayAsync(connection).ConfigureAwait(true);
            if (capture is null)
            {
                SetImageMatchTestStatus("The native Core screenshot capture returned no image.");
                return;
            }

            var screen = capture;

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

            _imageMatchTestImage = ScreenshotBitmapCodec.ToBitmapSource(capture);
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
        _busyActivityLease?.Dispose();
        _busyActivityLease = null;
        _scenarioPackageFiles.Clear();
        _selectedScenarioPackageFile = null;
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
        var writeReferencePath = ResourcePathRuntime.ResolveWritePath(referencePath);
        var backupPath = hasExistingReference
            ? ResourcePathRuntime.ResolveWritePath(CreateBackupPath(referencePath))
            : null;
        var temporaryPath = ResourcePathRuntime.ResolveWritePath(referencePath) + $".{Guid.NewGuid():N}.tmp";
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
            Directory.CreateDirectory(Path.GetDirectoryName(writeReferencePath)!);
            File.Move(temporaryPath, writeReferencePath, overwrite: true);

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
            return screenshot;

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
                ResourcePathRuntime.Resolve($"resource/uma/system_reference/{liveFileName}"),
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
            ResourcePathRuntime.Resolve($"resource/uma/system_reference/{fileName}"),
            Path.Combine(Directory.GetCurrentDirectory(), "resource", "uma", "system_reference", fileName),
            ResourcePathRuntime.Resolve($"resource/uma/maintenance/system_reference/{fileName}"),
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

        // Native catalog initialization and resource refresh can raise
        // DatabaseLoaded on a thread-pool thread. Refreshing the
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
        if (busy && _busyActivityLease is null)
            _busyActivityLease = _activityRegistry?.Acquire(UmamusumeWpfGui.Models.ActivityKind.DeveloperPipeline);
        else if (!busy)
        {
            _busyActivityLease?.Dispose();
            _busyActivityLease = null;
        }
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

    private sealed record ScenarioPackageProjection(
        string ManifestPath,
        string RootDirectory,
        string ResourceRoot,
        string ScenarioId,
        string DisplayName,
        string ExecutionPath,
        IReadOnlyList<string> ExecutionFragmentPaths,
        IReadOnlyDictionary<string, string> TaskSourceFiles)
    {
        public string ResourceCareerDirectory => Path.Combine(ResourceRoot, "hachimi", "career");
    }

    private sealed class CareerPackageInspectionResult : IDisposable
    {
        public CareerPackageInspectionResult(JsonDocument document, string standardError, int exitCode)
        {
            Document = document;
            StandardError = standardError;
            ExitCode = exitCode;
        }

        public JsonDocument Document { get; }
        public string StandardError { get; }
        public int ExitCode { get; }

        public void Dispose() => Document.Dispose();
    }

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
