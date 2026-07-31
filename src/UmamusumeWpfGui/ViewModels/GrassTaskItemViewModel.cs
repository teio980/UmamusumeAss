using System.ComponentModel;
using System.Runtime.CompilerServices;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.ViewModels;




public sealed class GrassTaskItemViewModel : INotifyPropertyChanged
{
    private string _name;
    private string _description;
    private bool _isEnabled;
    private string _status = "Idle";

    public GrassTaskItemViewModel(IGrassTaskModule module, bool isEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(module.Definition.FallbackName);
        ArgumentException.ThrowIfNullOrWhiteSpace(module.Definition.FallbackDescription);
        Module = module;
        _name = module.Definition.FallbackName;
        _description = module.Definition.FallbackDescription;
        _isEnabled = isEnabled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IGrassTaskModule Module { get; }

    public object Settings => Module.Settings;

    public string Name => _name;

    public string Description => _description;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
                return;
            _isEnabled = value;
            OnPropertyChanged();
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
                return;
            _status = value;
            OnPropertyChanged();
        }
    }

    internal void UpdateText(string name, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        if (_name != name)
        {
            _name = name;
            OnPropertyChanged(nameof(Name));
        }

        if (_description != description)
        {
            _description = description;
            OnPropertyChanged(nameof(Description));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
