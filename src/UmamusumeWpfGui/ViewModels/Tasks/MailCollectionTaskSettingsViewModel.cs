using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UmamusumeWpfGui.ViewModels.Tasks;

public sealed class MailCollectionTaskSettingsViewModel : INotifyPropertyChanged
{
    public const string DefaultDefinitionPath = "resource/hachimi/mail_collection.json";

    private string _definitionPath = DefaultDefinitionPath;
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
