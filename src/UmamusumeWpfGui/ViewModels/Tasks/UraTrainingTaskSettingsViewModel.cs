using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.ViewModels.Tasks;

public sealed class UraTrainingTaskSettingsViewModel : INotifyPropertyChanged
{
    public const string DefaultManifestPath = "resource/hachimi/ura/manifest.json";
    public const string DefaultStrategyId = "default-speed-medium";

    private readonly IUmaDatabaseService? _umaDatabase;
    private string _manifestPath = DefaultManifestPath;
    private int? _traineeId = 100601;
    private string _supportCardIdsText = string.Empty;
    private string _strategyId = DefaultStrategyId;
    private bool _pauseOnUnknownOutcome = true;
    private bool _allowOptionalRaces;
    private string _status = string.Empty;
    private string _traineeSearchText = string.Empty;
    private bool _isTraineeDropDownOpen;
    private readonly List<UraTraineeOption> _allTraineeOptions = [];

    public UraTrainingTaskSettingsViewModel(IUmaDatabaseService? umaDatabase = null)
    {
        _umaDatabase = umaDatabase;
        if (_umaDatabase is not null)
            _umaDatabase.DatabaseLoaded += OnDatabaseLoaded;
        RefreshTrainees();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<UraTraineeOption> TraineeOptions { get; } = [];

    public ObservableCollection<UraTraineeOption> FilteredTraineeOptions { get; } = [];

    public string TraineeSearchText
    {
        get => _traineeSearchText;
        set
        {
            var normalized = value ?? string.Empty;
            if (_traineeSearchText == normalized)
                return;
            _traineeSearchText = normalized;
            OnPropertyChanged();
            ApplyTraineeSearch();
        }
    }

    public bool IsTraineeDropDownOpen
    {
        get => _isTraineeDropDownOpen;
        set
        {
            if (_isTraineeDropDownOpen == value)
                return;
            _isTraineeDropDownOpen = value;
            OnPropertyChanged();
        }
    }

    public string ManifestPath
    {
        get => _manifestPath;
        set => Set(ref _manifestPath, value?.Trim() ?? string.Empty);
    }

    public int? TraineeId
    {
        get => _traineeId;
        set
        {
            var normalized = value is > 0 ? value : null;
            if (Set(ref _traineeId, normalized))
                OnPropertyChanged(nameof(SelectedTrainee));
        }
    }

    public UraTraineeOption? SelectedTrainee
    {
        get => TraineeOptions.FirstOrDefault(item => item.TraineeId == TraineeId)
            ?? TraineeOptions.FirstOrDefault();
        set => TraineeId = value?.TraineeId;
    }

    public string SupportCardIdsText
    {
        get => _supportCardIdsText;
        set => Set(ref _supportCardIdsText, value?.Trim() ?? string.Empty);
    }

    public string StrategyId
    {
        get => _strategyId;
        set => Set(ref _strategyId, value?.Trim() ?? string.Empty);
    }

    public bool PauseOnUnknownOutcome
    {
        get => _pauseOnUnknownOutcome;
        set => Set(ref _pauseOnUnknownOutcome, value);
    }

    public bool AllowOptionalRaces
    {
        get => _allowOptionalRaces;
        set => Set(ref _allowOptionalRaces, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public IReadOnlyList<int> ParseSupportCardIds()
    {
        var ids = new List<int>();
        foreach (var token in SupportCardIdsText.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token, out var id) || id <= 0)
                throw new InvalidOperationException($"Invalid support card ID '{token}'.");
            if (!ids.Contains(id))
                ids.Add(id);
        }

        return ids;
    }

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(ManifestPath)
        && TraineeId is > 0
        && !string.IsNullOrWhiteSpace(StrategyId);

    internal void SetStatus(string status) => Status = status;

    public void RefreshTrainees()
    {
        var selectedId = TraineeId;
        TraineeOptions.Clear();
        _allTraineeOptions.Clear();

        if (_umaDatabase is not null)
        {
            foreach (var trainee in _umaDatabase.Trainees
                         .Where(item => item.Available && HasRunnerTemplate(item))
                         .OrderBy(item => item.NameEn, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.TraineeId))
            {
                var label = string.IsNullOrWhiteSpace(trainee.NameEn)
                    ? "Unknown trainee"
                    : trainee.NameEn;
                BitmapSource? thumbnail = null;
                var imagePath = _umaDatabase.GetTraineeImagePath(trainee.TraineeId);
                if (File.Exists(imagePath))
                {
                    try
                    {
                        thumbnail = UmaImageCodec.Load(imagePath, maxDimension: 72);
                    }
                    catch (Exception)
                    {
                        // Keep the ID selectable if the optional thumbnail is invalid.
                    }
                }

                var option = new UraTraineeOption(trainee.TraineeId, label, thumbnail);
                _allTraineeOptions.Add(option);
                TraineeOptions.Add(option);
            }
        }

        if (selectedId is not null
            && !_allTraineeOptions.Any(item => item.TraineeId == selectedId))
        {
            TraineeId = null;
        }

        OnPropertyChanged(nameof(TraineeOptions));
        ApplyTraineeSearch();
    }

    private bool HasRunnerTemplate(UmaTraineeRecord trainee) =>
        File.Exists(_umaDatabase!.GetMaintenanceTraineeReferenceImagePath(trainee.TraineeId))
        || File.Exists(_umaDatabase.GetTraineeReferenceImagePath(trainee.TraineeId))
        || File.Exists(_umaDatabase.GetTraineeImagePath(trainee.TraineeId))
        || File.Exists(_umaDatabase.GetTraineeLiveOutfitReferenceImagePath(trainee.BaseCharacterId))
        || File.Exists(_umaDatabase.GetTraineeLiveOutfitImagePath(trainee.BaseCharacterId));

    private void OnDatabaseLoaded(object? sender, EventArgs e) => RefreshTrainees();

    private void ApplyTraineeSearch()
    {
        var query = TraineeSearchText.Trim();
        FilteredTraineeOptions.Clear();
        foreach (var option in _allTraineeOptions)
        {
            if (string.IsNullOrWhiteSpace(query)
                || option.TraineeId == TraineeId
                || option.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || option.TraineeId.ToString(CultureInfo.InvariantCulture)
                    .Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredTraineeOptions.Add(option);
            }
        }

        OnPropertyChanged(nameof(SelectedTrainee));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record UraTraineeOption(
    int TraineeId,
    string Label,
    BitmapSource? Thumbnail = null);
