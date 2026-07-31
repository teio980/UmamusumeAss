using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UmamusumeWpfGui.ViewModels;





public sealed class MenuItemViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public MenuItemViewModel(string labelKey, int index)
    {
        LabelKey = labelKey;
        Index = index;
    }


    public string LabelKey { get; }


    public int Index { get; }


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
