using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.ViewModels.Tasks;

public sealed class DailyRaceTaskSettingsViewModel : INotifyPropertyChanged
{
    public const string DefaultDefinitionPath = "resource/hachimi/daily_race.json";
    public const string MoniesMode = "monies";
    public const string SupportPointMode = "supportpoint";
    public const string VeryHardDifficulty = "veryhard";
    public const string HardDifficulty = "hard";
    public const string NormalDifficulty = "normal";
    public const string EasyDifficulty = "easy";
    public const int MinimumRaceCount = 1;
    public const int MaximumRaceCount = 6;

    private string _definitionPath = DefaultDefinitionPath;
    private string _mode = MoniesMode;
    private string _difficulty = VeryHardDifficulty;
    private string _raceCountText = "1";
    private string _status = string.Empty;
    private int? _traineeId;
    private readonly IUmaDatabaseService? _umaDatabase;

    public DailyRaceTaskSettingsViewModel(IUmaDatabaseService? umaDatabase = null)
    {
        _umaDatabase = umaDatabase;
        if (_umaDatabase is not null)
            _umaDatabase.DatabaseLoaded += OnDatabaseLoaded;
        RefreshTrainees();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<DailyRaceModeOption> Modes { get; } =
    [
        new(MoniesMode, "Monies"),
        new(SupportPointMode, "Support Points"),
    ];

    public IReadOnlyList<DailyRaceDifficultyOption> Difficulties { get; } =
    [
        new(VeryHardDifficulty, "Very Hard"),
        new(HardDifficulty, "Hard"),
        new(NormalDifficulty, "Normal"),
        new(EasyDifficulty, "Easy"),
    ];

    public ObservableCollection<DailyRaceTraineeOption> TraineeOptions { get; } = [];

    public int? TraineeId
    {
        get => _traineeId;
        set
        {
            var normalized = value is > 0 ? value : null;
            if (_traineeId == normalized)
                return;
            _traineeId = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedTrainee));
        }
    }

    public DailyRaceTraineeOption? SelectedTrainee
    {
        get => TraineeOptions.FirstOrDefault(item => item.TraineeId == TraineeId)
            ?? TraineeOptions.FirstOrDefault();
        set => TraineeId = value?.TraineeId;
    }

    public void RefreshTrainees()
    {
        var selectedId = TraineeId;
        TraineeOptions.Clear();
        TraineeOptions.Add(new(null, "Automatic (highest rating)"));

        if (_umaDatabase is not null)
        {
            foreach (var trainee in _umaDatabase.Trainees
                         .Where(item => item.Available)
                         .OrderBy(item => item.NameEn, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.TraineeId))
            {
                var label = string.IsNullOrWhiteSpace(trainee.NameEn)
                    ? trainee.TraineeId.ToString(CultureInfo.InvariantCulture)
                    : $"{trainee.NameEn} ({trainee.TraineeId.ToString(CultureInfo.InvariantCulture)})";
                TraineeOptions.Add(new(trainee.TraineeId, label));
            }
        }

        if (selectedId is not null
            && !TraineeOptions.Any(item => item.TraineeId == selectedId))
        {
            TraineeId = null;
        }

        OnPropertyChanged(nameof(SelectedTrainee));
    }

    public string DefinitionPath
    {
        get => _definitionPath;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (_definitionPath == normalized)
                return;
            _definitionPath = normalized;
            OnPropertyChanged();
        }
    }

    public string Mode
    {
        get => _mode;
        set
        {
            var normalized = NormalizeMode(value);
            if (_mode == normalized)
                return;
            _mode = normalized;
            OnPropertyChanged();
        }
    }

    public string Difficulty
    {
        get => _difficulty;
        set
        {
            var normalized = NormalizeDifficulty(value);
            if (_difficulty == normalized)
                return;
            _difficulty = normalized;
            OnPropertyChanged();
        }
    }

    public string RaceCountText
    {
        get => _raceCountText;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (_raceCountText == normalized)
                return;
            _raceCountText = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RaceCount));
        }
    }

    public int RaceCount => int.TryParse(RaceCountText, out var value)
        ? Math.Clamp(value, MinimumRaceCount, MaximumRaceCount)
        : 0;

    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value)
                return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public bool IsModeValid =>
        string.Equals(Mode, MoniesMode, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Mode, SupportPointMode, StringComparison.OrdinalIgnoreCase);

    public bool IsDifficultyValid =>
        string.Equals(Difficulty, VeryHardDifficulty, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Difficulty, HardDifficulty, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Difficulty, NormalDifficulty, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Difficulty, EasyDifficulty, StringComparison.OrdinalIgnoreCase);

    internal void SetStatus(string status) => Status = status;

    public static string NormalizeMode(string? value) =>
        string.Equals(value?.Trim(), SupportPointMode, StringComparison.OrdinalIgnoreCase)
            ? SupportPointMode
            : MoniesMode;

    public static string NormalizeDifficulty(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            HardDifficulty => HardDifficulty,
            NormalDifficulty => NormalDifficulty,
            EasyDifficulty => EasyDifficulty,
            _ => VeryHardDifficulty,
        };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void OnDatabaseLoaded(object? sender, EventArgs e) => RefreshTrainees();
}

public sealed record DailyRaceModeOption(string Value, string Label);

public sealed record DailyRaceDifficultyOption(string Value, string Label);

public sealed record DailyRaceTraineeOption(int? TraineeId, string Label);
