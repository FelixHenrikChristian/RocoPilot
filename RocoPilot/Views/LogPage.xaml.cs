using System.Collections.Specialized;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

using RocoPilot.ViewModels;

namespace RocoPilot.Views;

public sealed partial class LogPage : Page
{
    private const double BottomTolerance = 24;

    private ScrollViewer? _logScrollViewer;
    private bool _isActive;
    private bool _followNewEntries = true;
    private bool _scrollToBottomQueued;

    public LogViewModel ViewModel
    {
        get;
    }

    public LogPage()
    {
        ViewModel = App.GetService<LogViewModel>();
        InitializeComponent();

        LogListView.Loaded += LogListView_Loaded;
        LogListView.Unloaded += LogListView_Unloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isActive = true;
        _followNewEntries = true;
        ViewModel.Attach(DispatcherQueue);
        ViewModel.Entries.CollectionChanged += Entries_CollectionChanged;
        TryAttachLogScrollViewer();
        RequestScrollToBottom();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _isActive = false;
        ViewModel.Entries.CollectionChanged -= Entries_CollectionChanged;
        DetachLogScrollViewer();
        ViewModel.Detach();
        base.OnNavigatedFrom(e);
    }

    private void LogListView_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        TryAttachLogScrollViewer();

        if (_followNewEntries)
        {
            RequestScrollToBottom();
        }
    }

    private void LogListView_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        DetachLogScrollViewer();
    }

    private void TryAttachLogScrollViewer()
    {
        if (!_isActive)
        {
            return;
        }

        if (_logScrollViewer != null)
        {
            return;
        }

        var scrollViewer = FindDescendant<ScrollViewer>(LogListView);
        if (scrollViewer == null)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, TryAttachLogScrollViewer);
            return;
        }

        _logScrollViewer = scrollViewer;
        _logScrollViewer.ViewChanged += LogScrollViewer_ViewChanged;
        UpdateFollowNewEntries();
    }

    private void DetachLogScrollViewer()
    {
        if (_logScrollViewer == null)
        {
            return;
        }

        _logScrollViewer.ViewChanged -= LogScrollViewer_ViewChanged;
        _logScrollViewer = null;
    }

    private void LogScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateFollowNewEntries();
    }

    private void Entries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_followNewEntries || e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }

        RequestScrollToBottom();
    }

    private void RequestScrollToBottom()
    {
        if (!_isActive || _scrollToBottomQueued)
        {
            return;
        }

        _scrollToBottomQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _scrollToBottomQueued = false;
            if (!_isActive)
            {
                return;
            }

            ScrollToBottom();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_isActive && _followNewEntries)
                {
                    ScrollToBottom();
                }
            });
        });
    }

    private void ScrollToBottom()
    {
        if (!_isActive || ViewModel.Entries.Count == 0)
        {
            return;
        }

        LogListView.ScrollIntoView(ViewModel.Entries[^1]);

        if (_logScrollViewer != null)
        {
            _logScrollViewer.ChangeView(null, _logScrollViewer.ScrollableHeight, null, disableAnimation: true);
            _followNewEntries = true;
        }
    }

    private void UpdateFollowNewEntries()
    {
        if (_logScrollViewer == null)
        {
            _followNewEntries = true;
            return;
        }

        _followNewEntries = _logScrollViewer.ScrollableHeight - _logScrollViewer.VerticalOffset <= BottomTolerance;
    }

    private static T? FindDescendant<T>(Microsoft.UI.Xaml.DependencyObject root)
        where T : Microsoft.UI.Xaml.DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindDescendant<T>(child);
            if (descendant != null)
            {
                return descendant;
            }
        }

        return null;
    }
}
