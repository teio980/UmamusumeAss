using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using Umamusume.Avalonia.ViewModels;

namespace Umamusume.Avalonia.Views.Pages;

public partial class LogsPage : UserControl
{
    private LogsViewModel? _subscribed;
    private bool _scrollQueued;

    public LogsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        Unsubscribe();
        _subscribed = DataContext as LogsViewModel;
        if (_subscribed is not null) _subscribed.FilteredLogs.CollectionChanged += OnLogsChanged;
    }

    private void Unsubscribe()
    {
        if (_subscribed is not null) _subscribed.FilteredLogs.CollectionChanged -= OnLogsChanged;
        _subscribed = null;
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_scrollQueued || _subscribed?.AutoScroll != true) return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (_subscribed is { AutoScroll: true, FilteredLogs.Count: > 0 } vm)
                LogList.ScrollIntoView(vm.FilteredLogs[0]);
        }, DispatcherPriority.Background);
    }
}
