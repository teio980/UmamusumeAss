using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Umamusume.Avalonia.ViewModels;

namespace Umamusume.Avalonia.Views.Pages;

public partial class LogsPage : UserControl
{
    private LogsViewModel? _subscribed;
    private bool _isAttached;
    private DispatcherOperation? _scrollOperation;

    public LogsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) =>
        {
            _isAttached = true;
            Subscribe();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _isAttached = false;
            Unsubscribe();
        };
    }

    private void Subscribe()
    {
        Unsubscribe();
        if (!_isAttached) return;
        _subscribed = DataContext as LogsViewModel;
        if (_subscribed is not null)
        {
            _subscribed.FilteredLogs.CollectionChanged += OnLogsChanged;
            _subscribed.PropertyChanged += OnViewModelPropertyChanged;
            RequestScrollToEnd();
        }
    }

    private void Unsubscribe()
    {
        _scrollOperation?.Abort();
        _scrollOperation = null;
        if (_subscribed is not null)
        {
            _subscribed.FilteredLogs.CollectionChanged -= OnLogsChanged;
            _subscribed.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _subscribed = null;
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RequestScrollToEnd();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogsViewModel.AutoScroll)) RequestScrollToEnd();
    }

    private void RequestScrollToEnd()
    {
        if (_scrollOperation is not null || _subscribed?.AutoScroll != true) return;
        _scrollOperation = Dispatcher.UIThread.InvokeAsync(() =>
        {
            _scrollOperation = null;
            if (_subscribed is { AutoScroll: true, FilteredLogs.Count: > 0 } vm)
                LogList.ScrollIntoView(vm.FilteredLogs[^1]);
        }, DispatcherPriority.Background);
    }
}
