using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace UmamusumeWpfGui.Views;





public sealed partial class GrassView : UserControl
{
    private ScrollViewer? _scrollViewer;
    private INotifyCollectionChanged? _subscribedCollection;
    private bool _isAtBottom = true;
    private bool _scrollRequestPending;
    private int _viewGeneration;

    public GrassView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewGeneration++;
        _scrollRequestPending = false;
        LocateScrollViewer();
        SubscribeToCollection();
        RequestScrollToEnd(DispatcherPriority.Loaded);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewGeneration++;
        _scrollRequestPending = false;
        UnsubscribeFromCollection();
        UnsubscribeFromScrollViewer();
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
