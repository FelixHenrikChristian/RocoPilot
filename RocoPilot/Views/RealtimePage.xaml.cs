using System.ComponentModel;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.Helpers;
using RocoPilot.ViewModels;
using RocoPilot.Views.Windows;
using RocoPilot.Views.Windows.AutoBattleConfigPages;

namespace RocoPilot.Views;

public sealed partial class RealtimePage : Page
{
    private AutoBattleOtherConfigWindow? _autoBattleOtherConfigWindow;
    private SpiritCatalogWindow? _spiritCatalogWindow;

    public RealtimeViewModel ViewModel
    {
        get;
    }

    public RealtimePage()
    {
        ViewModel = App.GetService<RealtimeViewModel>();
        InitializeComponent();
        Loaded += RealtimePage_Loaded;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private async void RealtimePage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= RealtimePage_Loaded;
        await ViewModel.LoadAsync();
    }

    private async void ConfigureAutoBattleButton_Click(object sender, RoutedEventArgs e)
    {
        await AutoBattleConfigWindow.ShowAsync(AutoBattleConfigSection.SharedSequences);
    }

    private async void ConfigureAutoBattleOtherButton_Click(object sender, RoutedEventArgs e)
    {
        var runtime = App.GetService<IRuntimeTaskService>();
        await runtime.LoadSettingsAsync();
        if (_autoBattleOtherConfigWindow is not null)
        {
            _autoBattleOtherConfigWindow.Activate();
            return;
        }

        _autoBattleOtherConfigWindow = new AutoBattleOtherConfigWindow(runtime);
        _autoBattleOtherConfigWindow.Closed += (_, _) => _autoBattleOtherConfigWindow = null;
        WindowPlacementHelper.SetOwner(_autoBattleOtherConfigWindow, App.MainWindow);
        WindowPlacementHelper.CenterOnParent(_autoBattleOtherConfigWindow, App.MainWindow);
        _autoBattleOtherConfigWindow.Activate();
    }

    private async void ViewSpiritCatalogButton_Click(object sender, RoutedEventArgs e)
    {
        if (_spiritCatalogWindow is not null)
        {
            await _spiritCatalogWindow.SetSourceAsync(ViewModel.SelectedSpiritCatalogSourceId);
            _spiritCatalogWindow.Activate();
            return;
        }

        _spiritCatalogWindow = new SpiritCatalogWindow(ViewModel.SelectedSpiritCatalogSourceId);
        _spiritCatalogWindow.Closed += (_, _) => _spiritCatalogWindow = null;
        WindowPlacementHelper.SetOwner(_spiritCatalogWindow, App.MainWindow);
        WindowPlacementHelper.CenterOnParent(_spiritCatalogWindow, App.MainWindow);
        _spiritCatalogWindow.Activate();
    }

    private async void SyncSpiritCatalogButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SyncSpiritCatalogAsync();
        if (_spiritCatalogWindow is not null)
        {
            await _spiritCatalogWindow.SetSourceAsync(ViewModel.SelectedSpiritCatalogSourceId);
            await _spiritCatalogWindow.ReloadAsync();
        }
    }

    private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RealtimeViewModel.SelectedSpiritCatalogSource)
            && _spiritCatalogWindow is not null)
        {
            await _spiritCatalogWindow.SetSourceAsync(ViewModel.SelectedSpiritCatalogSourceId);
        }
    }
}
