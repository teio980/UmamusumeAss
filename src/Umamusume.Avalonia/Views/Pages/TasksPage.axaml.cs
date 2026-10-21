using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Umamusume.Avalonia.ViewModels;

namespace Umamusume.Avalonia.Views.Pages;

public partial class TasksPage : UserControl
{
    private bool _isAttached;
    private bool _followTaskLog = true;
    private DispatcherOperation? _taskLogScrollOperation;

    public TasksPage()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            _isAttached = true;
            _followTaskLog = true;
            RequestTaskLogScroll();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _isAttached = false;
            _taskLogScrollOperation?.Abort();
            _taskLogScrollOperation = null;
        };
    }

    private void OnTaskLogScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
        {
            // New rows change the extent during layout; keep the previous follow state.
            // Clearing the log makes it fit again and starts a fresh follow session.
            if (TaskLogScrollViewer.Extent.Height <= TaskLogScrollViewer.Viewport.Height)
                _followTaskLog = true;
            RequestTaskLogScroll();
        }
        else if (e.OffsetDelta.Y != 0)
        {
            _followTaskLog = TaskLogScrollViewer.Offset.Y
                >= TaskLogScrollViewer.Extent.Height - TaskLogScrollViewer.Viewport.Height - 1;
        }
    }

    private void RequestTaskLogScroll()
    {
        if (!_isAttached || !_followTaskLog || _taskLogScrollOperation is not null) return;
        _taskLogScrollOperation = Dispatcher.UIThread.InvokeAsync(() =>
        {
            _taskLogScrollOperation = null;
            if (_isAttached && _followTaskLog) TaskLogScrollViewer.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void OnTaskDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is TasksViewModel vm && vm.SelectedTask is not null)
        {
            vm.OpenTaskSettings(vm.SelectedTask);
        }
    }
}
