using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.ViewModels;

namespace UmamusumeWpfGui.Views;

/// <summary>
/// Log view that displays timestamped Core callback events.
/// Auto-scrolls to the newest entry unless the user has manually scrolled up.
/// </summary>
public sealed partial class LogView : UserControl
{
    private ScrollViewer? _scrollViewer;
    private INotifyCollectionChanged? _subscribedCollection;
    private bool _isAtBottom = true;

    /// <summary>
    /// Creates the LogView and wires auto-scroll behavior.
    /// </summary>
    public LogView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LocateScrollViewer();
        SubscribeToCollection();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeFromCollection();
        UnsubscribeFromScrollViewer();
    }

    private void SubscribeToCollection()
    {
        UnsubscribeFromCollection(); // guard against double-subscribe
        if (EntriesListBox.ItemsSource is INotifyCollectionChanged collection)
        {
            _subscribedCollection = collection;
            collection.CollectionChanged += OnCollectionChanged;
        }
    }

    private void UnsubscribeFromCollection()
    {
        if (_subscribedCollection is not null)
        {
            _subscribedCollection.CollectionChanged -= OnCollectionChanged;
            _subscribedCollection = null;
        }
    }

    private void SubscribeToScrollViewer()
    {
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged += OnScrollChanged;
        }
    }

    private void UnsubscribeFromScrollViewer()
    {
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged -= OnScrollChanged;
            _scrollViewer = null;
        }
    }

    private void LocateScrollViewer()
    {
        UnsubscribeFromScrollViewer();
        _scrollViewer = FindVisualChild<ScrollViewer>(EntriesListBox);
        SubscribeToScrollViewer();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isAtBottom && _scrollViewer is not null
            && e.Action == NotifyCollectionChangedAction.Add)
        {
            _scrollViewer.ScrollToEnd();
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scrollViewer is null)
            return;

        // Determine if user is at the bottom (within 1px threshold)
        _isAtBottom =
            _scrollViewer.VerticalOffset
            >= _scrollViewer.ScrollableHeight - 1;
    }

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LogViewModel viewModel || viewModel.Entries.Count == 0)
            return;

        var text = string.Join(
            Environment.NewLine,
            viewModel.Entries.Select(FormatEntry));
        Clipboard.SetText(text);
    }

    private static string FormatEntry(LogEntry entry) =>
        $"{entry.Timestamp:HH:mm:ss.fff}\t{entry.Type}\t{entry.Details}";

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
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
