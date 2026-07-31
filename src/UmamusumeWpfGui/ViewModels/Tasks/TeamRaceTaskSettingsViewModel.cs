using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UmamusumeWpfGui.ViewModels.Tasks;

public sealed class TeamRaceTaskSettingsViewModel : INotifyPropertyChanged
{
    public const string DefaultDefinitionPath = "resource/hachimi/team_race.json";

    private string _definitionPath = DefaultDefinitionPath;
    private string _raceCountText = "1";
    private bool _stopWhenTicketsEmpty = true;
    private string _status = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

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
        ? Math.Clamp(value, 1, 999)
        : 0;

    public bool StopWhenTicketsEmpty
    {
        get => _stopWhenTicketsEmpty;
        set
        {
            if (_stopWhenTicketsEmpty == value)
                return;
            _stopWhenTicketsEmpty = value;
            OnPropertyChanged();
        }
    }

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

    internal void SetStatus(string status) => Status = status;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
