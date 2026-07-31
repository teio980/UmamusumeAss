using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.ViewModels.Dialogs;






public sealed class SelectionDialogViewModel : INotifyPropertyChanged
{
    private readonly ObservableCollection<SelectableEmulatorItem> _items;





    public SelectionDialogViewModel(IReadOnlyList<DetectedEmulatorInfo> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        _items = new ObservableCollection<SelectableEmulatorItem>(
            candidates.Select(c => new SelectableEmulatorItem(c)));


        if (_items.Count == 1)
        {
            _items[0].IsSelected = true;
        }


        foreach (var item in _items)
        {
            item.PropertyChanged += OnItemPropertyChanged;
        }

        ConfirmCommand = new RelayCommand(
            _ => RequestClose?.Invoke(true),
            _ => SelectedCandidate is not null);

        CancelCommand = new RelayCommand(
            _ => RequestClose?.Invoke(false),
            _ => true);

        TitleResourceKey = "SelectionDialogTitle";
    }




    public string TitleResourceKey { get; }




    public ObservableCollection<SelectableEmulatorItem> Items => _items;




    public DetectedEmulatorInfo? SelectedCandidate =>
        _items.FirstOrDefault(i => i.IsSelected)?.Candidate;





    public event Action<bool?>? RequestClose;




    public ICommand ConfirmCommand { get; }




    public ICommand CancelCommand { get; }





    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SelectableEmulatorItem.IsSelected))
            return;

        if (sender is not SelectableEmulatorItem changedItem || !changedItem.IsSelected)
            return;


        foreach (var item in _items)
        {
            if (item != changedItem && item.IsSelected)
            {
                item.IsSelected = false;
            }
        }

        OnPropertyChanged(nameof(SelectedCandidate));
        if (ConfirmCommand is RelayCommand rc)
            rc.RaiseCanExecuteChanged();
    }





    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }









    public sealed class SelectableEmulatorItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        internal SelectableEmulatorItem(DetectedEmulatorInfo candidate)
        {
            Candidate = candidate;
        }


        public DetectedEmulatorInfo Candidate { get; }


        public string EmulatorName => Candidate.EmulatorName;


        public string? AdbPath => Candidate.AdbPath;


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





    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool> _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool> canExecute)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute(parameter);

        public void Execute(object? parameter) => _execute(parameter);

        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
