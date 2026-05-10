using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.ViewModels;
using RocoPilot.Views.Windows;

namespace RocoPilot.Views;

public sealed partial class RealtimePage : Page
{
    private AutoBattleConfigWindow? _autoBattleConfigWindow;
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
    }

    private async void RealtimePage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= RealtimePage_Loaded;
        await ViewModel.LoadAsync();
    }

    private void ConfigureAutoBattleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_autoBattleConfigWindow is not null)
        {
            _autoBattleConfigWindow.Activate();
            return;
        }

        _autoBattleConfigWindow = new AutoBattleConfigWindow(ViewModel);
        _autoBattleConfigWindow.Closed += (_, _) => _autoBattleConfigWindow = null;
        _autoBattleConfigWindow.Activate();
    }

    private void ViewSpiritCatalogButton_Click(object sender, RoutedEventArgs e)
    {
        if (_spiritCatalogWindow is not null)
        {
            _spiritCatalogWindow.Activate();
            return;
        }

        _spiritCatalogWindow = new SpiritCatalogWindow();
        _spiritCatalogWindow.Closed += (_, _) => _spiritCatalogWindow = null;
        _spiritCatalogWindow.Activate();
    }

    private async void SyncSpiritCatalogButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SyncSpiritCatalogAsync();
        if (_spiritCatalogWindow is not null)
        {
            await _spiritCatalogWindow.ReloadAsync();
        }
    }
}
