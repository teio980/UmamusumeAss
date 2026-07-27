using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using UmamusumeWpfGui.Models;

namespace UmamusumeWpfGui.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the multi-instance selection dialog.
/// Displays detected emulator candidates and lets the user pick one.
/// Cancel preserves the existing draft; Confirm applies the selection.
/// </summary>
public sealed class SelectionDialogViewModel : INotifyPropertyChanged
{
    private readonly ObservableCollection<SelectableEmulatorItem> _items;

    /// <summary>
    /// Creates the selection dialog with the given candidates.
    /// When only one candidate is provided, it is pre-selected.
    /// </summary>
    public SelectionDialogViewModel(IReadOnlyList<DetectedEmulatorInfo> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        _items = new ObservableCollection<SelectableEmulatorItem>(
            candidates.Select(c => new SelectableEmulatorItem(c)));

        // Pre-select when only one candidate
        if (_items.Count == 1)
        {
            _items[0].IsSelected = true;
        }

        // Subscribe to selection changes
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

    /// <summary>
    /// Resource key for the dialog title, resolved via DynamicResource.
    /// </summary>
    public string TitleResourceKey { get; }

    /// <summary>
    /// The list of selectable emulator candidates.
    /// </summary>
    public ObservableCollection<SelectableEmulatorItem> Items => _items;

    /// <summary>
    /// Returns the currently selected candidate, or null if none selected.
    /// </summary>
    public DetectedEmulatorInfo? SelectedCandidate =>
        _items.FirstOrDefault(i => i.IsSelected)?.Candidate;

    /// <summary>
    /// Raised when the dialog should close. Parameter indicates
    /// true for Confirm (selection applied) or false for Cancel.
    /// </summary>
    public event Action<bool?>? RequestClose;

    /// <summary>
    /// Confirms the current selection and closes the dialog.
    /// </summary>
    public ICommand ConfirmCommand { get; }

    /// <summary>
    /// Cancels the selection and closes the dialog.
    /// </summary>
    public ICommand CancelCommand { get; }

    // ────────────────────────────────────────────────────────────────
    // Event handlers
    // ────────────────────────────────────────────────────────────────

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SelectableEmulatorItem.IsSelected))
            return;

        if (sender is not SelectableEmulatorItem changedItem || !changedItem.IsSelected)
            return;

        // Deselect all other items (radio-button semantics)
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

    // ────────────────────────────────────────────────────────────────
    // INotifyPropertyChanged
    // ────────────────────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // ────────────────────────────────────────────────────────────────
    // Wrapper item with selection tracking
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Wraps a <see cref="DetectedEmulatorInfo"/> with an observable
    /// <see cref="IsSelected"/> property for use in selection UI.
    /// </summary>
    public sealed class SelectableEmulatorItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        internal SelectableEmulatorItem(DetectedEmulatorInfo candidate)
        {
            Candidate = candidate;
        }

        /// <summary>Underlying emulator candidate data.</summary>
        public DetectedEmulatorInfo Candidate { get; }

        /// <summary>Display name of the emulator.</summary>
        public string EmulatorName => Candidate.EmulatorName;

        /// <summary>ADB executable path, or null if unavailable.</summary>
        public string? AdbPath => Candidate.AdbPath;

        /// <summary>Whether this item is currently selected.</summary>
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

    // ────────────────────────────────────────────────────────────────
    // Minimal RelayCommand
    // ────────────────────────────────────────────────────────────────

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
