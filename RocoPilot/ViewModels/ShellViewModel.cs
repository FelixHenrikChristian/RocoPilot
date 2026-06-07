using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;

using RocoPilot.Contracts.Services;
using RocoPilot.Contracts.Services.Statistics;
using RocoPilot.Views;

namespace RocoPilot.ViewModels;

public partial class ShellViewModel : ObservableRecipient
{
    [ObservableProperty]
    public partial bool IsBackEnabled { get; set; }

    [ObservableProperty]
    public partial object? Selected { get; set; }

    private readonly IStatisticsService _statisticsService;
    private readonly DispatcherQueue? _dispatcherQueue;
    private int _pendingShinyCount;

    public INavigationService NavigationService
    {
        get;
    }

    public INavigationViewService NavigationViewService
    {
        get;
    }

    public int PendingShinyCount
    {
        get => _pendingShinyCount;
        private set
        {
            if (SetProperty(ref _pendingShinyCount, value))
            {
                OnPropertyChanged(nameof(PendingShinyBadgeVisibility));
            }
        }
    }

    public Visibility PendingShinyBadgeVisibility => PendingShinyCount > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public ShellViewModel(
        INavigationService navigationService,
        INavigationViewService navigationViewService,
        IStatisticsService statisticsService)
    {
        NavigationService = navigationService;
        NavigationService.Navigated += OnNavigated;
        NavigationViewService = navigationViewService;
        _statisticsService = statisticsService;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _statisticsService.DocumentChanged += StatisticsService_DocumentChanged;
        _statisticsService.SelectedAccountChanged += StatisticsService_SelectedAccountChanged;
        RefreshPendingShinyCount();
        _ = LoadPendingShinyCountAsync();
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        IsBackEnabled = NavigationService.CanGoBack;

        if (e.SourcePageType == typeof(SettingsPage))
        {
            Selected = NavigationViewService.SettingsItem;
            return;
        }

        var selectedItem = NavigationViewService.GetSelectedItem(e.SourcePageType);
        if (selectedItem != null)
        {
            Selected = selectedItem;
        }
    }

    private void StatisticsService_DocumentChanged(object? sender, StatisticsDocumentChangedEventArgs e)
    {
        QueueRefreshPendingShinyCount();
    }

    private void StatisticsService_SelectedAccountChanged(object? sender, EventArgs e)
    {
        QueueRefreshPendingShinyCount();
    }

    private void QueueRefreshPendingShinyCount()
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            RefreshPendingShinyCount();
            return;
        }

        _dispatcherQueue.TryEnqueue(RefreshPendingShinyCount);
    }

    private void RefreshPendingShinyCount()
    {
        PendingShinyCount = _statisticsService.GetSelectedAccountPendingShinyCaptures().Count;
    }

    private async Task LoadPendingShinyCountAsync()
    {
        await _statisticsService.LoadAsync();
        QueueRefreshPendingShinyCount();
    }
}
