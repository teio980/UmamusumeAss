using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.ViewModels.Tasks;

/// <summary>
/// Settings that describe the common Career entry flow. This view model does
/// not know which Career mode will run after entry.
/// </summary>
public sealed class CareerEntrySettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IUmaDatabaseService? _umaDatabase;
    private string _manifestPath = CareerTrainingTaskSettingsViewModel.DefaultManifestPath;
    private int? _traineeId = 100601;
    private bool _continueExistingCareer;
    private string _legacySelectionMode = "auto";
    private bool _useLegacyGuest;
    private bool _useCachedLegacy = true;
    private string _traineeSearchText = string.Empty;
    private bool _isTraineeDropDownOpen;
    private bool _databaseLoadedSubscribed;
    private bool _disposed;
    private readonly List<CareerTraineeOption> _allTraineeOptions = [];

    public CareerEntrySettingsViewModel(IUmaDatabaseService? umaDatabase = null)
    {
        _umaDatabase = umaDatabase;
        SubscribeToDatabaseLoadedIfNeeded();

        RefreshTrainees();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<CareerTraineeOption> TraineeOptions { get; } = [];

    public ObservableCollection<CareerTraineeOption> FilteredTraineeOptions { get; } = [];

    public IReadOnlyList<CareerLegacySelectionModeOption> LegacySelectionModes { get; } =
    [
        new("auto", "Auto-Select"),
        new("manual", "Select Legacy 1 and Legacy 2"),
    ];

    public ObservableCollection<CareerSparkOption> AttributeSparkOptions { get; } =
    [
        new("Speed", "Speed"),
        new("Stamina", "Stamina"),
        new("Power", "Power"),
        new("Guts", "Guts"),
        new("Wit", "Wit"),
    ];

    public ObservableCollection<CareerSparkOption> AptitudeSparkOptions { get; } =
    [
        new("Turf", "Turf"),
        new("Dirt", "Dirt"),
        new("Sprint", "Sprint"),
        new("Mile", "Mile"),
        new("Medium", "Medium"),
        new("Long", "Long"),
        new("Front", "Front"),
        new("Pace", "Pace"),
        new("Late", "Late"),
        new("End", "End"),
    ];

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

    public CareerTraineeOption? SelectedTrainee
    {
        get => TraineeOptions.FirstOrDefault(item => item.TraineeId == TraineeId)
            ?? TraineeOptions.FirstOrDefault();
        set => TraineeId = value?.TraineeId;
    }

    public bool ContinueExistingCareer
    {
        get => _continueExistingCareer;
        set => Set(ref _continueExistingCareer, value);
    }

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
        set => Set(ref _isTraineeDropDownOpen, value);
    }

    public string LegacySelectionMode
    {
        get => _legacySelectionMode;
        set
        {
            var normalized = string.Equals(value, "manual", StringComparison.OrdinalIgnoreCase)
                ? "manual"
                : "auto";
            if (!Set(ref _legacySelectionMode, normalized))
                return;
            OnPropertyChanged(nameof(IsManualLegacySelection));
        }
    }

    public bool IsManualLegacySelection =>
        string.Equals(LegacySelectionMode, "manual", StringComparison.OrdinalIgnoreCase);

    public bool UseLegacyGuest
    {
        get => _useLegacyGuest;
        set => Set(ref _useLegacyGuest, value);
    }

    public bool UseCachedLegacy
    {
        get => _useCachedLegacy;
        set => Set(ref _useCachedLegacy, value);
    }

    public IReadOnlyList<string> ParseLegacyAttributeSparks() =>
        AttributeSparkOptions.Where(item => item.IsSelected).Select(item => item.Key).ToArray();

    public IReadOnlyList<string> ParseLegacyAptitudeSparks() =>
        AptitudeSparkOptions.Where(item => item.IsSelected).Select(item => item.Key).ToArray();

    public void SetLegacySparkSelections(
        IEnumerable<string> attributeSparks,
        IEnumerable<string> aptitudeSparks)
    {
        SetSelections(AttributeSparkOptions, attributeSparks);
        SetSelections(AptitudeSparkOptions, aptitudeSparks);
    }

    public bool IsManifestValid()
    {
        if (string.IsNullOrWhiteSpace(ManifestPath))
            return false;
        try
        {
            return File.Exists(Path.GetFullPath(ManifestPath));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

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
                        // The image is optional; keep the trainee selectable.
                    }
                }

                var option = new CareerTraineeOption(trainee.TraineeId, label, thumbnail);
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

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        UnsubscribeFromDatabaseLoaded();
    }

    private void SubscribeToDatabaseLoadedIfNeeded()
    {
        if (_umaDatabase is null || _umaDatabase.IsLoaded)
            return;

        _umaDatabase.DatabaseLoaded += OnDatabaseLoaded;
        _databaseLoadedSubscribed = true;

        // Loading can complete between the IsLoaded check and the event
        // subscription. In that case the initial refresh below observes the
        // loaded data, while the stale one-shot subscription is removed.
        if (_umaDatabase.IsLoaded)
            UnsubscribeFromDatabaseLoaded();
    }

    private void UnsubscribeFromDatabaseLoaded()
    {
        if (!_databaseLoadedSubscribed || _umaDatabase is null)
            return;

        _umaDatabase.DatabaseLoaded -= OnDatabaseLoaded;
        _databaseLoadedSubscribed = false;
    }

    private void OnDatabaseLoaded(object? sender, EventArgs e)
    {
        // Unsubscribe before doing any work so a long refresh cannot retain
        // this task instance through the singleton database service.
        UnsubscribeFromDatabaseLoaded();
        if (!_disposed)
            RefreshTrainees();
    }

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

    private static void SetSelections(
        IEnumerable<CareerSparkOption> options,
        IEnumerable<string> selectedKeys)
    {
        var selected = selectedKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
            option.IsSelected = selected.Contains(option.Key);
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
