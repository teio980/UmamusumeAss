using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UmamusumeWpfGui.ViewModels;

/// <summary>
/// Represents a navigation menu item with a resource-key label
/// and selection tracking. Used by SettingsView left-nav.
/// </summary>
public sealed class MenuItemViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public MenuItemViewModel(string labelKey, int index)
    {
        LabelKey = labelKey;
        Index = index;
    }

    /// <summary>DynamicResource key for the label text.</summary>
    public string LabelKey { get; }

    /// <summary>Index for selection mapping.</summary>
    public int Index { get; }

    /// <summary>Whether this menu item is currently selected.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
