using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.ViewModels.Dialogs;

public sealed class TaskSelectionDialogViewModel : INotifyPropertyChanged
{
    private readonly ObservableCollection<TaskOption> _items;
    private TaskOption? _selectedTask;

    public TaskSelectionDialogViewModel(IReadOnlyList<IGrassTaskModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _items = new ObservableCollection<TaskOption>(
            modules.Select(module => new TaskOption(module)));
        SelectedTask = _items.FirstOrDefault();

        ConfirmCommand = new RelayCommand(
            _ => RequestClose?.Invoke(true),
            _ => SelectedTask is not null);
        CancelCommand = new RelayCommand(
            _ => RequestClose?.Invoke(false),
            _ => true);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TaskOption> Items => _items;

    public TaskOption? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (ReferenceEquals(_selectedTask, value))
                return;
            _selectedTask = value;
            OnPropertyChanged();
            if (ConfirmCommand is RelayCommand command)
                command.RaiseCanExecuteChanged();
        }
    }

    public IGrassTaskModule? SelectedModule => SelectedTask?.Module;

    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }

    public event Action<bool?>? RequestClose;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public sealed class TaskOption
    {
        internal TaskOption(IGrassTaskModule module)
        {
            Module = module;
        }

        internal IGrassTaskModule Module { get; }

        public string Name => Module.Definition.FallbackName;
        public string Description => Module.Definition.FallbackDescription;
    }

    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?> _canExecute;

        public RelayCommand(Action<object?> execute, Predicate<object?> canExecute)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute(parameter);
        public void Execute(object? parameter) => _execute(parameter);
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
