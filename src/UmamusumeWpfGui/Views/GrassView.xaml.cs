using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.ViewModels;
using UmamusumeWpfGui.ViewModels.Dialogs;
using UmamusumeWpfGui.Views.Dialogs;

namespace UmamusumeWpfGui.Views;





public sealed partial class GrassView : UserControl
{
    private static readonly TimeSpan TaskLongPressDuration = TimeSpan.FromMilliseconds(450);

    private ScrollViewer? _scrollViewer;
    private INotifyCollectionChanged? _subscribedCollection;
    private bool _isAtBottom = true;
    private bool _scrollRequestPending;
    private int _viewGeneration;
    private readonly DispatcherTimer _taskLongPressTimer;
    private GrassTaskItemViewModel? _pendingDragTask;
    private Point _taskDragStartPoint;
    private bool _taskLongPressReady;
    private bool _taskDragInProgress;

    public GrassView()
    {
        InitializeComponent();
        _taskLongPressTimer = new DispatcherTimer(
            DispatcherPriority.Input,
            Dispatcher)
        {
            Interval = TaskLongPressDuration,
        };
        _taskLongPressTimer.Tick += OnTaskLongPressTimerTick;
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e) =>
        AttachTaskSelectionPicker();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewGeneration++;
        _scrollRequestPending = false;
        LocateScrollViewer();
        SubscribeToCollection();
        AttachTaskSelectionPicker();
        RequestScrollToEnd(DispatcherPriority.Loaded);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewGeneration++;
        _scrollRequestPending = false;
        ResetTaskDragState();
        UnsubscribeFromCollection();
        UnsubscribeFromScrollViewer();
    }

    private void OnTaskQueuePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ResetTaskDragState();

        if (DataContext is not GrassViewModel viewModel
            || !viewModel.CanReorderTasks)
        {
            return;
        }

        var task = GetTaskItem(e.OriginalSource as DependencyObject);
        if (task is null)
            return;

        _pendingDragTask = task;
        _taskDragStartPoint = e.GetPosition(TaskQueueListBox);
        _taskLongPressTimer.Start();
    }

    private void OnTaskQueuePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pendingDragTask is null || _taskDragInProgress)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ResetTaskDragState();
            return;
        }

        var point = e.GetPosition(TaskQueueListBox);
        if (!_taskLongPressReady)
        {
            if (HasPassedTaskDragThreshold(point))
                ResetTaskDragState();
            return;
        }

        if (!HasPassedTaskDragThreshold(point))
            return;

        var task = _pendingDragTask;
        _taskLongPressTimer.Stop();
        _taskDragInProgress = true;
        try
        {
            DragDrop.DoDragDrop(
                TaskQueueListBox,
                task,
                DragDropEffects.Move);
        }
        finally
        {
            ResetTaskDragState();
        }

        e.Handled = true;
    }

    private void OnTaskQueuePreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        ResetTaskDragState();

    private void OnTaskLongPressTimerTick(object? sender, EventArgs e)
    {
        _taskLongPressTimer.Stop();
        if (_pendingDragTask is not null
            && Mouse.LeftButton == MouseButtonState.Pressed)
        {
            _taskLongPressReady = true;
        }
    }

    private void OnTaskQueueDragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanAcceptTaskDrop(e) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTaskQueueDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not GrassViewModel viewModel
            || !CanAcceptTaskDrop(e)
            || e.Data.GetData(typeof(GrassTaskItemViewModel)) is not GrassTaskItemViewModel draggedTask)
        {
            e.Handled = true;
            return;
        }

        var sourceIndex = viewModel.Tasks.IndexOf(draggedTask);
        if (sourceIndex < 0)
        {
            e.Handled = true;
            return;
        }

        var targetContainer = ItemsControl.ContainerFromElement(
            TaskQueueListBox,
            e.OriginalSource as DependencyObject) as ListBoxItem;
        var targetTask = targetContainer?.DataContext as GrassTaskItemViewModel;
        var targetIndex = targetTask is null
            ? viewModel.Tasks.Count - 1
            : viewModel.Tasks.IndexOf(targetTask);

        if (targetContainer is not null
            && e.GetPosition(targetContainer).Y > targetContainer.ActualHeight / 2)
        {
            targetIndex++;
        }

        if (sourceIndex < targetIndex)
            targetIndex--;

        viewModel.MoveTask(draggedTask, targetIndex);
        e.Handled = true;
    }

    private bool CanAcceptTaskDrop(DragEventArgs e) =>
        DataContext is GrassViewModel viewModel
        && viewModel.CanReorderTasks
        && e.Data.GetDataPresent(typeof(GrassTaskItemViewModel));

    private bool HasPassedTaskDragThreshold(Point point) =>
        Math.Abs(point.X - _taskDragStartPoint.X) >= SystemParameters.MinimumHorizontalDragDistance
        || Math.Abs(point.Y - _taskDragStartPoint.Y) >= SystemParameters.MinimumVerticalDragDistance;

    private GrassTaskItemViewModel? GetTaskItem(DependencyObject? source)
    {
        if (source is null)
            return null;

        var container = ItemsControl.ContainerFromElement(TaskQueueListBox, source) as ListBoxItem;
        return container?.DataContext as GrassTaskItemViewModel;
    }

    private void ResetTaskDragState()
    {
        _taskLongPressTimer.Stop();
        _pendingDragTask = null;
        _taskLongPressReady = false;
        _taskDragInProgress = false;
    }

    private void SubscribeToCollection()
    {
        UnsubscribeFromCollection();
        if (ScriptLogListBox.ItemsSource is INotifyCollectionChanged collection)
        {
            _subscribedCollection = collection;
            collection.CollectionChanged += OnCollectionChanged;
        }
    }

    private void UnsubscribeFromCollection()
    {
        if (_subscribedCollection is null)
            return;

        _subscribedCollection.CollectionChanged -= OnCollectionChanged;
        _subscribedCollection = null;
    }

    private void LocateScrollViewer()
    {
        UnsubscribeFromScrollViewer();
        _scrollViewer = FindVisualChild<ScrollViewer>(ScriptLogListBox);
        if (_scrollViewer is not null)
            _scrollViewer.ScrollChanged += OnScrollChanged;
    }

    private void UnsubscribeFromScrollViewer()
    {
        if (_scrollViewer is null)
            return;

        _scrollViewer.ScrollChanged -= OnScrollChanged;
        _scrollViewer = null;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_isAtBottom
            || _scrollViewer is null
            || e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }




        RequestScrollToEnd(DispatcherPriority.Background);
    }

    private void RequestScrollToEnd(DispatcherPriority priority)
    {
        if (_scrollRequestPending || _scrollViewer is null)
            return;

        _scrollRequestPending = true;
        var generation = _viewGeneration;
        Dispatcher.BeginInvoke(
            priority,
            new Action(() =>
            {
                _scrollRequestPending = false;
                if (generation != _viewGeneration || !IsLoaded)
                    return;

                _scrollViewer?.ScrollToEnd();
            }));
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scrollViewer is null)
            return;

        _isAtBottom = _scrollViewer.VerticalOffset
            >= _scrollViewer.ScrollableHeight - 1;
    }

    private void AttachTaskSelectionPicker()
    {
        if (DataContext is GrassViewModel viewModel
            && viewModel.RequestTaskSelection is null)
        {
            viewModel.RequestTaskSelection = ShowTaskSelection;
        }
    }

    private IGrassTaskModule? ShowTaskSelection(
        IReadOnlyList<IGrassTaskModule> modules)
    {
        if (modules.Count == 1)
            return modules[0];

        var owner = Window.GetWindow(this);
        if (owner is null)
            return null;

        var viewModel = new TaskSelectionDialogViewModel(modules);
        var dialog = new TaskSelectionDialogView
        {
            Owner = owner,
            DataContext = viewModel,
        };
        return dialog.ShowDialog() == true ? viewModel.SelectedModule : null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
                return found;

            var result = FindVisualChild<T>(child);
            if (result is not null)
                return result;
        }

        return null;
    }
}
