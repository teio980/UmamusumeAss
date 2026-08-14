using System.Collections.Specialized;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private const double TaskRowDragStartThreshold = 4d;
    private const double TaskRowAutoScrollEdge = 36d;
    private const double TaskRowAutoScrollStep = 18d;
    private const double TaskDropIndicatorHeight = 3d;

    private ScrollViewer? _scrollViewer;
    private ScrollViewer? _taskListScrollViewer;
    private INotifyCollectionChanged? _subscribedCollection;
    private bool _isAtBottom = true;
    private bool _scrollRequestPending;
    private int _viewGeneration;
    private GrassTaskItemViewModel? _pendingDragTask;
    private ListBoxItem? _taskDragSourceContainer;
    private Point _taskDragStartPoint;
    private bool _taskDragInProgress;
    private bool _taskDragInitializing;
    private int _taskDragSourceIndex = -1;
    private int _taskInsertionIndex = -1;

    public GrassView()
    {
        InitializeComponent();
        TaskQueueListBox.AddHandler(
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnTaskListPreviewMouseLeftButtonDown),
            handledEventsToo: true);
        TaskQueueListBox.AddHandler(
            UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(OnTaskListPreviewMouseMove),
            handledEventsToo: true);
        TaskQueueListBox.AddHandler(
            UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnTaskListPreviewMouseLeftButtonUp),
            handledEventsToo: true);
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
        LocateTaskListScrollViewer();
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
        _taskListScrollViewer = null;
    }

    private void OnTaskListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ResetTaskDragState();

        if (DataContext is not GrassViewModel viewModel
            || !viewModel.CanReorderTasks
            || ItemsControl.ContainerFromElement(
                TaskQueueListBox,
                e.OriginalSource as DependencyObject) is not ListBoxItem container
            || container.DataContext is not GrassTaskItemViewModel task
            || IsInteractiveTaskRowDragSource(e.OriginalSource as DependencyObject, container))
        {
            return;
        }

        _pendingDragTask = task;
        _taskDragSourceContainer = container;
        _taskDragStartPoint = e.GetPosition(container);
    }

    private void OnTaskListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pendingDragTask is null
            || _taskDragSourceContainer is null
            || _taskDragInitializing)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ResetTaskDragState();
            return;
        }

        var sourcePoint = e.GetPosition(_taskDragSourceContainer);
        if (!_taskDragInProgress
            && !HasPassedTaskDragThreshold(sourcePoint))
        {
            return;
        }

        if (!_taskDragInProgress && !BeginTaskRowDrag())
            return;

        UpdateTaskRowDrag(e);
        e.Handled = true;
    }

    private void OnTaskListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_taskDragInProgress
            || DataContext is not GrassViewModel viewModel
            || _pendingDragTask is not GrassTaskItemViewModel sourceTask)
        {
            ResetTaskDragState();
            return;
        }

        var sourceIndex = _taskDragSourceIndex;
        var insertionIndex = _taskInsertionIndex;
        var shouldMove = viewModel.CanReorderTasks
            && IsPointerInsideTaskList(e)
            && sourceIndex >= 0
            && sourceIndex < viewModel.Tasks.Count
            && insertionIndex >= 0;

        ResetTaskDragState();
        e.Handled = true;

        if (!shouldMove)
            return;

        var targetIndex = insertionIndex > sourceIndex
            ? insertionIndex - 1
            : insertionIndex;
        targetIndex = Math.Clamp(targetIndex, 0, viewModel.Tasks.Count - 1);
        if (targetIndex == sourceIndex)
            return;

        viewModel.SelectedTask = sourceTask;
        viewModel.MoveTask(sourceTask, targetIndex);
    }

    private bool BeginTaskRowDrag()
    {
        if (DataContext is not GrassViewModel viewModel
            || !viewModel.CanReorderTasks
            || _pendingDragTask is null
            || _taskDragSourceContainer is null)
        {
            return false;
        }

        var sourceIndex = viewModel.Tasks.IndexOf(_pendingDragTask);
        if (sourceIndex < 0)
        {
            ResetTaskDragState();
            return false;
        }

        _taskDragInProgress = true;
        _taskDragSourceIndex = sourceIndex;
        _taskInsertionIndex = sourceIndex;
        _taskDragSourceContainer.Opacity = 0;
        _taskDragInitializing = true;
        try
        {
            Mouse.Capture(_taskDragSourceContainer, CaptureMode.SubTree);
        }
        finally
        {
            _taskDragInitializing = false;
        }
        _taskDragSourceContainer.PreviewMouseLeftButtonUp += OnTaskListPreviewMouseLeftButtonUp;
        ShowTaskDragPreview();
        return true;
    }

    private void UpdateTaskRowDrag(MouseEventArgs e)
    {
        if (!_taskDragInProgress)
            return;

        AutoScrollTaskList(e);
        UpdateTaskDragPreview(e);

        if (!TryResolveTaskInsertion(
                e,
                out var insertionIndex,
                out var indicatorLeft,
                out var indicatorTop,
                out var indicatorWidth))
        {
            _taskInsertionIndex = -1;
            HideTaskDropIndicator();
            return;
        }

        _taskInsertionIndex = insertionIndex;
        UpdateTaskDropIndicator(indicatorLeft, indicatorTop, indicatorWidth);
    }

    private bool TryResolveTaskInsertion(
        MouseEventArgs e,
        out int insertionIndex,
        out double indicatorLeft,
        out double indicatorTop,
        out double indicatorWidth)
    {
        insertionIndex = -1;
        indicatorLeft = 0;
        indicatorTop = 0;
        indicatorWidth = 0;

        var rows = GetVisibleTaskRows()
            .Select(row => new
            {
                Row = row,
                Index = row.DataContext is GrassTaskItemViewModel task
                    && DataContext is GrassViewModel viewModel
                    ? viewModel.Tasks.IndexOf(task)
                    : -1,
            })
            .Where(item => item.Index >= 0)
            .OrderBy(item => item.Index)
            .ToArray();
        if (rows.Length == 0)
        {
            return false;
        }

        var pointerY = e.GetPosition(TaskQueueListBox).Y;
        var selectedRow = rows[^1];
        insertionIndex = selectedRow.Index + 1;

        foreach (var row in rows)
        {
            var rowPoint = row.Row.TranslatePoint(new Point(), TaskQueueListBox);
            var centerY = rowPoint.Y + row.Row.ActualHeight / 2;
            if (pointerY < centerY)
            {
                selectedRow = row;
                insertionIndex = row.Index;
                break;
            }
        }

        var indicatorPoint = selectedRow.Row.TranslatePoint(new Point(), TaskQueueOverlay);
        indicatorLeft = indicatorPoint.X;
        indicatorTop = insertionIndex <= selectedRow.Index
            ? indicatorPoint.Y
            : indicatorPoint.Y + selectedRow.Row.ActualHeight;
        indicatorWidth = selectedRow.Row.ActualWidth;
        insertionIndex = Math.Clamp(
            insertionIndex,
            0,
            DataContext is GrassViewModel vm ? vm.Tasks.Count : 0);
        return true;
    }

    private IEnumerable<ListBoxItem> GetVisibleTaskRows()
    {
        for (var index = 0; index < TaskQueueListBox.Items.Count; index++)
        {
            if (TaskQueueListBox.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem row
                && row.IsVisible
                && row.ActualWidth > 0
                && row.ActualHeight > 0)
            {
                yield return row;
            }
        }
    }

    private void AutoScrollTaskList(MouseEventArgs e)
    {
        if (_taskListScrollViewer is null
            || _taskListScrollViewer.ViewportHeight <= 0)
        {
            return;
        }

        var position = e.GetPosition(_taskListScrollViewer);
        var delta = 0d;
        if (position.Y < TaskRowAutoScrollEdge)
            delta = -TaskRowAutoScrollStep;
        else if (position.Y > _taskListScrollViewer.ActualHeight - TaskRowAutoScrollEdge)
            delta = TaskRowAutoScrollStep;

        var nextOffset = Math.Clamp(
            _taskListScrollViewer.VerticalOffset + delta,
            0,
            _taskListScrollViewer.ScrollableHeight);
        if (Math.Abs(nextOffset - _taskListScrollViewer.VerticalOffset) > 0.01)
            _taskListScrollViewer.ScrollToVerticalOffset(nextOffset);
    }

    private void ShowTaskDragPreview()
    {
        if (_taskDragSourceContainer is null || _pendingDragTask is null)
            return;

        TaskDragPreview.DataContext = _pendingDragTask;
        TaskDragPreview.Width = _taskDragSourceContainer.ActualWidth;
        TaskDragPreview.Height = _taskDragSourceContainer.ActualHeight;
        TaskDragPreview.Visibility = Visibility.Visible;
    }

    private void UpdateTaskDragPreview(MouseEventArgs e)
    {
        var position = e.GetPosition(TaskQueueOverlay);
        Canvas.SetLeft(TaskDragPreview, position.X - _taskDragStartPoint.X);
        Canvas.SetTop(TaskDragPreview, position.Y - _taskDragStartPoint.Y);
    }

    private void UpdateTaskDropIndicator(double left, double top, double width)
    {
        TaskDropIndicator.Width = Math.Max(0, width);
        TaskDropIndicator.Height = TaskDropIndicatorHeight;
        Canvas.SetLeft(TaskDropIndicator, left);
        Canvas.SetTop(TaskDropIndicator, top - TaskDropIndicatorHeight / 2);
        TaskDropIndicator.Visibility = Visibility.Visible;
    }

    private void HideTaskDropIndicator() =>
        TaskDropIndicator.Visibility = Visibility.Collapsed;

    private void HideTaskDragPreview()
    {
        TaskDragPreview.Visibility = Visibility.Collapsed;
        TaskDragPreview.DataContext = null;
    }

    private bool IsPointerInsideTaskList(MouseEventArgs e)
    {
        var point = e.GetPosition(TaskQueueListBox);
        return point.X >= 0
            && point.Y >= 0
            && point.X <= TaskQueueListBox.ActualWidth
            && point.Y <= TaskQueueListBox.ActualHeight;
    }

    private bool HasPassedTaskDragThreshold(Point point) =>
        Math.Abs(point.X - _taskDragStartPoint.X) >= TaskRowDragStartThreshold
        || Math.Abs(point.Y - _taskDragStartPoint.Y) >= TaskRowDragStartThreshold;

    private static bool IsInteractiveTaskRowDragSource(
        DependencyObject? source,
        ListBoxItem rowControl)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, rowControl))
                return false;

            if (current is ButtonBase
                || current is TextBoxBase
                || current is Slider
                || current is ComboBox)
            {
                return true;
            }

            current = current switch
            {
                FrameworkContentElement contentElement => contentElement.Parent,
                _ => VisualTreeHelper.GetParent(current),
            };
        }

        return false;
    }

    private void ResetTaskDragState()
    {
        if (_taskDragSourceContainer is not null)
            _taskDragSourceContainer.PreviewMouseLeftButtonUp -= OnTaskListPreviewMouseLeftButtonUp;

        if (_taskDragSourceContainer is not null)
            _taskDragSourceContainer.Opacity = 1;

        HideTaskDropIndicator();
        HideTaskDragPreview();
        if (_taskDragInProgress && ReferenceEquals(Mouse.Captured, _taskDragSourceContainer))
            Mouse.Capture(null);

        _pendingDragTask = null;
        _taskDragSourceContainer = null;
        _taskDragInProgress = false;
        _taskDragInitializing = false;
        _taskDragSourceIndex = -1;
        _taskInsertionIndex = -1;
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

    private void LocateTaskListScrollViewer()
    {
        _taskListScrollViewer = FindVisualChild<ScrollViewer>(TaskQueueListBox);
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

    private async void OnCopyScriptLogClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not GrassViewModel viewModel
            || viewModel.ScriptLogs.Count == 0)
        {
            return;
        }

        var text = string.Join(
            Environment.NewLine,
            viewModel.ScriptLogs.Select(entry =>
                $"{entry.Timestamp:HH:mm:ss.fff}\t{entry.Kind}\t{entry.Type}\t{entry.Details}"));
        await TrySetClipboardTextAsync(text);
    }

    private static async Task<bool> TrySetClipboardTextAsync(string text)
    {
        const int attempts = 5;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException) when (attempt + 1 < attempts)
            {
                await Task.Delay(75);
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        return false;
    }

    private void OnClearScriptLogClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is GrassViewModel viewModel)
            viewModel.ScriptLogs.Clear();
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
