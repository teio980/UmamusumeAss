using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UmamusumeWpfGui.ViewModels;

/// <summary>
/// Display-only task item for the MAA-inspired grass page.
/// Execution is intentionally not part of this phase.
/// </summary>
public sealed class GrassTaskItemViewModel : INotifyPropertyChanged
{
    private string _name;
    private string _description;
    private bool _isEnabled;
    private string _status = "Idle";

    public GrassTaskItemViewModel(string name, string description, bool isEnabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        _name = name;
        _description = description;
        _isEnabled = isEnabled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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
