using System.ComponentModel;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.Models.Input;
using RocoPilot.ViewModels;
using RocoPilot.Views.Windows;

namespace RocoPilot.Views;

public sealed partial class RealtimePage : Page
{
    private readonly IInterceptionDriverService _interceptionDriverService;

    private AutoBattleConfigWindow? _autoBattleConfigWindow;
    private AutoBattleOtherConfigWindow? _autoBattleOtherConfigWindow;
    private SpiritCatalogWindow? _spiritCatalogWindow;
    private AutoBattleKeyboardInputMethodOption? _confirmedKeyboardInputMethodOption;
    private bool _isKeyboardInputMethodSelectionReady;
    private bool _isRestoringKeyboardInputMethodSelection;

    public RealtimeViewModel ViewModel
    {
        get;
    }

    public RealtimePage()
    {
        ViewModel = App.GetService<RealtimeViewModel>();
        _interceptionDriverService = App.GetService<IInterceptionDriverService>();
        InitializeComponent();
        Loaded += RealtimePage_Loaded;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private async void RealtimePage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= RealtimePage_Loaded;
        await ViewModel.LoadAsync();
        _confirmedKeyboardInputMethodOption = ViewModel.SelectedAutoBattleKeyboardInputMethodOption;
        _isKeyboardInputMethodSelectionReady = true;
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

    private void ConfigureAutoBattleOtherButton_Click(object sender, RoutedEventArgs e)
    {
        if (_autoBattleOtherConfigWindow is not null)
        {
            _autoBattleOtherConfigWindow.Activate();
            return;
        }

        _autoBattleOtherConfigWindow = new AutoBattleOtherConfigWindow(ViewModel);
        _autoBattleOtherConfigWindow.Closed += (_, _) => _autoBattleOtherConfigWindow = null;
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

    private async void AutoBattleKeyboardInputMethodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isKeyboardInputMethodSelectionReady
            || _isRestoringKeyboardInputMethodSelection
            || AutoBattleKeyboardInputMethodComboBox.SelectedItem is not AutoBattleKeyboardInputMethodOption selectedOption)
        {
            return;
        }

        if (selectedOption.Method != KeyboardInputMethod.Interception)
        {
            _confirmedKeyboardInputMethodOption = selectedOption;
            return;
        }

        if (_interceptionDriverService.IsDriverInstalled())
        {
            _confirmedKeyboardInputMethodOption = selectedOption;
            return;
        }

        var fallbackOption = _confirmedKeyboardInputMethodOption?.Method == KeyboardInputMethod.Interception
            ? FindKeyboardInputMethodOption(KeyboardInputMethod.PostMessage)
            : _confirmedKeyboardInputMethodOption ?? FindKeyboardInputMethodOption(KeyboardInputMethod.PostMessage);

        AutoBattleKeyboardInputMethodComboBox.IsEnabled = false;
        try
        {
            var installed = await InterceptionDriverInstallDialog.EnsureInstalledAsync(
                XamlRoot,
                _interceptionDriverService);

            if (installed)
            {
                _confirmedKeyboardInputMethodOption = selectedOption;
                return;
            }

            RestoreKeyboardInputMethodSelection(fallbackOption);
        }
        finally
        {
            AutoBattleKeyboardInputMethodComboBox.IsEnabled = true;
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

    private AutoBattleKeyboardInputMethodOption? FindKeyboardInputMethodOption(KeyboardInputMethod method)
    {
        return ViewModel.AutoBattleKeyboardInputMethodOptions.FirstOrDefault(option => option.Method == method);
    }

    private void RestoreKeyboardInputMethodSelection(AutoBattleKeyboardInputMethodOption? option)
    {
        if (option is null)
        {
            return;
        }

        _isRestoringKeyboardInputMethodSelection = true;
        try
        {
            ViewModel.SelectedAutoBattleKeyboardInputMethodOption = option;
            _confirmedKeyboardInputMethodOption = option;
        }
        finally
        {
            _isRestoringKeyboardInputMethodSelection = false;
        }
    }
}
